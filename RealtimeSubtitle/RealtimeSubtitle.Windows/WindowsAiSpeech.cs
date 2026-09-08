using System.Runtime.InteropServices;
using Microsoft.Windows.AI;
using Microsoft.Windows.AI.Speech;
using RealtimeSubtitle.Core.Diagnostics;
using RealtimeSubtitle.Core.Speech;

namespace RealtimeSubtitle.Windows;

/// <summary>
/// Windows AI 语音模型生命周期管理器（Microsoft.Windows.AI.Speech.SpeechRecognitionModel）。
/// 实现了 v3.0 计划（F2/F3）中的 GetReadyState 四分支流程：
/// Ready / NotReady（同意+下载）/ DisabledByUser / NotSupportedOnCurrentSystem。
/// </summary>
public sealed class SpeechModelManager : ISpeechModelManager
{
    private readonly LogSink _log;

    public SpeechModelManager(LogSink? log = null)
    {
        _log = log ?? LogSink.Default;
    }

    public AsrModelReadyState GetReadyState() => TryGetReadyState(out _);

    /// <summary>
    /// 读取模型就绪状态而不抛出异常（已知问题：在某些 CPU 上 AI 语音引擎会因 0x8A1F022F / 0xC000001D 崩溃——参见 microsoft/WindowsAppSDK#6561）。
    /// 失败会被记录并报告为 <see cref="AsrModelReadyState.Unknown"/>，保持管道可用。
    /// </summary>
    public AsrModelReadyState TryGetReadyState(out string? error)
    {
        try
        {
            error = null;
            return Map(SpeechRecognitionModel.GetReadyState());
        }
        catch (Exception ex)
        {
            error = ex is COMException ce ? $"0x{ce.HResult:X8}" : ex.GetType().Name;
            _log.Warn("SpeechRecognitionModel.GetReadyState failed ({0}): {1}", error, ex.Message);
            return AsrModelReadyState.Unknown;
        }
    }

    public bool RequiresConsent => GetReadyState() == AsrModelReadyState.NotReady;

    public async Task EnsureReadyAsync(IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        var state = TryGetReadyState(out _);
        if (state == AsrModelReadyState.Ready)
        {
            return;
        }

        _log.Info("Ensuring speech model ready (state={0})...", state);
        var result = await SpeechRecognitionModel.EnsureReadyAsync().AsTask(cancellationToken);
        progress?.Report(1.0);

        if (result.Status != AIFeatureReadyResultState.Success)
        {
            throw result.ExtendedError ?? new InvalidOperationException(
                $"Speech model preparation failed with status {result.Status}.");
        }

        _log.Info("Speech model ready after ensure.");
    }

    public ISpeechRecognizer CreateRecognizer()
    {
        var state = GetReadyState();
        if (state != AsrModelReadyState.Ready)
        {
            throw new InvalidOperationException(
                $"Cannot create a recognizer in state {state}. Call EnsureReadyAsync first.");
        }

        var creation = SpeechRecognitionModel.TryCreateAsync().AsTask().GetAwaiter().GetResult();
        if (creation.SpeechModel is null)
        {
            throw creation.ExtendedError ?? new InvalidOperationException("SpeechRecognitionModel creation failed.");
        }

        return new WindowsAiSpeechRecognizer(creation.SpeechModel, _log);
    }

    private static AsrModelReadyState Map(AIFeatureReadyState s) => s switch
    {
        AIFeatureReadyState.Ready => AsrModelReadyState.Ready,
        AIFeatureReadyState.NotReady => AsrModelReadyState.NotReady,
        AIFeatureReadyState.DisabledByUser => AsrModelReadyState.DisabledByUser,
        AIFeatureReadyState.NotSupportedOnCurrentSystem => AsrModelReadyState.NotSupportedOnCurrentSystem,
        _ => AsrModelReadyState.Unknown,
    };
}

/// <summary>
/// 基于 Windows AI 语音 API 的流式识别器。音频通过 <see cref="WritePcm16"/> 以 16 kHz / 16 位 / 单声道 PCM 到达，
/// 并被推送到 <see cref="SpeechAudioProvider"/>。
/// 部分结果（Recognizing）引发 <see cref="Partial"/>，最终结果引发 <see cref="Final"/>。
/// </summary>
public sealed class WindowsAiSpeechRecognizer : ISpeechRecognizer
{
    private readonly SpeechRecognitionModel _model;
    private readonly SpeechAudioProvider _provider;
    private readonly StreamingRecognition _recognition;
    private readonly LogSink _log;
    private readonly CancellationTokenSource _lifetime = new();

    public WindowsAiSpeechRecognizer(SpeechRecognitionModel model, LogSink? log = null)
    {
        _model = model;
        _log = log ?? LogSink.Default;
        _provider = new SpeechAudioProvider();
        var audioConfig = AudioConfiguration.ForProvider(_provider);
        _recognition = new StreamingRecognition(audioConfig, _model);

        _recognition.Recognizing += OnRecognizing;
        _recognition.Recognized += OnRecognized;
    }

