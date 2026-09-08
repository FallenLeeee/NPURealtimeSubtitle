using System.Diagnostics;
using System.Text.Json;
using OpenVinoSharp;
using RealtimeSubtitle.Core.Audio;
using RealtimeSubtitle.Core.Diagnostics;
using RealtimeSubtitle.Core.Speech;

namespace RealtimeSubtitle.Windows;

/// <summary>
/// Qwen3-ASR backend via the classic OpenVINO API (decision P6-5). Pipeline per segment:
///   whisper-style 128-mel [128, T] (padded to a multiple of 100 frames) →
///   AuT audio encoder (CPU; dynamic shape) → (1, n_audio, 2048) →
///   prompt ids (prefix + audio_pad×n_audio + suffix + language suffix) →
///   thinker embeddings → splice audio embeddings at the pad positions →
///   prefill KV (input_embeds + position_ids) → per-token decode (new_embed + new_pos + past)
///   → Qwen byte-level BPE decode.
/// The 1.7B LLM decode dominates latency (~2 s/segment on CPU), so this backend is
/// final-only (no streaming partials) — see the GUI hint for 中文.
/// </summary>
public sealed class OpenVinoQwen3AsrRecognizer : ISpeechRecognizer
{
    private const int SampleRate = 16000;
    private const int PrepadSamples = 3200;
    private const int MaxRingSamples = 240_000; // 15 s cap
    private const int MinSegmentSamples = 1600;
    private const int MaxMelFrames = 3000;
    private const int FrameChunk = 100;         // encoder reshape requires T % 100 == 0
    private const int VocabSize = 151936;
    private const int MaxDecodeTokens = 256;

    private readonly OpenVinoSharp.Core _core;
    private readonly CompiledModel _audioEncoder;
    private readonly CompiledModel _thinker;
    private readonly CompiledModel _prefill;
    private readonly CompiledModel _decode;
    private readonly MelSpectrogram _mel;
    private readonly QwenBpeDecoder _token;
    private readonly int[] _prefix;
    private readonly int[] _suffix;
    private readonly int[] _langSuffix;
    private readonly int _audioPadId;
    private readonly int _eosId;
    private readonly VoiceActivityDetector _vad;
    private readonly LogSink _log;
    private readonly object _sync = new();
    private readonly List<float> _ring = new();
    private int _segmentStart = -1;
    private bool _started;
    private readonly SemaphoreSlim _transcribeGate = new(1, 1);
    private readonly object _inFlightSync = new();
    private readonly List<Task> _inFlight = new();

