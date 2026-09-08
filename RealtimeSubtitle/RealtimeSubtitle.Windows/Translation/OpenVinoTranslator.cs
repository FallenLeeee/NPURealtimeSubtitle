using System.Diagnostics;
using OpenVinoSharp;
using RealtimeSubtitle.Core.Diagnostics;
using RealtimeSubtitle.Core.Translation;

namespace RealtimeSubtitle.Windows;

/// <summary>
/// MarianMT (opus-mt) translation via the OpenVINO classic API, using the optimum-exported
/// encoder/decoder graphs (see tools/model-convert/out/opus-mt-en-zh-int8).
///
/// Device policy (decisions P3-2/P5-6): the exported graphs have dynamic shapes. The CPU
/// plugin runs them as-is; the NPU plugin requires fully static graphs.
///   - ENCODER: compiled ONCE on NPU at a fixed [1,64] shape; real sentences are padded to 64
///     with attention_mask 0, so the NPU compile is a single ~2.3s one-time cost per process
///     and every sentence after the first reuses it (BERT-style padding).
///   - DECODER: the optimum-exported decoder keeps dynamic beam-related nodes even after
///     reshaping all four inputs, and the NPU compiler rejects it ("to_shape was called on a
///     dynamic shape", verified by --npu-probe), so the decoder always runs on CPU.
/// Result: encoder NPU + decoder CPU hybrid. Any NPU compile failure falls back to all-CPU.
/// </summary>
public sealed class OpenVinoTranslator : ITranslator
{
    private const int MaxSrcLen = 64;
    private const int MaxDstLen = 64;
    private const int HiddenDim = 512;      // opus-mt hidden size (all Helsinki-NLP marian)
    private const int EncoderNpuLen = 64;   // fixed static shape for the NPU encoder

    private readonly OpenVinoSharp.Core _core;
    private readonly string _modelPath;
    private readonly CompiledModel _encoderCpu; // dynamic CPU encoder (fallback)
    private readonly CompiledModel _decoderCpu; // dynamic CPU decoder (always)
    private readonly CompiledModel? _encNpu;    // static [1,64] NPU encoder (when armed)
    private readonly MarianTokenizer _tokenizer;
    private readonly LogSink _log;
    private readonly string _device;   // resolved: "NPU+CPU" (hybrid) or "CPU"

    public OpenVinoTranslator(string modelPath, string device, LogSink? log = null)
    {
        _log = log ?? LogSink.Default;
        _modelPath = modelPath;
        _core = new OpenVinoSharp.Core();

        _device = "CPU";
        _encNpu = null;

        if (device is "NPU" or "auto")
        {
            OpenVinoDevice.ApplyCache(_core, "NPU", "GPU", "CPU");
            _log.Info("OpenVinoTranslator: compiling encoder [1,{0}] on NPU ...", EncoderNpuLen);
            try
            {
                _encNpu = CompileStaticNpu("openvino_encoder_model.xml",
                    ("input_ids", new long[] { 1, EncoderNpuLen }),
                    ("attention_mask", new long[] { 1, EncoderNpuLen }));
                _device = "NPU+CPU"; // encoder NPU, decoder CPU (decoder not NPU-compilable)
                _log.Info("OpenVinoTranslator: NPU encoder ready → hybrid NPU(enc)+CPU(dec).");
            }
            catch (Exception ex)
            {
                _log.Warn("OpenVinoTranslator: NPU encoder not usable ({0}); running on CPU.", ex.Message);
            }
        }

        _encoderCpu = Compile(modelPath, "openvino_encoder_model.xml", "CPU");
        _decoderCpu = Compile(modelPath, "openvino_decoder_model.xml", "CPU");

        string tokenizerPath = Path.Combine(modelPath, "tokenizer.json");
        _tokenizer = new MarianTokenizer(MarianTokenizerModel.Load(tokenizerPath));
        _log.Info("OpenVinoTranslator ready (device={0}).", _device);
    }

    public string Device => _device;

    public Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        string text;
        try
        {
            text = TranslateCore(request.SourceText, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Error("Translation failed for '{0}': {1}", request.SourceText, ex.Message);
            text = string.Empty;
        }

        sw.Stop();
        return Task.FromResult(new TranslationResult(text, _device, sw.Elapsed, FallbackToCpu: false));
    }

    public void Dispose()
    {
        TryDispose(_encNpu);
        TryDispose(_decoderCpu);
        TryDispose(_encoderCpu);
        _core.Dispose();
    }

    private static void TryDispose(CompiledModel? m)
    {
        try { m?.Dispose(); } catch { }
    }

    private CompiledModel Compile(string modelPath, string file, string device)
    {
        var model = _core.ReadModel(Path.Combine(modelPath, file));
        try
        {
            return _core.CompileModel(model, device);
        }
        finally
        {
            model.Dispose();
        }
    }

    /// <summary>Reads a fresh graph, reshapes the named inputs to fixed dims, compiles on NPU.</summary>
    private CompiledModel CompileStaticNpu(string file, params (string Name, long[] Dims)[] shapes)
    {
        var model = _core.ReadModel(Path.Combine(_modelPath, file));
        try
        {
            foreach ((string name, long[] dims) in shapes)
            {
                model.Reshape(name, dims);
            }

            return _core.CompileModel(model, "NPU");
        }
        finally
        {
            model.Dispose();
        }
    }

