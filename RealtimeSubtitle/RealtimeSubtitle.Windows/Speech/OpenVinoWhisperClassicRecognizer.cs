using System.Diagnostics;
using OpenVinoSharp;
using RealtimeSubtitle.Core.Audio;
using RealtimeSubtitle.Core.Diagnostics;
using RealtimeSubtitle.Core.Speech;

namespace RealtimeSubtitle.Windows;

/// <summary>
/// Whisper ASR backend via the classic OpenVINO API (decision P3-5 — the GenAI runtime's
/// whisper contract is upstream-broken). Pipeline: 16 kHz mono segments (VAD-gated) → C#
/// log-mel [1,80,3000] → static encoder (NPU-priority, CPU fallback) → greedy decoder with
/// language auto-detection → byte-level BPE decode.
///
/// Decoder variants (P6-2): the `-with-past` optimum export hides the KV cache inside the
/// graph as OpenVINO states (ReadValue/Assign), so each step feeds ONE token instead of the
/// whole prefix and the cross-attention K/V are computed once. That path is ~10x faster per
/// step than the older no-past export (which re-runs the full prefix every step); a fresh
/// InferRequest per segment gives a fresh cache because the binding exposes no reset_state.
/// Both variants stay supported so older model dirs keep working.
/// </summary>
public sealed class OpenVinoWhisperClassicRecognizer : ISpeechRecognizer
{
    private const int SampleRate = 16000;
    private const int PrepadSamples = 3200;     // 200 ms prepad
    private const int MaxRingSamples = 128_000; // 8 s cap (P6-11: 15 s collapsed music chunks transcribe badly)
    private const int MinSegmentSamples = 1600; // 0.1 s
    private const int MaxDecodeTokens = 448;

    private readonly OpenVinoSharp.Core _core;
    private readonly CompiledModel? _encoder;    // dynamic CPU encoder (lazy fallback)
    private readonly CompiledModel? _encoderNpu; // static [1,80,3000] NPU encoder (when armed)
    private readonly CompiledModel _decoder;     // CPU decoder (stateful or full-prefix)
    private readonly string _encoderDevice;      // "NPU" | "CPU"
    private readonly MelSpectrogram _mel;
    private readonly WhisperTokenDecoder _token;
    private readonly VoiceActivityDetector _vad;
    private readonly LogSink _log;
    private readonly string _language; // "auto" or e.g. "en"
    private readonly object _sync = new();
    private readonly List<float> _ring = new();
    private int _segmentStart = -1;
    private bool _started;
    private readonly SemaphoreSlim _transcribeGate = new(1, 1);
    private DateTimeOffset _lastPartial = DateTimeOffset.MinValue;
    private int _junkBurst;           // consecutive junk decodes (P6-11: suppress partial spam)
    private const int JunkBurstLimit = 3;
    private long _junkSinceMs = -1;   // P6-23: start of the current junk run (TickCount64), -1 = none
    private long _lastJunkInfoMs;     // P6-23: last time we surfaced the "pure music" Info line
    private readonly object _inFlightSync = new();
    private readonly List<Task> _inFlight = new();

    // Decoder interface, resolved at construction (names differ between exports).
    private readonly bool _decoderStateful;
    private readonly string _decIdsName;
    private readonly string _decEncName;
    private readonly string? _decBeamName;

