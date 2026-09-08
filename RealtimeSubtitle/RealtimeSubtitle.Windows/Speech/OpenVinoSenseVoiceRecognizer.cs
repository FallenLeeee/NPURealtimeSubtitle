using System.Diagnostics;
using System.Text.Json;
using OpenVinoSharp;
using RealtimeSubtitle.Core.Audio;
using RealtimeSubtitle.Core.Diagnostics;
using RealtimeSubtitle.Core.Speech;

namespace RealtimeSubtitle.Windows;

/// <summary>
/// SenseVoiceSmall ASR backend via the classic OpenVINO API (decision P6-6). The model is a
/// single non-autoregressive forward pass (CTC): kaldi-style log-mel (80 bins, hamming
/// 25 ms/10 ms, snip_edges, FFT 512, slaney mel — see <see cref="KaldiFbank"/>) → LFR 7/6
/// stacking → AM.MVN (AddShift/Rescale from the kaldi nnet) → ONNX → per-frame argmax with
/// CTC blank collapse → rich-transcription token ids → control-token stripping.
/// The ONNX file is read directly by OpenVINO (no IR conversion needed).
/// </summary>
public sealed class OpenVinoSenseVoiceRecognizer : ISpeechRecognizer
{
    private const int SampleRate = 16000;
    private const int PrepadSamples = 3200;
    private const int MaxRingSamplesDynamic = 240_000;   // 15 s cap (dynamic model)
    private const int MinSegmentSamples = 1600;
    private const int LfrM = 7;
    private const int LfrN = 6;
    private const int FeatureDim = 80;
    private const int VocabSize = 25055;
    private const int LanguageInput = 0;  // zh/ja/en all transcribe through CTC regardless
    private const int TextNormOff = 0;    // no inverse text normalisation (raw transcript)

    private readonly int _maxRingSamples;

    private readonly OpenVinoSharp.Core _core;
    private readonly CompiledModel _model;
    private readonly string _inputName;
    private readonly int _staticN;   // 0 = dynamic model; >0 = static [1, N, 560] (folded inputs)
    private readonly KaldiFbank _fbank;
    private readonly float[] _addShift;
    private readonly float[] _rescale;
    private readonly string[] _tokens;
    private readonly VoiceActivityDetector _vad;
    private readonly LogSink _log;
    private readonly object _sync = new();
    private readonly List<float> _ring = new();
    private int _segmentStart = -1;
    private bool _started;
    private readonly SemaphoreSlim _transcribeGate = new(1, 1);
    private DateTimeOffset _lastPartial = DateTimeOffset.MinValue;
    private readonly object _inFlightSync = new();
    private readonly List<Task> _inFlight = new();

