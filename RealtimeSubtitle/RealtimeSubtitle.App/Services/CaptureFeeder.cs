using RealtimeSubtitle.Core.Audio;
using RealtimeSubtitle.Core.Configuration;
using RealtimeSubtitle.Core.Diagnostics;
using RealtimeSubtitle.Core.Speech;
using RealtimeSubtitle.Windows;

namespace RealtimeSubtitle.App.Services;

/// <summary>
/// Feeds the loopback capturer's mix-format stream through down-mix + resample + VAD into
/// an <see cref="ISpeechRecognizer"/> as 16 kHz mono PCM16 — the "true sound" front end.
/// Reconnects the capturer with backoff when the device is lost.
/// </summary>
public sealed class CaptureFeeder : IDisposable
{
    private readonly IAudioCapturer _capturer;
    private readonly ISpeechRecognizer _recognizer;
    private readonly VadConfig _vadConfig;
    private readonly LogSink _log;
    private readonly CancellationTokenSource _lifetime = new();
    private IAudioResampler? _resampler;
    private Task? _pipeline;
    private int _reconnects;

    // Health counters so a silent live session (no subtitles) is diagnosable from the log alone.
    private long _samplesFed;
    private float _peakRms;
    private int _emptyReads;
    private long _lastHealthLogTicks = Environment.TickCount64;

    public CaptureFeeder(IAudioCapturer capturer, ISpeechRecognizer recognizer, VadConfig vadConfig, LogSink log)
    {
        _capturer = capturer;
        _recognizer = recognizer;
        _vadConfig = vadConfig;
        _log = log;
    }

    public void Start(CancellationToken token)
    {
        // capturer.Format is only known after Start() — build the pipeline pieces here.
        _resampler = new AudioResampler(_capturer.Format.Channels, _capturer.Format.SampleRate, outputSampleRate: 16000);

        var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        _pipeline = Task.Run(() => PipelineLoop(linked.Token));
    }

    private async Task PipelineLoop(CancellationToken token)
    {
        var resampler = _resampler!;
        try
        {
            int mixRate = _capturer.Format.SampleRate;
            int channels = _capturer.Format.Channels;
            int chunkSamples = Math.Max(1, mixRate / 100) * channels;
            var input = new float[chunkSamples];
            var resampled = new float[1024];
            var pcm = new byte[2048];

            while (!token.IsCancellationRequested)
            {
                int n = _capturer.Buffer.TryRead(input);
                if (n == 0)
                {
                    _emptyReads++;
                    await Task.Delay(2, token);
                    MaybeLogHealth();
                    continue;
                }

                int m = resampler.Process(input.AsSpan(0, n), resampled);
                // NOTE: VAD gating happens inside the recognizer (it owns a VoiceActivityDetector
                // built from the same VadConfig, P6-11). No feed-side VAD here — a second VAD
                // whose result was discarded just wasted CPU and confused the config wiring.

                float chunkPeak = 0f;
                for (int i = 0; i < m; i++)
                {
                    float s = Math.Clamp(resampled[i], -1f, 1f);
                    float abs = Math.Abs(s);
                    if (abs > chunkPeak) chunkPeak = abs;
                    short sample = (short)(s * short.MaxValue);
                    pcm[i * 2] = (byte)(sample & 0xFF);
                    pcm[i * 2 + 1] = (byte)((sample >> 8) & 0xFF);
                }

                _samplesFed += m;
                if (chunkPeak > _peakRms) _peakRms = chunkPeak;
                _recognizer.WritePcm16(pcm.AsSpan(0, m * 2), DateTimeOffset.UtcNow);
                MaybeLogHealth();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _log.Error("Capture pipeline crashed: {0}", ex);
        }
    }

    /// <summary>
    /// Every ~5 s, report whether the loopback is actually delivering audio. A live session
    /// with zero subtitles often means the render endpoint was idle (no game/video playing)
    /// or the mix was silent — this line makes that visible instead of a silent hang.
    /// </summary>
    private void MaybeLogHealth()
    {
        long now = Environment.TickCount64;
        if (now - _lastHealthLogTicks < 5000) return;
        _lastHealthLogTicks = now;

        long samples = Interlocked.Read(ref _samplesFed);
        float peak = _peakRms;
        int empty = _emptyReads;
        _peakRms = 0f;

        if (samples == 0)
        {
            _log.Warn(
                "Capture idle: no loopback PCM in the last 5 s ({0} empty reads). " +
                "默认渲染端点无应用在出声时不会产生回环数据 — 请确认游戏/视频正在播放且未静音。",
                empty);
        }
        else
        {
            _log.Info("Capture health: fed {0} samples ({1:F1}s @16k), peak amplitude {2:F3}, empty reads {3}.",
                samples, samples / 16000.0, peak, empty);
        }

        _emptyReads = 0;
        Interlocked.Exchange(ref _samplesFed, 0);
    }

    /// <summary>Handles capturer device-lost events with a bounded reconnect loop.</summary>
    public void OnCapturerError(Exception ex)
    {
        _log.Warn("Capturer error ({0}); reconnecting ({1}/3)...", ex.Message, _reconnects + 1);
        if (_reconnects >= 3)
        {
            _log.Error("Capturer gave up after repeated device failures.");
            return;
        }

        _reconnects++;
        _ = Task.Run(async () =>
        {
            try
            {
                _capturer.Stop();
                await Task.Delay(1000 * _reconnects);
                _capturer.Start(_lifetime.Token);
                _log.Info("Capturer reconnected.");
            }
            catch (Exception reconnectEx)
            {
                _log.Error("Reconnect failed: {0}", reconnectEx.Message);
            }
        });
    }

    /// <summary>Stops only the feed pipeline; the capturer keeps running (used by ASR hot-swap).</summary>
    public void StopPipeline()
    {
        try { _lifetime.Cancel(); } catch { }
        _pipeline?.Wait(1000);
    }

    public void Dispose()
    {
        StopPipeline();
        _capturer.Stop();
        _lifetime.Dispose();
    }
}