    public OpenVinoWhisperClassicRecognizer(string modelDir, string device = "auto", string language = "auto", LogSink? log = null, RealtimeSubtitle.Core.Configuration.VadConfig? vad = null)
    {
        _log = log ?? LogSink.Default;
        _language = language;
        _core = new OpenVinoSharp.Core();

        // Encoder NPU (P5-10): the optimum-exported encoder has dynamic dims, but the mel input
        // is always [1,80,3000] (fixed 30 s window, zero-padded), so reshaping to a static shape
        // lets the NPU compile it once. The NPU compiled blob is cached on disk (P6-3), so
        // later starts and hot swaps load in ~0.2 s instead of recompiling for ~15 s.
        _encoderDevice = "CPU";
        _encoderNpu = null;
        _encoder = null;
        if (device is "NPU" or "auto")
        {
            OpenVinoDevice.ApplyCache(_core, "NPU", "GPU", "CPU");
            _log.Info("Whisper(classic): compiling encoder [1,80,3000] on NPU ...");
            try
            {
                var encModel = _core.ReadModel(Path.Combine(modelDir, "openvino_encoder_model.xml"));
                try
                {
                    encModel.Reshape("input_features", new long[] { 1, 80, 3000 });
                    _encoderNpu = _core.CompileModel(encModel, "NPU");
                    _encoderDevice = "NPU";
                }
                finally
                {
                    encModel.Dispose();
                }

                _log.Info("Whisper(classic): encoder on NPU, decoder on CPU.");
            }
            catch (Exception ex)
            {
                _encoderNpu = null;
                _encoderDevice = "CPU";
                _log.Warn("Whisper(classic): encoder NPU compile failed ({0}); using CPU.", ex.Message);
            }
        }
        else
        {
            _log.Info("Whisper(classic): encoder on CPU, decoder on CPU.");
        }

        if (_encoderNpu is null)
        {
            _encoder = Compile(modelDir, "openvino_encoder_model.xml", "CPU");
        }

        string decPath = Path.Combine(modelDir, "openvino_decoder_model.xml");
        _decoder = Compile(modelDir, "openvino_decoder_model.xml", "CPU");

        // Resolve the decoder's interface: optimum names the ids input differently between
        // exports, and only the `-with-past` export carries internal KV-cache states.
        _decIdsName = "input_ids";
        _decEncName = "encoder_hidden_states";
        _decBeamName = null;
        _decoderStateful = false;
        try
        {
            using var decModel = _core.ReadModel(decPath);
            var names = decModel.Inputs.Select(i => i.GetAnyName()).ToArray();
            _decIdsName = names.FirstOrDefault(n => n.Contains("input_ids")) ?? names[0];
            _decEncName = names.FirstOrDefault(n => n.Contains("encoder_hidden_states")) ?? names[1];
            _decBeamName = names.FirstOrDefault(n => n.Contains("beam_idx"));
            _decoderStateful = FileContains(decPath, "ReadValue");
        }
        catch (Exception ex)
        {
            _log.Warn("Whisper(classic): decoder interface probe failed ({0}); assuming defaults.", ex.Message);
        }

        _mel = new MelSpectrogram(Path.Combine(modelDir, "mel_filters.bin"));
        _token = WhisperTokenDecoder.Load(Path.Combine(modelDir, "whisper_meta.json"));

        // P6-11: VAD parameters come from config (previously hardcoded 0.02/150/500 which
        // ignored VadConfig entirely — the feeder's own VAD result was also discarded).
        float thr = vad?.Threshold ?? 0.02f;
        int minMs = vad?.MinSpeechMs ?? 150;
        int hangMs = vad?.HangoverMs ?? 500;
        _vad = new VoiceActivityDetector(thr, minMs, hangMs, sampleRate: SampleRate);
        _log.Info("Whisper(classic) ready (encoder={0}, decoder={1}); VAD thr={2} min={3}ms hang={4}ms.",
            _encoderDevice, _decoderStateful ? "stateful KV-cache" : "full-prefix", thr, minMs, hangMs);
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
                    _log.Debug("Whisper(classic): VAD speech start (rms={0:F3}).", vad.Rms);
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

            // Force-finalize over-long continuous speech (reaches the ring cap) so subtitles
            // keep flowing even when the speaker never pauses (e.g. a continuous video track).
            if (_segmentStart >= 0 && _ring.Count - _segmentStart >= MaxRingSamples)
            {
                FinalizeSegment(timestamp);
            }

            // Streaming partial: while speech is in progress, periodically transcribe the
            // in-progress segment so the subtitle previews text before the sentence ends.
            // P6-11: while a junk burst ([Music] flood) is active, skip partial encodes
            // entirely — they re-encode the whole growing segment every 1.2 s for nothing.
            if (_segmentStart >= 0)
            {
                int segLen = _ring.Count - _segmentStart;
                int burst = Volatile.Read(ref _junkBurst);
                // Throttle while junk ([Music]) floods, but retry at least every ~5 s so a song
                // that later has lyrics recovers without waiting for a VAD gap or ring-cap final.
                bool bursting = burst >= JunkBurstLimit
                    && DateTimeOffset.UtcNow - _lastPartial < TimeSpan.FromMilliseconds(5000);
                if (!bursting
                    && segLen >= SampleRate * 8 / 10
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
        // Continuous music/speech hits the 8 s ring cap with count == MaxRingSamples + a partial
        // chunk. The old `count > MaxRingSamples → return` dropped that entire segment, so a song
        // without pauses produced zero finals (and junk-burst had already muted partials).
        if (count > MaxRingSamples) count = MaxRingSamples;
        _segmentStart = -1;
        if (count < MinSegmentSamples) return;

        float[] segment = _ring.GetRange(end - count, count).ToArray();
        if (_ring.Count > PrepadSamples) _ring.RemoveRange(0, _ring.Count - PrepadSamples);

        _ = StartTranscribe(segment, timestamp, partial: false);
    }

    /// <summary>Runs a transcription on the thread pool and tracks it so Dispose can await it.</summary>
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
        // Serialize transcribes on one gate: finals wait for the slot, partials skip when busy
        // (a preview is disposable, and a concurrent Infer on the same compiled models is avoided).
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
            float[] mel = _mel.Compute(segment);
            long melMs = total.ElapsedMilliseconds - t0;

            // encoder (NPU static when armed, else CPU dynamic)
            t0 = total.ElapsedMilliseconds;
            var encReq = (_encoderNpu ?? _encoder!).CreateInferRequest();
            encReq.SetInputTensor("input_features", Float32Tensor(new[] { 1L, 80L, (long)_mel.FramesCap }, mel));
            encReq.Infer();
            float[] hidden = encReq.GetOutputTensor("last_hidden_state").GetFloatData();
            encReq.Dispose();
            int hiddenDim = mel.Length > 0 ? hidden.Length / 1500 : 384;
            long encMs = total.ElapsedMilliseconds - t0;

            t0 = total.ElapsedMilliseconds;
            var generated = _decoderStateful
                ? DecodeStateful(hidden, hiddenDim)
                : DecodeFullPrefix(hidden, hiddenDim);
            long decMs = total.ElapsedMilliseconds - t0;

            string text = _token.Decode(generated);

            // P6-11: audio-event annotations ([Music] etc.) are not speech. Count consecutive
            // junk decodes to suppress the partial-encode flood, and log them at Debug so the
            // LogBox does not scroll "[Music]" every 1.2 s while a song plays.
            // P6-23: surface the "pure music, no lyrics" state at Info once per ~15 s so a
            // silent subtitle period is diagnosable ("recognizer alive, just [Music]") instead
            // of looking like a dead pipeline.
            bool junk = AsrJunkFilter.IsJunk(text);
            if (junk)
            {
                int burst = Interlocked.Increment(ref _junkBurst);
                long now = Environment.TickCount64;
                if (Interlocked.Read(ref _junkSinceMs) < 0)
                {
                    Interlocked.Exchange(ref _junkSinceMs, now);
                }

                _log.Debug("Whisper(classic){0} junk decode ({1:N0} ms): \"{2}\"",
                    partial ? " (partial)" : "", total.ElapsedMilliseconds, text);
                // Finals must always surface (they close the segment); only partials throttle.
                if (!partial)
                {
                    Interlocked.Exchange(ref _junkBurst, 0);
                }
                else if (burst >= JunkBurstLimit)
                {
                    // Keep the throttle, but retry a partial every few seconds so a song that
                    // later yields lyrics can recover without waiting for a VAD silence.
                    _log.Info("Whisper(classic): junk burst {0}; partials throttled.", burst);
                }

                // P6-23: tell the user once per ~15 s that the recognizer is alive but the
                // source is a pure-music segment (no lyrics to show).
                long runSec = (now - Interlocked.Read(ref _junkSinceMs)) / 1000L;
                if (runSec >= 15 && now - Interlocked.Read(ref _lastJunkInfoMs) >= 15_000)
                {
                    Interlocked.Exchange(ref _lastJunkInfoMs, now);
                    _log.Info("Whisper(classic): 纯音乐段持续 {0}s — 识别器运行中，但仅检测到 [Music] 类事件，无歌词可显示。", runSec);
                }
            }
            else
            {
                if (Interlocked.Read(ref _junkSinceMs) >= 0)
                {
                    _log.Info("Whisper(classic): 恢复到歌词（纯音乐段结束）。");
                }

                Interlocked.Exchange(ref _junkSinceMs, -1);
                Interlocked.Exchange(ref _junkBurst, 0);
                _log.Info("Whisper(classic){0} in {1:N0} ms (mel {2}, enc {3}, dec {4}): \"{5}\"",
                    partial ? " (partial)" : "", total.ElapsedMilliseconds, melMs, encMs, decMs, text);
            }

            if (text.Length > 0)
            {
                if (partial) Partial?.Invoke(new AsrPartial(text, timestamp));
                else Final?.Invoke(new AsrFinal(text, timestamp));
            }
        }
        catch (Exception ex)
        {
            _log.Error("Whisper(classic) transcription failed: {0}", ex);
            Error?.Invoke(ex);
        }
        finally
        {
            _transcribeGate.Release();
        }
    }