    public OpenVinoSenseVoiceRecognizer(string modelDir, LogSink? log = null,
        RealtimeSubtitle.Core.Configuration.VadConfig? vad = null,
        string device = "auto")
    {
        _log = log ?? LogSink.Default;
        _core = new OpenVinoSharp.Core();
        OpenVinoDevice.ApplyCache(_core, "CPU", "NPU");

        // Prefer the static export (P6-6): fixed [1, N, 560] with the scalar inputs folded
        // into constants — no dynamic-shape fragility, and it compiles on the NPU (cached).
        // The dynamic model remains the fallback for older model dirs.
        _inputName = "speech";
        _staticN = 0;
        string staticPath = Path.Combine(modelDir, "model_static.onnx");
        string modelFile = "model_quant.onnx";

        using (var metaDoc = JsonDocument.Parse(File.ReadAllText(Path.Combine(modelDir, "sensevoice_meta.json"))))
        {
            if (metaDoc.RootElement.TryGetProperty("static_n", out JsonElement staticEl))
            {
                _staticN = staticEl.GetInt32();
            }
        }

        if (File.Exists(staticPath) && _staticN > 0)
        {
            modelFile = "model_static.onnx";
            // P6-9: the int8 dynamic-quantized graph mis-computes on the NPU, but dequantized
            // pure-FP32 static export is correct there and ~4.6x faster (58 ms vs 267 ms).
            // P6-18: device routing now mirrors the GUI ("auto"/"NPU" → try NPU FP32 first,
            // fall back to CPU; "CPU" → straight CPU). "auto" prefers NPU when available.
            string fp32Path = Path.Combine(modelDir, "model_fp32_static.onnx");
            bool npuArmed = device is "NPU" or "auto";
            if (File.Exists(fp32Path) && npuArmed)
            {
                try
                {
                    _model = _core.CompileModel(fp32Path, "NPU");
                    _log.Info("SenseVoice: FP32 static [1, {0}, 560] on NPU.", _staticN);
                }
                catch (Exception ex)
                {
                    _model = _core.CompileModel(fp32Path, "CPU");
                    _log.Warn("SenseVoice: FP32 static NPU compile failed ({0}); using CPU.", ex.Message);
                }
            }
            else if (File.Exists(fp32Path))
            {
                _model = _core.CompileModel(fp32Path, "CPU");
                _log.Info("SenseVoice: FP32 static [1, {0}, 560] on CPU (device={1}).", _staticN, device);
            }
            else
            {
                _model = _core.CompileModel(staticPath, "CPU");
                _log.Info("SenseVoice: int8 static [1, {0}, 560] on CPU (FP32 export not present; device={1}).", _staticN, device);
            }
        }
        else
        {
            _model = _core.CompileModel(Path.Combine(modelDir, modelFile), "CPU");
            _log.Info("SenseVoice: dynamic model on CPU (no static export present).");
        }

        _inputName = ResolveInputName(_model);
        // The static model processes a fixed N frames; keep segments within N LFR rows
        // (≈ (N-8)·960 samples) or the tail would be clipped.
        _maxRingSamples = _staticN > 0 ? Math.Min(MaxRingSamplesDynamic, (_staticN - 8) * 960) : MaxRingSamplesDynamic;

        _fbank = new KaldiFbank();

        using var meta = JsonDocument.Parse(File.ReadAllText(Path.Combine(modelDir, "sensevoice_meta.json")));
        JsonElement root = meta.RootElement;
        _addShift = ReadFloats(root.GetProperty("cmvn_shift"));
        _rescale = ReadFloats(root.GetProperty("cmvn_rescale"));
        if (_addShift.Length != FeatureDim * LfrM || _rescale.Length != FeatureDim * LfrM)
        {
            throw new InvalidDataException($"sensevoice_meta CMVN dims: {_addShift.Length} != {FeatureDim * LfrM}");
        }

        _tokens = JsonSerializer.Deserialize<string[]>(File.ReadAllText(Path.Combine(modelDir, "tokens.json")))
                  ?? Array.Empty<string>();

        // P6-14: honor the user-configured VAD (same as the whisper classic backend).
        // The old hard-coded 0.02/150/500 made SenseVoice segments very long when singing
        // continuously, so finals — and therefore translations — were rare.
        float thr = vad?.Threshold ?? 0.02f;
        int minMs = vad?.MinSpeechMs ?? 150;
        int hangMs = vad?.HangoverMs ?? 500;
        _vad = new VoiceActivityDetector(threshold: thr, minSpeechMs: minMs, hangoverMs: hangMs, sampleRate: SampleRate);
        _log.Info("SenseVoice recognizer ready ({0} tokens; VAD thr={1} min={2}ms hang={3}ms).",
            _tokens.Length, thr, minMs, hangMs);
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
            var chunk = new float[sampleCount];
            for (int i = 0; i < sampleCount; i++)
            {
                chunk[i] = BitConverter.ToInt16(pcmBytes.Slice(i * 2, 2)) / 32768f;
            }

            for (int off = 0; off < chunk.Length; off += 160)
            {
                int len = Math.Min(160, chunk.Length - off);
                VadResult vad = _vad.Process(chunk.AsSpan(off, len));

                if (vad.State == VadState.Speech && _segmentStart < 0)
                {
                    _segmentStart = Math.Max(0, _ring.Count - PrepadSamples);
                }
                else if (vad.State == VadState.Silence && _segmentStart >= 0)
                {
                    FinalizeSegment(timestamp);
                }
            }

            _ring.AddRange(chunk);
            if (_segmentStart < 0 && _ring.Count > _maxRingSamples)
            {
                _ring.RemoveRange(0, _ring.Count - _maxRingSamples);
            }

            if (_segmentStart >= 0 && _ring.Count - _segmentStart >= _maxRingSamples)
            {
                FinalizeSegment(timestamp);
            }

            if (_segmentStart >= 0)
            {
                int segLen = _ring.Count - _segmentStart;
                if (segLen >= SampleRate * 8 / 10
                    && DateTimeOffset.UtcNow - _lastPartial >= TimeSpan.FromMilliseconds(1200))
                {
                    _lastPartial = DateTimeOffset.UtcNow;
                    float[] seg = _ring.GetRange(_segmentStart, segLen).ToArray();
                    _ = StartTranscribe(seg, timestamp, partial: true);
                }
            }
        }
    }

    private void FinalizeSegment(DateTimeOffset timestamp)
    {
        int end = _ring.Count;
        int count = end - _segmentStart;
        _segmentStart = -1;
        if (count < MinSegmentSamples || count > _maxRingSamples) return;

        float[] segment = _ring.GetRange(end - count, count).ToArray();
        if (_ring.Count > PrepadSamples) _ring.RemoveRange(0, _ring.Count - PrepadSamples);

        _ = StartTranscribe(segment, timestamp, partial: false);
    }

    private Task StartTranscribe(float[] segment, DateTimeOffset timestamp, bool partial)
    {
        var task = Task.Run(() => Transcribe(segment, timestamp, partial));
        lock (_inFlightSync)
        {
            _inFlight.Add(task);
            _inFlight.RemoveAll(t => t.IsCompleted);
        }

        return task;
    }

    private void Transcribe(float[] segment, DateTimeOffset timestamp, bool partial)
    {
        if (partial)
        {
            if (!_transcribeGate.Wait(0)) return;
        }
        else
        {
            _transcribeGate.Wait();
        }

        try
        {
            var total = Stopwatch.StartNew();
            long t0 = total.ElapsedMilliseconds;
            float[] mel = _fbank.ComputeLogMel(segment);
            int T = KaldiFbank.FrameCount(segment.Length);
            long melMs = total.ElapsedMilliseconds - t0;

            t0 = total.ElapsedMilliseconds;
            float[] stacked = KaldiFbank.LfrCmvn(mel, T, FeatureDim, LfrM, LfrN, _addShift, _rescale, out int tLfr);
            long featMs = total.ElapsedMilliseconds - t0;

            string text = tLfr > 0 ? RunModel(stacked, tLfr) : string.Empty;
            long modelMs = total.ElapsedMilliseconds - t0;

            _log.Info("SenseVoice{0} in {1:N0} ms (mel {2}, lfr/c {3}, model {4}): \"{5}\"",
                partial ? " (partial)" : "", total.ElapsedMilliseconds, melMs, featMs, modelMs, text);
            if (text.Length > 0)
            {
                if (partial) Partial?.Invoke(new AsrPartial(text, timestamp));
                else Final?.Invoke(new AsrFinal(text, timestamp));
            }
        }
        catch (Exception ex)
        {
            _log.Error("SenseVoice transcription failed: {0}", ex);
            Error?.Invoke(ex);
        }
        finally
        {
            _transcribeGate.Release();
        }
    }

    private string RunModel(float[] stacked, int tLfr)
    {
        using var req = _model.CreateInferRequest();
        if (_staticN > 0)
        {
            // Static path: pad the LFR features to [1, N, 560]; the scalar inputs are folded.
            int n = Math.Min(Math.Max(tLfr, 1), _staticN);
            var padded = new float[_staticN * FeatureDim * LfrM];
            Array.Copy(stacked, padded, Math.Min(stacked.Length, padded.Length));
            req.SetInputTensor(_inputName, Float32Tensor(new[] { 1L, (long)_staticN, (long)FeatureDim * LfrM }, padded));
        }
        else
        {
            req.SetInputTensor(_inputName, Float32Tensor(new[] { 1L, (long)tLfr, (long)stacked.Length / tLfr }, stacked));
            req.SetInputTensor("speech_lengths", Int32Tensor(new[] { 1L }, new[] { tLfr }));
            req.SetInputTensor("language", Int32Tensor(new[] { 1L }, new[] { LanguageInput }));
            req.SetInputTensor("textnorm", Int32Tensor(new[] { 1L }, new[] { TextNormOff }));
        }

        req.Infer();

        int outLen = Math.Min(tLfr + 4, _staticN > 0 ? _staticN : int.MaxValue);
        try
        {
            int[] lens = req.GetOutputTensor("encoder_out_lens").GetIntData();
            if (lens.Length > 0 && lens[0] > 0)
            {
                // Decode the real rows plus a small margin (the unmasked padding shifts the
                // last tokens a few rows out); going deeper into the padding hallucinates.
                outLen = Math.Min(lens[0], outLen);
            }
        }
        catch
        {
            // fall back to the full frame count
        }

        float[] logits = req.GetOutputTensor("ctc_logits").GetFloatData(); // [1, L, vocab]

        var sb = new System.Text.StringBuilder();
        int prev = -1;
        for (int f = 0; f < outLen; f++)
        {
            int rowBase = f * VocabSize;
            int best = 0;
            float bestV = float.NegativeInfinity;
            for (int v = 0; v < VocabSize; v++)
            {
                if (logits[rowBase + v] > bestV)
                {
                    bestV = logits[rowBase + v];
                    best = v;
                }
            }

            // CTC greedy: skip blank (0) and direct repeats; prev tracks every frame
            // (a blank between identical tokens lets them emit twice, like the reference).
            if (best == 0 || best == prev)
            {
                prev = best;
                continue;
            }

            prev = best;

            string token = best >= 0 && best < _tokens.Length ? _tokens[best] : string.Empty;
            if (string.IsNullOrEmpty(token) || token.StartsWith("<|", StringComparison.Ordinal)
                || token is "<unk>" or "<s>" or "</s>") continue;

            sb.Append(token == "\u2581" ? " " : token.Replace("\u2581", " "));
        }

        return sb.ToString().Trim();
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

        _log.Info("SenseVoice started.");
    }

    public void Stop()
    {
        lock (_sync)
        {
            if (!_started) return;
            _started = false;
            if (_segmentStart >= 0)
            {
                FinalizeSegment(DateTimeOffset.UtcNow);
            }

            _segmentStart = -1;
        }

        _ring.Clear();
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _started = false;
            _segmentStart = -1;
        }

        Task[] pending;
        lock (_inFlightSync)
        {
            pending = _inFlight.Where(t => !t.IsCompleted).ToArray();
        }

        if (pending.Length > 0)
        {
            try { Task.WaitAll(pending, TimeSpan.FromSeconds(30)); } catch { }
        }

        try { _model.Dispose(); } catch { }
        try { _core.Dispose(); } catch { }
    }

    private static float[] ReadFloats(JsonElement arr)
    {
        var list = new float[arr.GetArrayLength()];
        int i = 0;
        foreach (JsonElement v in arr.EnumerateArray()) list[i++] = v.GetSingle();
        return list;
    }

    /// <summary>First input name of the compiled model ("speech" for both exports).</summary>
    private static string ResolveInputName(CompiledModel model)
    {
        try
        {
            if (model.InputCount > 0)
            {
                string name = model.get_input(0).GetAnyName();
                if (!string.IsNullOrEmpty(name)) return name;
            }
        }
        catch
        {
            // fall through to the default
        }

        return "speech";
    }

    private static Tensor Int32Tensor(long[] dims, int[] data)
    {
        var t = new Tensor(new Shape(dims), ElementType.I32);
        t.SetData(data);
        return t;
    }

    private static Tensor Float32Tensor(long[] dims, float[] data)
    {
        var t = new Tensor(new Shape(dims), ElementType.F32);
        t.SetData(data);
        return t;
    }
}
