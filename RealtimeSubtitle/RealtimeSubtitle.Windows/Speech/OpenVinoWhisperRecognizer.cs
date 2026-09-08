using OpenVinoSharp.GenAI;
using RealtimeSubtitle.Core.Audio;
using RealtimeSubtitle.Core.Diagnostics;
using RealtimeSubtitle.Core.Speech;

namespace RealtimeSubtitle.Windows;

/// <summary>
/// ASR backend per decision P2-3: Whisper (tiny/base INT8) through OpenVINO GenAI's
/// WhisperPipeline, NPU-priority. Implements <see cref="ISpeechRecognizer"/> by segmenting the
/// 16 kHz mono PCM stream with <see cref="VoiceActivityDetector"/> and transcribing each
/// finalized speech segment (whisper handles punctuation/language auto-detection internally,
/// ~99 languages — covers U2). Only Final results are raised; Partial preview stays off
/// (latency-first, and the subtitle pipeline only translates finals anyway).
/// </summary>
public sealed class OpenVinoWhisperRecognizer : ISpeechRecognizer
{
    private const int SampleRate = 16000;
    private const int PrepadSamples = 3200;   // 200 ms prepad
    private const int MaxRingSamples = 240_000; // 15 s cap
    private const int MinSegmentSamples = 1600; // 0.1 s

    private readonly WhisperPipeline _pipeline;
    private readonly VoiceActivityDetector _vad;
    private readonly LogSink _log;
    private readonly object _sync = new();

    private readonly List<float> _ring = new(); // 16k mono stream samples
    private int _segmentStart = -1;
    private bool _started;

    public OpenVinoWhisperRecognizer(string modelPath, string device = "auto", LogSink? log = null)
    {
        _log = log ?? LogSink.Default;
        if (device == "auto")
        {
            device = WhisperDevices.HasNpu() ? "NPU" : "CPU";
        }

        _log.Info("Initializing WhisperPipeline (model={0}, device={1})...", modelPath, device);
        _pipeline = new WhisperPipeline(modelPath, device);

        _vad = new VoiceActivityDetector(threshold: 0.02f, minSpeechMs: 150, hangoverMs: 500, sampleRate: SampleRate);
        _log.Info("WhisperPipeline ready on {0}.", device);
    }

    public event Action<AsrPartial>? Partial;
    public event Action<AsrFinal>? Final;
    public event Action<Exception>? Error;

    public void WritePcm16(ReadOnlySpan<byte> pcmBytes, DateTimeOffset timestamp)
    {
        int sampleCount = pcmBytes.Length / 2;
        if (sampleCount == 0) return;

        lock (_sync)
        {
            // Convert Int16 → float and feed both the segmenter ring and the VAD.
            var chunk = new float[sampleCount];
            for (int i = 0; i < sampleCount; i++)
            {
                chunk[i] = BitConverter.ToInt16(pcmBytes.Slice(i * 2, 2)) / 32768f;
            }

            for (int off = 0; off < chunk.Length; off += 160)
            {
                int len = Math.Min(160, chunk.Length - off);
                VadResult vad = _vad.Process(chunk.AsSpan(off, len));

                switch (vad.State)
                {
                    case VadState.Speech when _segmentStart < 0:
                        try
                        {
                            _segmentStart = Math.Max(0, _ring.Count - PrepadSamples);
                        }
                        catch (ArgumentOutOfRangeException)
                        {
                            _segmentStart = 0;
                        }

                        break;
                    case VadState.Silence when _segmentStart >= 0:
                        FinalizeSegment(timestamp);
                        break;
                }
            }

            _ring.AddRange(chunk);

            // Keep the ring bounded: after a finalized segment we only retain the prepad tail.
            if (_segmentStart < 0 && _ring.Count > MaxRingSamples)
            {
                _ring.RemoveRange(0, _ring.Count - MaxRingSamples);
            }
        }
    }

    private void FinalizeSegment(DateTimeOffset timestamp)
    {
        int end = _ring.Count;
        int count = end - _segmentStart;
        _segmentStart = -1;
        if (count < MinSegmentSamples || count > MaxRingSamples) return;

        float[] segment = _ring.GetRange(end - count, count).ToArray();
        // Keep only the prepad tail for the next turn.
        if (_ring.Count > PrepadSamples)
        {
            _ring.RemoveRange(0, _ring.Count - PrepadSamples);
        }

        _ = Task.Run(() => Transcribe(segment, timestamp));
    }

    private void Transcribe(float[] segment, DateTimeOffset timestamp)
    {
        try
        {
            DateTimeOffset started = DateTimeOffset.UtcNow;
            var config = new WhisperGenerationConfig();
            using var results = _pipeline.Generate(segment, config);
            string text = results.GetString().Trim();
            _log.Info("Whisper segment transcribed in {0:N0} ms ({1} samples): \"{2}\"",
                (DateTimeOffset.UtcNow - started).TotalMilliseconds, segment.Length, text);
            if (text.Length > 0)
            {
                Final?.Invoke(new AsrFinal(text, timestamp));
            }
        }
        catch (Exception ex)
        {
            _log.Error("Whisper transcription failed: {0}", ex);
            Error?.Invoke(ex);
        }
    }

    public void Start(CancellationToken token)
    {
        lock (_sync)
        {
            if (_started) return;
            _started = true;
            _vad.Reset();
            _ring.Clear();
            _segmentStart = -1;
        }

        _log.Info("Whisper recognizer started.");
    }

    public void Stop()
    {
        lock (_sync)
        {
            if (!_started) return;
            _started = false;
            _segmentStart = -1;
        }

        _log.Info("Whisper recognizer stopped.");
        _ring.Clear();
    }

    public void Dispose()
    {
        Stop();
        _pipeline.Dispose();
    }
}

/// <summary>Small helper to pick NPU when available (decisions.md P3-1).</summary>
public static class WhisperDevices
{
    public static bool HasNpu()
    {
        try
        {
            using var core = new OpenVinoSharp.Core();
            return core.GetAvailableDevices().Contains("NPU", StringComparer.Ordinal);
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static string[] Available()
    {
        try
        {
            using var core = new OpenVinoSharp.Core();
            return core.GetAvailableDevices().ToArray();
        }
        catch (Exception)
        {
            return Array.Empty<string>();
        }
    }
}