    /// <summary>
    /// Greedy decode through the stateful (KV-cache) decoder: a fresh InferRequest starts with
    /// an empty cache, the prompt is prefilled, then every step feeds a single token.
    /// </summary>
    private int[] DecodeStateful(float[] hidden, int hiddenDim)
    {
        var ids = new List<long>();
        using var req = _decoder.CreateInferRequest();
        req.SetInputTensor(_decEncName, Float32Tensor(new[] { 1L, 1500L, (long)hiddenDim }, hidden));
        if (_decBeamName is not null)
        {
            req.SetInputTensor(_decBeamName, Int32Tensor(new[] { 1L }, new[] { 0 }));
        }

        long next;
        if (_token.Mono)
        {
            // whisper.en: no language token, no detection — prompt = [sot, transcribe, notimestamps].
            var prompt = new List<long> { _token.SotId, _token.TranscribeId, _token.NoTimestampsId };
            next = Step(req, prompt.ToArray());
            ids.AddRange(prompt);
        }
        else if (_language != "auto" && _token.LanguageId(_language) >= 0)
        {
            var prompt = new List<long> { _token.SotId, _token.LanguageId(_language), _token.TranscribeId, _token.NoTimestampsId };
            next = Step(req, prompt.ToArray());
            ids.AddRange(prompt);
        }
        else
        {
            // Prefill the SOT token only, read the language from that row, then continue.
            long langId = StepLanguage(req);
            ids.Add(_token.SotId);
            ids.Add(langId);
            next = Step(req, new[] { langId, _token.TranscribeId, _token.NoTimestampsId });
            ids.Add(_token.TranscribeId);
            ids.Add(_token.NoTimestampsId);
        }

        for (int step = 0; step < MaxDecodeTokens; step++)
        {
            if (next == _token.EosId) break;
            ids.Add(next);
            next = Step(req, new[] { next });
        }

        return ids.Skip(_token.Mono ? 3 : 4).Where(i => i != _token.EosId).Select(i => (int)i).ToArray();
    }

