using Windows.Media.SpeechRecognition;
using RealtimeSubtitle.Core.Diagnostics;
using RealtimeSubtitle.Core.Speech;

namespace RealtimeSubtitle.Windows;

/// <summary>
/// 默认 ASR 后端（决策 P2-3/P5-5）：传统的 Windows.Media.SpeechRecognition API
/// （"Windows Speech Recognition"）。按设计流式处理：在用户仍在说话时 HypothesisGenerated 引发 Partial，
/// ContinuousRecognitionSession.ResultGenerated 每话语引发 Final。
/// 消费默认麦克风（此 API 没有回环/自定义 PCM 输入，
/// 因此当此后端激活时绕过回环捕获路径）。
///
/// 封装了与 Rick Strahl 的 VoiceDictation 包装器（Markdown Monster）相同的形状：
/// SpeechRecognizer(Language) + 听写主题约束 + 持续会话事件。
/// </summary>
public sealed class LegacySpeechRecognizer : ISpeechRecognizer
{
    private readonly LogSink _log;
    private readonly string? _language; // 例如 "en-US", "zh-CN"；null/"auto" = 系统语言
    private SpeechRecognizer? _recognizer;
    private bool _started;

    public LegacySpeechRecognizer(string? language = null, LogSink? log = null)
    {
        _log = log ?? LogSink.Default;
        _language = language;
    }

    public event Action<AsrPartial>? Partial;
    public event Action<AsrFinal>? Final;
    public event Action<Exception>? Error;

    public void WritePcm16(ReadOnlySpan<byte> pcmBytes, DateTimeOffset timestamp)
    {
        // 传统 API 无法消耗原始 PCM 推送；忽略（基于麦克风）。
    }

    public void Start(CancellationToken token)
    {
        if (_started) return;
        _started = true;

        try
        {
            _recognizer = CreateRecognizer();
            _recognizer.HypothesisGenerated += OnHypothesis; // streaming partials
            _recognizer.ContinuousRecognitionSession.ResultGenerated += OnResult; // finals
            _recognizer.ContinuousRecognitionSession.Completed += OnSessionCompleted;

            var constraint = new SpeechRecognitionTopicConstraint(SpeechRecognitionScenario.Dictation, "dictation");
            _recognizer.Constraints.Add(constraint);
            var compilation = _recognizer.CompileConstraintsAsync().AsTask().GetAwaiter().GetResult();
            if (compilation.Status != SpeechRecognitionResultStatus.Success)
            {
                throw new InvalidOperationException($"Windows speech recognition compile failed: {compilation.Status}");
            }

            _recognizer.ContinuousRecognitionSession.StartAsync().AsTask(token).GetAwaiter().GetResult();
            _log.Info("Windows speech recognition started (microphone, language={0}).", _language ?? "system");
        }
        catch (Exception ex)
        {
            _started = false;
            _log.Error("Windows speech recognition start failed: {0}", ex.Message);
            Error?.Invoke(ex);
        }
    }

    public void Stop()
    {
        if (!_started) return;
        _started = false;

        try
        {
            _recognizer?.ContinuousRecognitionSession.StopAsync().AsTask().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _log.Warn("Windows speech recognition stop failed: {0}", ex.Message);
        }

        if (_recognizer is not null)
        {
            _recognizer.HypothesisGenerated -= OnHypothesis;
            _recognizer.ContinuousRecognitionSession.ResultGenerated -= OnResult;
            _recognizer.ContinuousRecognitionSession.Completed -= OnSessionCompleted;
        }
    }

    public void Dispose()
    {
        Stop();
        _recognizer?.Dispose();
        _recognizer = null;
    }

    private SpeechRecognizer CreateRecognizer()
    {
        if (string.IsNullOrWhiteSpace(_language) || _language == "auto")
        {
            return new SpeechRecognizer();
        }

        try
        {
            return new SpeechRecognizer(new global::Windows.Globalization.Language(_language));
        }
        catch (Exception ex)
        {
            _log.Warn("Speech language '{0}' not available ({1}); using system language.", _language, ex.Message);
            return new SpeechRecognizer();
        }
    }

    private void OnHypothesis(SpeechRecognizer sender, SpeechRecognitionHypothesisGeneratedEventArgs args)
    {
        if (args.Hypothesis?.Text is { Length: > 0 } text)
        {
            Partial?.Invoke(new AsrPartial(text, DateTimeOffset.Now));
        }
    }

    private void OnResult(SpeechContinuousRecognitionSession sender, SpeechContinuousRecognitionResultGeneratedEventArgs args)
    {
        if (args.Result?.Text is { Length: > 0 } text)
        {
            Final?.Invoke(new AsrFinal(text, DateTimeOffset.Now));
        }
    }

    private void OnSessionCompleted(SpeechContinuousRecognitionSession sender, SpeechContinuousRecognitionCompletedEventArgs args)
    {
        _log.Debug("Windows speech recognition session completed (status={0}).", args.Status);
    }
}
