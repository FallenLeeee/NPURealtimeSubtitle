using System.Diagnostics;
using System.Text.Json;
using OpenVinoSharp;
using RealtimeSubtitle.Core.Audio;
using RealtimeSubtitle.Core.Diagnostics;
using RealtimeSubtitle.Core.Speech;

namespace RealtimeSubtitle.Windows;

/// <summary>
/// Qwen3-ASR backend via the classic OpenVINO API (decision P6-5 / P6-24). Pipeline per segment:
///   whisper-style 128-mel [128, T] (padded to a multiple of 100 frames) →
///   AuT audio encoder (CPU; dynamic shape) → (1, n_audio, 2048) →
///   prompt ids (prefix + audio_pad×n_audio + suffix + language suffix) →
///   thinker embeddings → splice audio embeddings at the pad positions →
///   prefill KV (input_embeds + position_ids) → per-token decode (new_embed + new_pos + past)
///   → Qwen byte-level BPE decode.
/// The 1.7B LLM decode dominates latency (~0.5–2 s/segment on CPU) and cannot NPU-compile
/// (dynamic KV shapes, hundreds of dynamic nodes after reshape — P6-5). A static-NPU encoder
/// ([128,3000]) is compileable but forces ~390 audio tokens on every short clip, which
/// inflates prefill more than the encoder gains — net loss for realtime subtitles.
/// Chinese default backend is SenseVoice (fast + NPU); this path stays for max accuracy via
/// Asr.ZhBackend=qwen3, tightened with an 8 s ring, 96-token decode cap, and CPU LATENCY hint.
/// Streaming: in-progress VAD partial every ~1.2 s + mid-decode Partial every few tokens.
/// </summary>
public sealed class OpenVinoQwen3AsrRecognizer : ISpeechRecognizer
{
    private const int SampleRate = 16000;
    private const int PrepadSamples = 3200;
    // 8 s cap (was 15 s): shorter segments decode far faster and match Whisper's ring budget.
    private const int MaxRingSamples = 128_000;
    private const int MinSegmentSamples = 1600;
    private const int MaxMelFrames = 3000;
    private const int FrameChunk = 100;         // encoder reshape requires T % 100 == 0
    private const int VocabSize = 151936;
    // Subtitles rarely need more than ~40 Chinese tokens; 96 is a safe realtime budget
    // (was 256 — a runaway decode could burn seconds for no UX gain).
    private const int MaxDecodeTokens = 96;
    private const int PartialMinSegmentSamples = SampleRate * 8 / 10; // 0.8 s
    private const int PartialIntervalMs = 1200;
    private const int PartialDecodeEveryTokens = 6; // mid-decode Partial cadence

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
    private DateTimeOffset _lastPartial = DateTimeOffset.MinValue;
    private readonly object _inFlightSync = new();
    private readonly List<Task> _inFlight = new();

    public OpenVinoQwen3AsrRecognizer(string modelDir, string language = "zh", LogSink? log = null,
        string device = "auto")
    {
        _log = log ?? LogSink.Default;
        _core = new OpenVinoSharp.Core();
        OpenVinoDevice.ApplyCache(_core, "CPU", "NPU");
        ApplyCpuLatencyHint(_core);

        // P6-18/P6-24: the 1.7B thinker/prefill/decode only compile on CPU. The AuT encoder
        // stays CPU+dynamic so short clips keep a short audio-token prompt (a static-NPU
        // [128,3000] pad makes prefill slower for typical 1–4 s subtitle segments).
        if (device is not ("auto" or "CPU"))
        {
            _log.Warn("Qwen3-ASR: device='{0}' requested, but the dynamic 1.7B pipeline only compiles on CPU — using CPU. Chinese realtime/NPU path is SenseVoice (Asr.ZhBackend=sensevoice).", device);
        }

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

            // Force-finalize over-long continuous speech so subtitles keep flowing.
            if (_segmentStart >= 0 && _ring.Count - _segmentStart >= MaxRingSamples)
            {
                FinalizeSegment(timestamp);
            }

            // Streaming Partial: re-transcribe the in-progress segment every ~1.2 s so
            // Chinese text appears while the speaker is still talking (previously final-only).
            if (_segmentStart >= 0)
            {
                int segLen = _ring.Count - _segmentStart;
                if (segLen >= PartialMinSegmentSamples
                    && DateTimeOffset.UtcNow - _lastPartial >= TimeSpan.FromMilliseconds(PartialIntervalMs))
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
        // Continuous speech hits the 15 s ring cap with count == MaxRingSamples + a chunk.
        // `count > MaxRingSamples → return` dropped that entire segment (no Chinese output).
        if (count > MaxRingSamples) count = MaxRingSamples;
        _segmentStart = -1;
        if (count < MinSegmentSamples) return;

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
        // Partials are disposable previews — skip when a Final/partial already holds the slot.
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
            string lastPreview = string.Empty;
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

                // Mid-decode stream: Finals take ~0.5–2 s — push Partial every few tokens so
                // the overlay grows instead of dumping the whole sentence at once.
                if (!partial && generated.Count % PartialDecodeEveryTokens == 0)
                {
                    string preview = _token.Decode(generated);
                    if (preview.Length > 0 && !string.Equals(preview, lastPreview, StringComparison.Ordinal))
                    {
                        lastPreview = preview;
                        Partial?.Invoke(new AsrPartial(preview, timestamp));
                    }
                }
            }

            long decMs = total.ElapsedMilliseconds - t0;

            string text = _token.Decode(generated);
            _log.Info("Qwen3-ASR{0} in {1:N0} ms (mel {2}, enc {3}, emb {4}, prefill {5}, dec {6}): \"{7}\"",
                partial ? " (partial)" : "", total.ElapsedMilliseconds, melMs, encMs, embMs, prefillMs, decMs, text);
            if (text.Length > 0)
            {
                if (partial) Partial?.Invoke(new AsrPartial(text, timestamp));
                else Final?.Invoke(new AsrFinal(text, timestamp));
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

    /// <summary>Realtime ASR wants the lowest per-call latency, not max throughput.</summary>
    private static void ApplyCpuLatencyHint(OpenVinoSharp.Core core)
    {
        try
        {
            core.SetProperty("CPU", "PERFORMANCE_HINT", "LATENCY");
        }
        catch
        {
            // Older OpenVINO builds use PERF_COUNT/NUM_THREADS defaults — ignore.
        }
    }

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