    /// <summary>One stateful decoder step with the given tokens; returns the argmax next token.</summary>
    private long Step(InferRequest req, long[] tokens)
    {
        req.SetInputTensor(_decIdsName, Int64Tensor(new[] { 1L, (long)tokens.Length }, tokens));
        req.Infer();
        float[] logits = req.GetOutputTensor("logits").GetFloatData();
        int vocab = logits.Length / tokens.Length;
        return vocab > 0 ? ArgMax(logits, (tokens.Length - 1) * vocab, vocab) : _token.EosId;
    }

    /// <summary>Feeds [sot] and returns the most likely language token.</summary>
    private long StepLanguage(InferRequest req)
    {
        req.SetInputTensor(_decIdsName, Int64Tensor(new[] { 1L, 1L }, new long[] { _token.SotId }));
        req.Infer();
        float[] logits = req.GetOutputTensor("logits").GetFloatData();
        var langIds = _token.LangIds.Values.Distinct().ToArray();
        long best = langIds.Length > 0 ? langIds[0] : -1;
        float bestScore = float.NegativeInfinity;
        foreach (int lid in langIds)
        {
            if (lid >= 0 && lid < logits.Length && logits[lid] > bestScore)
            {
                bestScore = logits[lid];
                best = lid;
            }
        }

        return best;
    }