    public event Action<AsrPartial>? Partial;
    public event Action<AsrFinal>? Final;
    public event Action<Exception>? Error;

    public void WritePcm16(ReadOnlySpan<byte> pcmBytes, DateTimeOffset timestamp)
    {
        // SpeechAudioProvider.PushData 接受托管 byte[]；从池化 span 转换。
        byte[] copy = pcmBytes.ToArray();
        _provider.PushData(copy);
    }

    public void Start(CancellationToken token)
    {
        token.Register(() => _lifetime.Cancel());
        _log.Info("Starting continuous recognition...");
        _recognition.StartContinuousRecognitionAsync().AsTask(_lifetime.Token).GetAwaiter().GetResult();
        _log.Info("Continuous recognition started.");
    }

    public void Stop()
    {
        try
        {
            _recognition.StopContinuousRecognition();
        }
        catch (Exception ex)
        {
            _log.Warn("StopContinuousRecognition failed: {0}", ex.Message);
        }
    }

    public void Dispose()
    {
        _recognition.Recognizing -= OnRecognizing;
        _recognition.Recognized -= OnRecognized;
        Stop();
        _recognition.Dispose();
        _provider.Dispose();
        _model.Dispose();
        _lifetime.Cancel();
        _lifetime.Dispose();
    }

    private void OnRecognizing(StreamingRecognition sender, StreamingRecognizingEventArgs args)
    {
        Partial?.Invoke(new AsrPartial(args.Text, DateTimeOffset.Now));
    }

    private void OnRecognized(StreamingRecognition sender, StreamingRecognizedEventArgs args)
    {
        Final?.Invoke(new AsrFinal(args.Text, DateTimeOffset.Now));
    }
}

/// <summary>
/// 诊断专用：检查模型就绪状态并尝试一次有界准备语音模型，写入结构化报告。
/// 由 --ai-setup 探针使用，以决定 Windows AI 语音后端是否可用于此硬件，或必须交换为 Whisper。
/// </summary>
public static class SpeechSetupProbe
{
    public static async Task<string> RunAsync(CancellationToken token = default)
    {
        var sb = new System.Text.StringBuilder();
        var manager = new SpeechModelManager();

        AsrModelReadyState state = manager.TryGetReadyState(out string? err);
        sb.AppendLine($"BEFORE_STATE={state}");
        sb.AppendLine($"BEFORE_ERROR={err ?? "-"}");

        if (state == AsrModelReadyState.Ready)
        {
            sb.AppendLine("ENSURE=SKIPPED_ALREADY_READY");
            return sb.ToString();
        }

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            cts.CancelAfter(TimeSpan.FromMinutes(8));
            await manager.EnsureReadyAsync(cancellationToken: cts.Token);
            sb.AppendLine("ENSURE=SUCCESS");
        }
        catch (OperationCanceledException)
        {
            sb.AppendLine("ENSURE=TIMEOUT_OR_CANCELLED_8MIN");
        }
        catch (Exception ex)
        {
            string hr = ex is COMException ce ? $"0x{ce.HResult:X8}" : "-";
            sb.AppendLine($"ENSURE=FAILED HR={hr} TYPE={ex.GetType().Name} MSG={ex.Message}");
        }

        AsrModelReadyState after = manager.TryGetReadyState(out string? errAfter);
        sb.AppendLine($"AFTER_STATE={after}");
        sb.AppendLine($"AFTER_ERROR={errAfter ?? "-"}");
        return sb.ToString();
    }
}
public static class SpeechProbe
{
    /// <summary>通过批量识别转录 WAV；返回转录文本和经过的时间。</summary>
    public static async Task<(string Transcript, TimeSpan Elapsed, AsrModelReadyState ReadyState)> RunBatchAsync(
        string wavPath, CancellationToken token = default)
    {
        var manager = new SpeechModelManager();
        var ready = manager.GetReadyState();
        if (ready != AsrModelReadyState.Ready)
        {
            await manager.EnsureReadyAsync(cancellationToken: token);
        }

        using var model = CreateModel();
        var batch = new BatchRecognition(model);
        DateTimeOffset start = DateTimeOffset.UtcNow;
        string transcript = await batch.RecognizeFromFile(wavPath).AsTask(token);
        return (transcript, DateTimeOffset.UtcNow - start, manager.GetReadyState());
    }

    internal static SpeechRecognitionModel CreateModel()
    {
        var creation = SpeechRecognitionModel.TryCreateAsync().AsTask().GetAwaiter().GetResult();
        if (creation.SpeechModel is null)
        {
            throw creation.ExtendedError ?? new InvalidOperationException("SpeechRecognitionModel creation failed.");
        }

        return creation.SpeechModel;
    }
}