    public OpenVinoQwen3AsrRecognizer(string modelDir, string language = "zh", LogSink? log = null)
    {
        _log = log ?? LogSink.Default;
        _core = new OpenVinoSharp.Core();
        OpenVinoDevice.ApplyCache(_core, "CPU", "NPU");

        _audioEncoder = _core.CompileModel(Path.Combine(modelDir, "audio_encoder_model.xml"), "CPU");
        _thinker = _core.CompileModel(Path.Combine(modelDir, "thinker_embeddings_model.xml"), "CPU");
        _prefill = _core.CompileModel(Path.Combine(modelDir, "decoder_prefill_kv_model.xml"), "CPU");
        _decode = _core.CompileModel(Path.Combine(modelDir, "decoder_kv_model.xml"), "CPU");
        _mel = new MelSpectrogram(Path.Combine(modelDir, "mel_filters_128.bin"));
        _token = QwenBpeDecoder.Load(Path.Combine(modelDir, "vocab.json"));

        using var prompt = JsonDocument.Parse(File.ReadAllText(Path.Combine(modelDir, "prompt_template.json")));
        JsonElement root = prompt.RootElement;
        _prefix = ReadInts(root.GetProperty("prefix_ids"));
        _suffix = ReadInts(root.GetProperty("suffix_ids"));
        _audioPadId = root.GetProperty("audio_pad_id").GetInt32();
        _eosId = root.GetProperty("eos_id").GetInt32();

        // Language bias suffix (e.g. "Chinese" → [11528, 8453, 151704]); unknown → empty.
        _langSuffix = Array.Empty<int>();
        string langName = LanguageName(language);
        if (root.TryGetProperty("language_suffix_ids", out JsonElement suffixes)
            && suffixes.TryGetProperty(langName, out JsonElement suf))
        {
            _langSuffix = ReadInts(suf);
        }

        _vad = new VoiceActivityDetector(threshold: 0.02f, minSpeechMs: 150, hangoverMs: 500, sampleRate: SampleRate);
        _log.Info("Qwen3-ASR recognizer ready (lang={0}, suffix={1}).", langName, _langSuffix.Length > 0 ? string.Join(",", _langSuffix) : "none");
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
            if (_segmentStart < 0 && _ring.Count > MaxRingSamples)
            {
                _ring.RemoveRange(0, _ring.Count - MaxRingSamples);
            }

            // Final-only backend (P6-5): the 1.7B decode cannot keep a 1.2 s partial cadence.
            if (_segmentStart >= 0 && _ring.Count - _segmentStart >= MaxRingSamples)
            {
                FinalizeSegment(timestamp);
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
        if (_ring.Count > PrepadSamples) _ring.RemoveRange(0, _ring.Count - PrepadSamples);

        _ = StartTranscribe(segment, timestamp);
    }

    private Task StartTranscribe(float[] segment, DateTimeOffset timestamp)
    {
        var task = Task.Run(() => Transcribe(segment, timestamp));
        lock (_inFlightSync)
        {
            _inFlight.Add(task);
            _inFlight.RemoveAll(t => t.IsCompleted);
        }

        return task;
    }

    private void Transcribe(float[] segment, DateTimeOffset timestamp)
    {
        _transcribeGate.Wait();
        try
        {
            var total = Stopwatch.StartNew();

            // 128-mel features; the encoder needs the time length as a multiple of 100.
            long t0 = total.ElapsedMilliseconds;
            float[] mel = _mel.Compute(segment); // [128, 3000], zero-padded tail
            int nb = Math.Min(1 + segment.Length / 160, MaxMelFrames);
            int padded = ((nb + FrameChunk - 1) / FrameChunk) * FrameChunk;
            var mel2d = new float[128 * padded];
            for (int m = 0; m < 128; m++)
            {
                Array.Copy(mel, m * MaxMelFrames, mel2d, m * padded, padded);
            }

            long melMs = total.ElapsedMilliseconds - t0;

            // audio encoder (dynamic [128, T] → (1, n_audio, 2048)); n_audio = T/100*13.
            t0 = total.ElapsedMilliseconds;
            using var encReq = _audioEncoder.CreateInferRequest();
            encReq.SetInputTensor("mel", Float32Tensor(new[] { 128L, (long)padded }, mel2d));
            encReq.Infer();
            float[] audioEmbeds = encReq.GetOutputTensor(0).GetFloatData();
            int nAudio = padded / FrameChunk * 13;
            long encMs = total.ElapsedMilliseconds - t0;

            // prompt ids + embeddings
            t0 = total.ElapsedMilliseconds;
            var ids = new List<int>(_prefix.Length + nAudio + _suffix.Length + _langSuffix.Length);
            ids.AddRange(_prefix);
            for (int i = 0; i < nAudio; i++) ids.Add(_audioPadId);
            ids.AddRange(_suffix);
            ids.AddRange(_langSuffix);

            long[] idArr = ids.Select(i => (long)i).ToArray();
            using var thReq = _thinker.CreateInferRequest();
            thReq.SetInputTensor("input_ids", Int64Tensor(new[] { 1L, (long)idArr.Length }, idArr));
            thReq.Infer();
            float[] embeds = thReq.GetOutputTensor(0).GetFloatData(); // [1, L, 2048]

            // splice audio embeddings over the audio_pad positions
            int spliceStart = _prefix.Length * 2048;
            int spliceCount = nAudio * 2048;
            Array.Copy(audioEmbeds, 0, embeds, spliceStart, spliceCount);
            long embMs = total.ElapsedMilliseconds - t0;

            // prefill KV
            t0 = total.ElapsedMilliseconds;
            var posIds = new long[idArr.Length];
            for (int i = 0; i < posIds.Length; i++) posIds[i] = i;
            using var pfReq = _prefill.CreateInferRequest();
            pfReq.SetInputTensor("input_embeds", Float32Tensor(new[] { 1L, (long)idArr.Length, 2048L }, embeds));
            pfReq.SetInputTensor("position_ids", Int64Tensor(new[] { 1L, (long)posIds.Length }, posIds));
            pfReq.Infer();
            float[] logits = pfReq.GetOutputTensor(0).GetFloatData();
            float[] pastKeys = pfReq.GetOutputTensor(1).GetFloatData();
            float[] pastValues = pfReq.GetOutputTensor(2).GetFloatData();
            long prefillMs = total.ElapsedMilliseconds - t0;

            // decode loop
            t0 = total.ElapsedMilliseconds;
            var generated = new List<int>();
            long next = ArgMax(logits, 0, VocabSize);
            using var thStep = _thinker.CreateInferRequest();
            using var decReq = _decode.CreateInferRequest();
            for (int step = 0; step < MaxDecodeTokens; step++)
            {
                if (next == _eosId) break;
                generated.Add((int)next);

                thStep.SetInputTensor("input_ids", Int64Tensor(new[] { 1L, 1L }, new long[] { next }));
                thStep.Infer();
                float[] newEmbed = thStep.GetOutputTensor(0).GetFloatData(); // [1,1,2048]

                decReq.SetInputTensor("new_embed", Float32Tensor(new[] { 1L, 1L, 2048L }, newEmbed));
                decReq.SetInputTensor("new_pos", Int64Tensor(new[] { 1L, 1L }, new long[] { idArr.Length + step }));
                decReq.SetInputTensor("past_keys", Float32Tensor(new[] { 28L, 1L, 8L, (long)(idArr.Length + step), 128L }, pastKeys));
                decReq.SetInputTensor("past_values", Float32Tensor(new[] { 28L, 1L, 8L, (long)(idArr.Length + step), 128L }, pastValues));
                decReq.Infer();

                logits = decReq.GetOutputTensor(0).GetFloatData();
                pastKeys = decReq.GetOutputTensor(1).GetFloatData();
                pastValues = decReq.GetOutputTensor(2).GetFloatData();
                next = ArgMax(logits, 0, VocabSize);
            }

            long decMs = total.ElapsedMilliseconds - t0;

            string text = _token.Decode(generated);
            _log.Info("Qwen3-ASR in {0:N0} ms (mel {1}, enc {2}, emb {3}, prefill {4}, dec {5}): \"{6}\"",
                total.ElapsedMilliseconds, melMs, encMs, embMs, prefillMs, decMs, text);
            if (text.Length > 0)
            {
                Final?.Invoke(new AsrFinal(text, timestamp));
            }
        }
        catch (Exception ex)
        {
            _log.Error("Qwen3-ASR transcription failed: {0}", ex);
            Error?.Invoke(ex);
        }
        finally
        {
            _transcribeGate.Release();
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

        _log.Info("Qwen3-ASR started.");
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

        foreach (var m in new[] { _decode, _prefill, _thinker, _audioEncoder })
        {
            try { m.Dispose(); } catch { }
        }

        try { _core.Dispose(); } catch { }
    }

    private static string LanguageName(string language) => language switch
    {
        "en" => "English",
        "ja" => "Japanese",
        _ => "Chinese",
    };

    private static int[] ReadInts(JsonElement arr)
    {
        var list = new int[arr.GetArrayLength()];
        int i = 0;
        foreach (JsonElement v in arr.EnumerateArray()) list[i++] = v.GetInt32();
        return list;
    }

    private static long ArgMax(float[] values, int start, int count)
    {
        long best = 0;
        float bestValue = float.NegativeInfinity;
        int end = Math.Min(start + count, values.Length);
        for (int i = start; i < end; i++)
        {
            if (values[i] > bestValue)
            {
                bestValue = values[i];
                best = i - start;
            }
        }

        return best;
    }

    private static Tensor Int64Tensor(long[] dims, long[] data)
    {
        var t = new Tensor(new Shape(dims), ElementType.I64);
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