    /// <summary>Legacy path for no-past exports: the whole prefix is re-fed on every step.</summary>
    private int[] DecodeFullPrefix(float[] hidden, int hiddenDim)
    {
        var ids = new List<long> { _token.SotId };
        if (_token.Mono)
        {
            // whisper.en: no language token, no detection.
        }
        else if (_language != "auto")
        {
            int lid = _token.LanguageId(_language);
            if (lid >= 0) ids.Add(lid);
        }
        else
        {
            ids.Add(DetectLanguage(hidden, hiddenDim));
        }

        ids.Add(_token.TranscribeId);
        ids.Add(_token.NoTimestampsId);

        using var decReq = _decoder.CreateInferRequest();
        for (int step = 0; step < MaxDecodeTokens; step++)
        {
            long[] cur = ids.ToArray();
            decReq.SetInputTensor(_decIdsName, Int64Tensor(new[] { 1L, (long)cur.Length }, cur));
            decReq.SetInputTensor(_decEncName, Float32Tensor(new[] { 1L, 1500L, (long)hiddenDim }, hidden));
            if (_decBeamName is not null)
            {
                decReq.SetInputTensor(_decBeamName, Int32Tensor(new[] { 1L }, new[] { 0 }));
            }

            decReq.Infer();

            float[] logits = decReq.GetOutputTensor("logits").GetFloatData();
            int vocab = cur.Length > 0 ? logits.Length / cur.Length : 0;
            if (vocab == 0) break;
            long next = ArgMax(logits, (cur.Length - 1) * vocab, vocab);
            if (next == _token.EosId) break;
            ids.Add(next);
        }

        return ids.Skip(_token.Mono ? 3 : 4).Where(i => i != _token.EosId).Select(i => (int)i).ToArray();
    }

    /// <summary>One decoder step with just the SOT token; argmax over language tokens.</summary>
    private long DetectLanguage(float[] hidden, int hiddenDim)
    {
        try
        {
            using var decReq = _decoder.CreateInferRequest();
            decReq.SetInputTensor(_decIdsName, Int64Tensor(new[] { 1L, 1L }, new long[] { _token.SotId }));
            decReq.SetInputTensor(_decEncName, Float32Tensor(new[] { 1L, 1500L, (long)hiddenDim }, hidden));
            if (_decBeamName is not null)
            {
                decReq.SetInputTensor(_decBeamName, Int32Tensor(new[] { 1L }, new[] { 0 }));
            }

            decReq.Infer();

            float[] logits = decReq.GetOutputTensor("logits").GetFloatData();
            int vocab = logits.Length; // 1 token → full row
            var langIds = _token.LangIds.Values.Distinct().ToArray();
            long best = langIds[0];
            float bestScore = float.NegativeInfinity;
            foreach (int lid in langIds)
            {
                if (lid >= 0 && lid < vocab)
                {
                    float s = logits[lid];
                    if (s > bestScore)
                    {
                        bestScore = s;
                        best = lid;
                    }
                }
            }

            return best;
        }
        catch (Exception ex)
        {
            _log.Warn("Language detection failed ({0}); using English token.", ex.Message);
            return _token.LanguageId("en") >= 0 ? _token.LanguageId("en") : -1;
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

        _log.Info("Whisper(classic) started.");
    }

    public void Stop()
    {
        lock (_sync)
        {
            if (!_started) return;
            _started = false;
            // Flush the in-progress segment so the last sentence is not lost on stop.
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

        // Wait for in-flight transcriptions so OpenVINO resources are not released under them.
        Task[] pending;
        lock (_inFlightSync)
        {
            pending = _inFlight.Where(t => !t.IsCompleted).ToArray();
        }

        if (pending.Length > 0)
        {
            try { Task.WaitAll(pending, TimeSpan.FromSeconds(30)); } catch { }
        }

        _decoder.Dispose();
        TryDispose(_encoderNpu);
        TryDispose(_encoder);
        _core.Dispose();
        // NOTE: _transcribeGate is intentionally not disposed — a background Transcribe may
        // still touch it during shutdown, and SemaphoreSlim(1,1) holds no OS handle (no leak).
    }

    private static void TryDispose(CompiledModel? m)
    {
        try { m?.Dispose(); } catch { }
    }

    private static bool FileContains(string path, string needle)
    {
        try
        {
            return File.ReadAllText(path).Contains(needle, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private CompiledModel Compile(string modelDir, string file, string device)
    {
        var model = _core.ReadModel(Path.Combine(modelDir, file));
        try
        {
            return _core.CompileModel(model, device);
        }
        finally
        {
            model.Dispose();
        }
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