    private string TranslateCore(string sourceText, CancellationToken ct)
    {
        int[] srcIds = _tokenizer.Encode(sourceText);
        if (srcIds.Length == 0) return string.Empty;

        long eos = _tokenizer.Model.EosId;
        long decoderStart = _tokenizer.Model.DecoderStartId;
        // HF marian appends eos to the source; cap at MaxSrcLen-1 to leave room for it.
        int keep = Math.Min(srcIds.Length, MaxSrcLen - 1);
        int length = keep + 1;
        var inputIds = new long[length];
        var mask = new long[length];
        Array.Fill(mask, 1L);
        for (int i = 0; i < keep; i++) inputIds[i] = srcIds[i];
        inputIds[keep] = eos;

        // --- encoder ---
        float[] hidden;
        if (_encNpu is not null)
        {
            // NPU static path: pad the sentence to [1,64]; attention_mask=0 suppresses padding.
            var paddedIds = new long[EncoderNpuLen];
            var paddedMask = new long[EncoderNpuLen];
            Array.Copy(inputIds, paddedIds, length);
            Array.Fill(paddedMask, 1L, 0, length);

            using var encReq = _encNpu.CreateInferRequest();
            ct.ThrowIfCancellationRequested();
            encReq.SetInputTensor("input_ids", Int64Tensor(new[] { 1L, EncoderNpuLen }, paddedIds));
            encReq.SetInputTensor("attention_mask", Int64Tensor(new[] { 1L, EncoderNpuLen }, paddedMask));
            encReq.Infer();
            float[] allHidden = encReq.GetOutputTensor("last_hidden_state").GetFloatData(); // [1,64,512]

            // Take only the real positions (padding contributes nothing we need).
            hidden = new float[length * HiddenDim];
            Array.Copy(allHidden, 0, hidden, 0, hidden.Length);
        }
        else
        {
            using var encReq = _encoderCpu.CreateInferRequest();
            ct.ThrowIfCancellationRequested();
            encReq.SetInputTensor("input_ids", Int64Tensor(new[] { 1L, length }, inputIds));
            encReq.SetInputTensor("attention_mask", Int64Tensor(new[] { 1L, length }, mask));
            encReq.Infer();
            hidden = encReq.GetOutputTensor("last_hidden_state").GetFloatData(); // [1, L, 512]
        }

        int hiddenDim = _encNpu is not null ? HiddenDim : (length > 0 ? hidden.Length / length : 0);
        if (hiddenDim == 0) return string.Empty;

        // --- decoder (autoregressive, CPU) ---
        // The optimum marian export hides the KV cache inside the graph as OpenVINO states
        // (ReadValue, like the whisper `-with-past` export). A fresh InferRequest per sentence
        // starts with an empty cache; the prefill feeds [decoder_start] and every later step
        // feeds ONE token, so the cross-attention K/V are computed once (P6-8). The old code
        // created a new request per step — which reset the state — and re-fed the whole
        // prefix, making the decoder O(n²) work per sentence.
        var decIds = new List<long>();
        using var decReq = _decoderCpu.CreateInferRequest();
        decReq.SetInputTensor("encoder_hidden_states", Float32Tensor(new[] { 1L, (long)length, hiddenDim }, hidden));
        decReq.SetInputTensor("encoder_attention_mask", Int64Tensor(new[] { 1L, (long)length }, mask));
        decReq.SetInputTensor("beam_idx", Int32Tensor(new[] { 1L }, new[] { 0 }));

        long last = decoderStart;
        for (int step = 0; step < MaxDstLen; step++)
        {
            ct.ThrowIfCancellationRequested();
            decReq.SetInputTensor("input_ids", Int64Tensor(new[] { 1L, 1L }, new[] { last }));
            decReq.Infer();

            float[] logits = decReq.GetOutputTensor("logits").GetFloatData();
            if (!AppendArgMax(decIds, logits, 1, eos, _tokenizer)) break;
            last = decIds[^1];
            if (last == eos) break;
        }

        var generated = decIds.TakeWhile(id => id != eos).Select(i => (int)i).ToArray();
        string decoded = _tokenizer.Decode(generated);
        return CutRepeatedSuffix(decoded).TrimEnd(' ', ',', '，', '。', '.', '?', '?');
    }

    /// <summary>Appends the argmax token; returns false when generation must stop.</summary>
    private static bool AppendArgMax(List<long> decIds, float[] logits, int curLen, long eos, MarianTokenizer tokenizer)
    {
        int vocab = curLen > 0 ? logits.Length / curLen : 0;
        if (vocab == 0) return false;

        long next = ArgMax(logits, (curLen - 1) * vocab, vocab);
        decIds.Add(next);

        if (next == eos) return false;

        if (next >= 0 && next < tokenizer.Model.TgtPieces.Length)
        {
            string piece = tokenizer.Model.TgtPieces[(int)next];
            if (piece == "<unk>" || piece.Length == 0) return false;
        }

        return true;
    }

    private static string CutRepeatedSuffix(string text)
    {
        // opus-mt sometimes replays the first phrase before eos ("你要去哪里? 来,你要去哪里?").
        // Cut at the point where the head of the text reappears later.
        if (text.Length < 4) return text;

        for (int len = 3; len <= text.Length / 2; len++)
        {
            string head = text[..len];
            int idx = text.IndexOf(head, len, StringComparison.Ordinal);
            if (idx > 0)
            {
                return text[..idx];
            }
        }

        return text;
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
