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
                    await Task.Delay(2, token);
                    continue;
                }

                int m = resampler.Process(input.AsSpan(0, n), resampled);
                // NOTE: VAD gating happens inside the recognizer (it owns a VoiceActivityDetector
                // built from the same VadConfig, P6-11). No feed-side VAD here — a second VAD
                // whose result was discarded just wasted CPU and confused the config wiring.

                for (int i = 0; i < m; i++)
                {
                    float s = Math.Clamp(resampled[i], -1f, 1f);
                    short sample = (short)(s * short.MaxValue);
                    pcm[i * 2] = (byte)(sample & 0xFF);
                    pcm[i * 2 + 1] = (byte)((sample >> 8) & 0xFF);
                }

                _recognizer.WritePcm16(pcm.AsSpan(0, m * 2), DateTimeOffset.UtcNow);
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