using System.Diagnostics;
using OpenVinoSharp;
using RealtimeSubtitle.Core.Audio;
using RealtimeSubtitle.Core.Configuration;
using RealtimeSubtitle.Core.Diagnostics;
using RealtimeSubtitle.Core.Models;
using RealtimeSubtitle.Windows;

namespace RealtimeSubtitle.Tools;

/// <summary>
/// Phase 1 acceptance tool: captures N seconds of system audio (default render device) via
/// WASAPI loopback, down-mixes + resamples to 16 kHz mono, runs the VAD, writes a
/// 16 kHz mono PCM WAV file, and prints live RMS/VAD output.
///
/// Usage:
///   WavDumpTool [--seconds N] [--out out.wav] [--config path] [--help]
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (Array.Exists(args, a => a == "--probe-read"))
        {
            return ProbeReadAction(args);
        }

        if (Array.Exists(args, a => a == "--asr"))
        {
            return await AsrAction(args);
        }

        if (Array.Exists(args, a => a == "--translate"))
        {
            return await TranslateAction(args);
        }

        if (Array.Exists(args, a => a == "--bench"))
        {
            return await BenchAction(args);
        }

        if (Array.Exists(args, a => a == "--synth"))
        {
            return SynthAction(args);
        }

        if (Array.Exists(args, a => a == "--npu-probe"))
        {
            return NpuProbeAction(args);
        }

        if (Array.Exists(args, a => a == "--whisper-npu-probe"))
        {
            return WhisperNpuProbeAction(args);
        }

        if (Array.Exists(args, a => a == "--sensevoice"))
        {
            return await SensevoiceAction(args);
        }

        if (Array.Exists(args, a => a == "--qwen3"))
        {
            return await Qwen3Action(args);
        }

        if (Array.Exists(args, a => a == "--download-models"))
        {
            return await DownloadModelsAction(args);
        }

        double seconds = 10;
        string outPath = "out.wav";
        string? configPath = null;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--seconds" or "-s" when i + 1 < args.Length: seconds = double.Parse(args[++i]); break;
                case "--out" or "-o" when i + 1 < args.Length: outPath = args[++i]; break;
                case "--config" when i + 1 < args.Length: configPath = args[++i]; break;
                case "--help" or "-h":
                    Console.WriteLine("WavDumpTool [--seconds N] [--out out.wav] [--config path]");
                    return 0;
                default:
                    Console.Error.WriteLine($"Unknown argument: {args[i]}");
                    return 2;
            }
        }

        var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
            Console.WriteLine();
            Console.WriteLine("Stopping...");
        };

        return await RunAsync(seconds, outPath, configPath, cts.Token);
    }

    /// <summary>
    /// Usage: <c>WavDumpTool --asr &lt;wav&gt; [--device auto|NPU|CPU] [--model &lt;dir&gt;]</c>.
    /// Transcribes a 16 kHz mono PCM16 WAV with the Whisper/OpenVINO backend (decision P2-3)
    /// and prints every finalized segment plus wall-clock latency.
    /// </summary>
    private static int ProbeReadAction(string[] args)
    {
        string path = args.Length > 1 ? args[1] : string.Empty;
        if (path.Length == 0)
        {
            Console.Error.WriteLine("Usage: WavDumpTool --probe-read <model.xml>");
            return 2;
        }

        try
        {
            using var core = new OpenVinoSharp.Core();
            using var model = core.ReadModel(Path.GetFullPath(path));
            Console.WriteLine($"READ OK (model loaded)");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"READ FAILED: {ex}");
            return 1;
        }
    }

    private static async Task<int> AsrAction(string[] args)
    {
        string wavPath = string.Empty;
        string device = "auto";
        string language = "auto";
        string modelPath = Path.Combine("tools", "model-convert", "out", "whisper-base-int8");

        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--device" when i + 1 < args.Length: device = args[++i]; break;
                case "--lang" when i + 1 < args.Length: language = args[++i]; break;
                case "--model" when i + 1 < args.Length: modelPath = args[++i]; break;
                case "--help" or "-h":
                    Console.WriteLine("WavDumpTool --asr <wav> [--device auto|NPU|CPU] [--lang auto|en|zh|ja] [--model <dir>]");
                    return 0;
                default:
                    if (wavPath.Length == 0) wavPath = args[i];
                    break;
            }
        }

        if (!File.Exists(wavPath))
        {
            Console.Error.WriteLine($"WAV not found: {wavPath}");
            return 2;
        }

        if (!Directory.Exists(modelPath))
        {
            Console.Error.WriteLine($"Whisper model dir not found: {modelPath}");
            return 2;
        }

        byte[] pcm = ReadPcm16Wave(wavPath);
        Console.WriteLine($"Feeding {pcm.Length / 2} samples ({(pcm.Length / 2) / 16000.0:F1}s) to Whisper(classic) on {device} (lang={language})...");

        var log = new LogSink { MinLevel = LogLevel.Info };
        log.LineWritten += line => Console.WriteLine(line);
        using var recognizer = new RealtimeSubtitle.Windows.OpenVinoWhisperClassicRecognizer(modelPath, device, language, log);
        var results = new List<(string Text, TimeSpan Latency)>();
        DateTimeOffset started = DateTimeOffset.UtcNow;
        var gate = new TaskCompletionSource<bool>();

        recognizer.Final += f =>
        {
            lock (results) results.Add((f.Text, DateTimeOffset.UtcNow - f.Timestamp));
            gate.TrySetResult(true);
        };
        recognizer.Error += ex => Console.Error.WriteLine($"ASR error: {ex.Message}");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        recognizer.Start(cts.Token);

        for (int off = 0; off < pcm.Length; off += 4096)
        {
            int len = Math.Min(4096, pcm.Length - off);
            recognizer.WritePcm16(pcm.AsSpan(off, len), DateTimeOffset.UtcNow);
        }

        // The recognizer's VAD finalizes segments as silence is detected after feeding;
        // wait briefly for pending transcriptions, then stop. Larger models transcribe
        // slower (whisper-small: ~1-3 s/segment), so allow more time.
        try { await Task.Delay(12000, cts.Token); } catch (OperationCanceledException) { }
        recognizer.Stop();
        await Task.Delay(1500); // drain in-flight transcriptions after Stop

        Console.WriteLine($"\n=== Segments ({results.Count}) ===");
        foreach ((string text, TimeSpan latency) in results)
        {
            Console.WriteLine($"  [{latency.TotalMilliseconds,6:N0} ms] {text}");
        }

        Console.WriteLine($"Total wall: {(DateTimeOffset.UtcNow - started).TotalMilliseconds:N0} ms.");
        return 0;
    }

    private static async Task<int> TranslateAction(string[] args)
    {
        string text = string.Empty;
        string modelPath = Path.Combine("tools", "model-convert", "out", "opus-mt-en-zh-int8");
        string device = "auto";

        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--model" when i + 1 < args.Length: modelPath = args[++i]; break;
                case "--device" when i + 1 < args.Length: device = args[++i]; break;
                case "--help" or "-h":
                    Console.WriteLine("WavDumpTool --translate \"text\" [--model <dir>] [--device auto|NPU|CPU]");
                    return 0;
                default:
                    if (text.Length == 0) text = args[i];
                    break;
            }
        }

        if (text.Length == 0)
        {
            Console.Error.WriteLine("--translate requires text.");
            return 2;
        }

        if (!Directory.Exists(modelPath))
        {
            Console.Error.WriteLine($"Marian model dir not found: {modelPath}");
            return 2;
        }

        using var translator = new RealtimeSubtitle.Windows.OpenVinoTranslator(modelPath, device);
        var result = await translator.TranslateAsync(
            new RealtimeSubtitle.Core.Translation.TranslationRequest(text, DateTimeOffset.UtcNow));
        Console.WriteLine($"device: {result.Device}  latency: {result.Elapsed.TotalMilliseconds:N0} ms");
        Console.WriteLine($"src : {text}");
        Console.WriteLine($"tgt : {result.TargetText}");
        return result.TargetText.Length > 0 ? 0 : 1;
    }

    private static async Task<int> BenchAction(string[] args)
    {
        string modelPath = Path.Combine("tools", "model-convert", "out", "opus-mt-en-zh-int8");
        string? forceDevice = null;
        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--model" when i + 1 < args.Length: modelPath = args[++i]; break;
                case "--device" when i + 1 < args.Length: forceDevice = args[++i]; break;
            }
        }

        if (!Directory.Exists(modelPath))
        {
            Console.Error.WriteLine($"Marian model dir not found: {modelPath}");
            return 2;
        }

        // With --device: measure that single device. Without: DeviceSelector compares NPU vs CPU
        // and picks the faster (NPU static shapes are probed at load, P5-6).
        if (forceDevice is not null)
        {
            Func<string, RealtimeSubtitle.Core.Translation.ITranslator> factory = dev =>
                new RealtimeSubtitle.Windows.OpenVinoTranslator(modelPath, dev);
            var progress = new Progress<string>(s => Console.WriteLine(s));
            var (device, npuMs, cpuMs, npuFailed) =
                await RealtimeSubtitle.Core.Translation.DeviceSelector.SelectAsync(factory, progress);
            Console.WriteLine($"\nRESULT (--device {forceDevice}) device={device} npuMs={(npuMs < 0 ? "n/a" : npuMs.ToString("N0"))} cpuMs={(cpuMs < 0 ? "n/a" : cpuMs.ToString("N0"))} npuFailed={npuFailed}");
            return 0;
        }

        Func<string, RealtimeSubtitle.Core.Translation.ITranslator> factory2 = dev =>
            new RealtimeSubtitle.Windows.OpenVinoTranslator(modelPath, dev);
        var progress2 = new Progress<string>(s => Console.WriteLine(s));
        var (device2, npuMs2, cpuMs2, npuFailed2) =
            await RealtimeSubtitle.Core.Translation.DeviceSelector.SelectAsync(factory2, progress2);

        Console.WriteLine($"\nRESULT device={device2} npuMs={(npuMs2 < 0 ? "n/a" : npuMs2.ToString("N0"))} cpuMs={(cpuMs2 < 0 ? "n/a" : cpuMs2.ToString("N0"))} npuFailed={npuFailed2}");
        Console.WriteLine("NOTE: NPU uses static-shape per-length compilation (P5-6); first sentence pays the compile cost.");
        return 0;
    }

    /// <summary>
    /// Usage: <c>WavDumpTool --npu-probe &lt;modelDir&gt;</c>.
    /// Independently verifies that the marian encoder/decoder graphs can be statically reshaped
    /// and compiled for the NPU on this machine, before the translator runs. Native vpux crashes
    /// surface here in isolation.
    /// </summary>
    private static int NpuProbeAction(string[] args)
    {
        string modelPath = args.Length > 1 ? args[1] : Path.Combine("tools", "model-convert", "out", "opus-mt-en-zh-int8");
        if (!Directory.Exists(modelPath))
        {
            Console.Error.WriteLine($"Marian model dir not found: {modelPath}");
            return 2;
        }

        try
        {
            using var core = new OpenVinoSharp.Core();
            var sw = Stopwatch.StartNew();

            Console.WriteLine("Probing encoder (reshape [1,16] → compile NPU) ...");
            var encModel = core.ReadModel(Path.Combine(modelPath, "openvino_encoder_model.xml"));
            encModel.Reshape("input_ids", new long[] { 1, 16 });
            encModel.Reshape("attention_mask", new long[] { 1, 16 });
            using (var encCompiled = core.CompileModel(encModel, "NPU"))
            {
                var req = encCompiled.CreateInferRequest();
                req.SetInputTensor("input_ids", Int64Tensor(new long[] { 1, 16 }, new long[16]));
                req.SetInputTensor("attention_mask", Int64Tensor(new long[] { 1, 16 }, Enumerable.Repeat(1L, 16).ToArray()));
                req.Infer();
                Console.WriteLine($"  encoder NPU OK ({sw.ElapsedMilliseconds} ms total so far)");
            }

            Console.WriteLine("Probing decoder (reshape input[1,4] hidden[1,16,512] → compile NPU) ...");
            var decModel = core.ReadModel(Path.Combine(modelPath, "openvino_decoder_model.xml"));
            decModel.Reshape("input_ids", new long[] { 1, 4 });
            decModel.Reshape("encoder_hidden_states", new long[] { 1, 16, 512 });
            decModel.Reshape("encoder_attention_mask", new long[] { 1, 16 });
            decModel.Reshape("beam_idx", new long[] { 1 });

            using (var decCompiled = core.CompileModel(decModel, "NPU"))
            {
                var req = decCompiled.CreateInferRequest();
                req.SetInputTensor("input_ids", Int64Tensor(new long[] { 1, 4 }, new long[] { 65000, 3, 3, 3 }));
                req.SetInputTensor("encoder_hidden_states", Float32Tensor(new long[] { 1, 16, 512 }, new float[16 * 512]));
                req.SetInputTensor("encoder_attention_mask", Int64Tensor(new long[] { 1, 16 }, Enumerable.Repeat(1L, 16).ToArray()));
                req.SetInputTensor("beam_idx", Int32Tensor(new long[] { 1 }, new[] { 0 }));
                req.Infer();
                Console.WriteLine($"  decoder NPU OK ({sw.ElapsedMilliseconds} ms total so far)");
            }

            Console.WriteLine($"NPU PROBE: PASS ({sw.ElapsedMilliseconds} ms)");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"NPU PROBE: FAILED — {ex}");
            return 1;
        }
    }

    /// <summary>
    /// Usage: <c>WavDumpTool --whisper-npu-probe &lt;modelDir&gt;</c>.
    /// Verifies the Whisper encoder can be statically reshaped ([1,80,3000]) and compiled on
    /// the NPU (same trick as the marian encoder); the decoder is expected to stay on CPU
    /// (autoregressive dynamic). Runs isolated so a vpux native crash cannot take down the app.
    /// </summary>
    private static int WhisperNpuProbeAction(string[] args)
    {
        string modelPath = args.Length > 1
            ? args[1]
            : Path.Combine("tools", "model-convert", "out", "whisper-base-int8");
        if (!Directory.Exists(modelPath))
        {
            Console.Error.WriteLine($"Whisper model dir not found: {modelPath}");
            return 2;
        }

        try
        {
            using var core = new OpenVinoSharp.Core();
            var sw = Stopwatch.StartNew();

            Console.WriteLine("=== whisper encoder (reshape [1,80,3000] → NPU) ===");
            var enc = core.ReadModel(Path.Combine(modelPath, "openvino_encoder_model.xml"));
            foreach (var inp in enc.Inputs)
            {
                Console.WriteLine($"  IN {inp.GetAnyName()} {inp.get_partial_shape()}");
            }

            enc.Reshape("input_features", new long[] { 1, 80, 3000 });
            using (var c = core.CompileModel(enc, "NPU"))
            {
                var req = c.CreateInferRequest();
                req.SetInputTensor("input_features", Float32Tensor(new long[] { 1, 80, 3000 }, new float[80 * 3000]));
                req.Infer();
                Console.WriteLine($"  encoder NPU OK ({sw.ElapsedMilliseconds} ms)");
            }

            Console.WriteLine("=== whisper decoder (reshape static → NPU) ===");
            var dec = core.ReadModel(Path.Combine(modelPath, "openvino_decoder_model.xml"));
            foreach (var inp in dec.Inputs)
            {
                Console.WriteLine($"  IN {inp.GetAnyName()} {inp.get_partial_shape()}");
            }

            dec.Reshape("input_ids", new long[] { 1, 4 });
            dec.Reshape("encoder_hidden_states", new long[] { 1, 1500, 384 });
            using (var c = core.CompileModel(dec, "NPU"))
            {
                var req = c.CreateInferRequest();
                req.SetInputTensor("input_ids", Int64Tensor(new long[] { 1, 4 }, new long[] { 50258, 50359, 50363, 50257 }));
                req.SetInputTensor("encoder_hidden_states", Float32Tensor(new long[] { 1, 1500, 384 }, new float[1500 * 384]));
                req.Infer();
                Console.WriteLine($"  decoder NPU OK ({sw.ElapsedMilliseconds} ms)");
            }

            Console.WriteLine($"WHISPER NPU PROBE: PASS ({sw.ElapsedMilliseconds} ms)");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"WHISPER NPU PROBE: FAILED — {ex.Message.Split('\n')[0]}");
            return 1;
        }
    }

    private static Tensor Int64Tensor(long[] dims, long[] data)
    {
        var t = new OpenVinoSharp.Tensor(new OpenVinoSharp.Shape(dims), OpenVinoSharp.ElementType.I64);
        t.SetData(data);
        return t;
    }

    private static Tensor Int32Tensor(long[] dims, int[] data)
    {
        var t = new OpenVinoSharp.Tensor(new OpenVinoSharp.Shape(dims), OpenVinoSharp.ElementType.I32);
        t.SetData(data);
        return t;
    }

    private static Tensor Float32Tensor(long[] dims, float[] data)
    {
        var t = new OpenVinoSharp.Tensor(new OpenVinoSharp.Shape(dims), OpenVinoSharp.ElementType.F32);
        t.SetData(data);
        return t;
    }

    /// <summary>
    /// Usage: <c>WavDumpTool --download-models [--dir &lt;installRoot&gt;]</c>.
    /// Provisions every catalog model (install dir → checkout out/ dir → offline dist zip →
    /// Models.BaseUrl) and prints the resolved location for each. <c>--dir</c> overrides the
    /// install root, useful to stage models into a clean folder.
    /// </summary>
    /// <summary>
    /// Usage: <c>WavDumpTool --sensevoice &lt;wav&gt; [--model &lt;dir&gt;]</c>.
    /// Transcribes with the SenseVoiceSmall ONNX backend (single-pass CTC).
    /// </summary>
    private static async Task<int> SensevoiceAction(string[] args)
    {
        string wavPath = string.Empty;
        string modelPath = Path.Combine("tools", "model-convert", "out", "sensevoice-small-int8");
        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--model" when i + 1 < args.Length: modelPath = args[++i]; break;
                case "--help" or "-h":
                    Console.WriteLine("WavDumpTool --sensevoice <wav> [--model <dir>]");
                    return 0;
                default:
                    if (wavPath.Length == 0) wavPath = args[i];
                    break;
            }
        }

        if (!File.Exists(wavPath) || !Directory.Exists(modelPath))
        {
            Console.Error.WriteLine($"Usage: --sensevoice <wav> --model <dir> (missing file/dir)");
            return 2;
        }

        byte[] pcm = ReadPcm16Wave(wavPath);
        var log = new LogSink { MinLevel = LogLevel.Info };
        log.LineWritten += line => Console.WriteLine(line);
        using var recognizer = new RealtimeSubtitle.Windows.OpenVinoSenseVoiceRecognizer(modelPath, log);
        var results = new List<string>();
        recognizer.Final += f => { lock (results) results.Add(f.Text); };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        recognizer.Start(cts.Token);
        for (int off = 0; off < pcm.Length; off += 4096)
        {
            int len = Math.Min(4096, pcm.Length - off);
            recognizer.WritePcm16(pcm.AsSpan(off, len), DateTimeOffset.UtcNow);
        }

        await Task.Delay(3000, cts.Token);
        recognizer.Stop();
        await Task.Delay(1000);

        Console.WriteLine($"\n=== SenseVoice segments ({results.Count}) ===");
        foreach (string text in results) Console.WriteLine($"  {text}");
        return results.Count > 0 ? 0 : 1;
    }

    /// <summary>
    /// Usage: <c>WavDumpTool --qwen3 &lt;wav&gt; [--model &lt;dir&gt;] [--lang zh|en|ja]</c>.
    /// Transcribes with the Qwen3-ASR backend (audio encoder + Qwen3-1.7B KV decode).
    /// </summary>
    private static async Task<int> Qwen3Action(string[] args)
    {
        string wavPath = string.Empty;
        string modelPath = Path.Combine("tools", "model-convert", "out", "qwen3-asr-1.7b-int8");
        string language = "zh";
        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--model" when i + 1 < args.Length: modelPath = args[++i]; break;
                case "--lang" when i + 1 < args.Length: language = args[++i]; break;
                case "--help" or "-h":
                    Console.WriteLine("WavDumpTool --qwen3 <wav> [--model <dir>] [--lang zh|en|ja]");
                    return 0;
                default:
                    if (wavPath.Length == 0) wavPath = args[i];
                    break;
            }
        }

        if (!File.Exists(wavPath) || !Directory.Exists(modelPath))
        {
            Console.Error.WriteLine("Usage: --qwen3 <wav> --model <dir>");
            return 2;
        }

        byte[] pcm = ReadPcm16Wave(wavPath);
        var log = new LogSink { MinLevel = LogLevel.Info };
        log.LineWritten += line => Console.WriteLine(line);
        using var recognizer = new RealtimeSubtitle.Windows.OpenVinoQwen3AsrRecognizer(modelPath, language, log);
        var results = new List<string>();
        recognizer.Final += f => { lock (results) results.Add(f.Text); };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        recognizer.Start(cts.Token);
        for (int off = 0; off < pcm.Length; off += 4096)
        {
            int len = Math.Min(4096, pcm.Length - off);
            recognizer.WritePcm16(pcm.AsSpan(off, len), DateTimeOffset.UtcNow);
        }

        await Task.Delay(6000, cts.Token);
        recognizer.Stop();
        await Task.Delay(2000);

        Console.WriteLine($"\n=== Qwen3 segments ({results.Count}) ===");
        foreach (string text in results) Console.WriteLine($"  {text}");
        return results.Count > 0 ? 0 : 1;
    }

    private static async Task<int> DownloadModelsAction(string[] args)
    {
        string? installRoot = null;
        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--dir" when i + 1 < args.Length: installRoot = args[++i]; break;
                case "--help" or "-h":
                    Console.WriteLine("WavDumpTool --download-models [--dir <installRoot>]");
                    return 0;
                default:
                    Console.Error.WriteLine($"Unknown argument: {args[i]}");
                    return 2;
            }
        }

        var (config, issues) = AppConfigLoader.Load();
        foreach (string issue in issues) Console.Error.WriteLine($"[config] {issue}");

        var provisioner = string.IsNullOrWhiteSpace(installRoot)
            ? new ModelProvisioner(config.Models)
            : new ModelProvisioner(new ModelsConfig { Directory = installRoot, BaseUrl = config.Models.BaseUrl });

        Console.WriteLine($"Install root: {provisioner.InstallRoot}");
        int rc = 0;
        foreach (ModelEntry entry in ModelCatalog.All)
        {
            try
            {
                var progress = new Progress<ModelProgress>(p =>
                {
                    Console.Write($"\r  {entry.Id,-20} {p.Stage,-32} {p.Fraction:P0}   ");
                });
                string dir = await provisioner.EnsureAsync(entry.Id, progress);
                Console.WriteLine($"\r  {entry.Id,-20} OK → {dir}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"\r  {entry.Id,-20} FAILED: {ex.Message}");
                rc = 1;
            }
        }

        return rc;
    }

    private static byte[] ReadPcm16Wave(string path)
    {
        byte[] all = File.ReadAllBytes(path);
        if (all.Length < 12 || System.Text.Encoding.ASCII.GetString(all, 0, 4) != "RIFF"
                            || System.Text.Encoding.ASCII.GetString(all, 8, 4) != "WAVE")
        {
            throw new InvalidDataException("Not a RIFF/WAVE file.");
        }

        // Walk chunks from offset 12 (handles variable-length fmt/fact chunks, 44-byte
        // classic headers and extensible ones alike).
        int pos = 12;
        while (pos + 8 <= all.Length)
        {
            string id = System.Text.Encoding.ASCII.GetString(all, pos, 4);
            int size = BitConverter.ToInt32(all, pos + 4);
            int body = pos + 8;
            if (id == "data")
            {
                int available = Math.Max(0, Math.Min(size, all.Length - body));
                return all.AsSpan(body, available).ToArray();
            }

            pos = body + size + (size & 1); // chunks are word-aligned
        }

        throw new InvalidDataException("No 'data' chunk found in WAVE file.");
    }

    /// <summary>
    /// Usage: <c>WavDumpTool --synth "text to speak" --out speech.wav</c>.
    /// Synthesizes local test speech (16 kHz mono PCM16) with the OS SAPI voices.
    /// </summary>
    private static int SynthAction(string[] args)
    {
        string text = string.Empty;
        string outPath = "speech.wav";
        string? voiceName = null;
        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--text" when i + 1 < args.Length: text = args[++i]; break;
                case "--out" or "-o" when i + 1 < args.Length: outPath = args[++i]; break;
                case "--voice" when i + 1 < args.Length: voiceName = args[++i]; break;
                case "--help" or "-h":
                    Console.WriteLine("WavDumpTool --synth \"text\" --out speech.wav [--voice \"Microsoft Huihui Desktop\"]");
                    return 0;
                default:
                    if (text.Length == 0) text = args[i];
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            Console.Error.WriteLine("--synth requires text.");
            return 2;
        }

        string? wavDir = Path.GetDirectoryName(Path.GetFullPath(outPath));
        if (!string.IsNullOrEmpty(wavDir)) Directory.CreateDirectory(wavDir);

        using var synth = new System.Speech.Synthesis.SpeechSynthesizer();
        if (voiceName is not null)
        {
            var voice = synth.GetInstalledVoices().FirstOrDefault(v =>
                v.VoiceInfo.Name.Equals(voiceName, StringComparison.OrdinalIgnoreCase));
            if (voice is null)
            {
                Console.Error.WriteLine($"Voice '{voiceName}' not found. Installed:");
                foreach (var v in synth.GetInstalledVoices()) Console.Error.WriteLine($"  {v.VoiceInfo.Name} ({v.VoiceInfo.Culture})");
                return 2;
            }

            synth.SelectVoice(voice.VoiceInfo.Name);
        }

        var format = new System.Speech.AudioFormat.SpeechAudioFormatInfo(
            System.Speech.AudioFormat.EncodingFormat.Pcm, 16000, 16, 1,
            averageBytesPerSecond: 32000, blockAlign: 2, formatSpecificData: null);
        synth.SetOutputToWaveFile(outPath, format);
        synth.Speak(text);
        Console.WriteLine($"Wrote synthetic speech to {outPath} (16 kHz mono PCM16).");
        return 0;
    }

    private static async Task<int> RunAsync(double seconds, string outPath, string? configPath, CancellationToken token)
    {
        var (config, issues) = AppConfigLoader.Load(configPath);
        foreach (string issue in issues)
        {
            Console.Error.WriteLine($"[config] {issue}");
        }

        string logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RealtimeSubtitle", "logs");
        string logFile = Path.Combine(logDir, $"wavdump-{DateTime.Now:yyyyMMdd-HHmmss}.log");
        LogSink.Default.LogFile = logFile;
        LogSink.Default.LineWritten += line => Console.WriteLine(line);
        LogSink.Default.Info("WavDumpTool starting; log: {0}", logFile);

        using var toolCts = CancellationTokenSource.CreateLinkedTokenSource(token);

        using var capturer = new WasapiLoopbackCapturer(LogSink.Default);
        capturer.Error += ex =>
        {
            LogSink.Default.Error("Capture error: {0}", ex);
            toolCts.Cancel();
        };

        capturer.Start(toolCts.Token);

        AudioFormat mix = capturer.Format;
        int mixRate = mix.SampleRate;
        int mixChannels = mix.Channels;
        if (mixChannels == 0 || mixRate == 0)
        {
            LogSink.Default.Error("Invalid mix format: {0} Hz / {1} ch", mixRate, mixChannels);
            return 1;
        }

        var resampler = new AudioResampler(mixChannels, mixRate, outputSampleRate: 16000);
        var vad = new VoiceActivityDetector(
            config.Vad.Threshold, config.Vad.MinSpeechMs, config.Vad.HangoverMs, sampleRate: 16000);

        int chunkSamples = Math.Max(1, mixRate / 100) * mixChannels; // ~10 ms mix frames
        var input = new float[chunkSamples];
        var output = new float[resampler.MaxOutputLength(chunkSamples)];
        var pcm = new short[output.Length];
        string? wavDir = Path.GetDirectoryName(Path.GetFullPath(outPath));
        if (!string.IsNullOrEmpty(wavDir)) Directory.CreateDirectory(wavDir);

        using var wav = new WavWriter(outPath, 16000, channels: 1, bitsPerSample: 16);

        double speechSeconds = 0;
        double rmsAccum = 0;
        long voicedChunks = 0;
        long totalChunks = 0;
        DateTimeOffset start = DateTimeOffset.UtcNow;
        DateTimeOffset lastPrint = start;

        LogSink.Default.Info("Capturing {0:F1}s of system audio as {1} (16 kHz mono PCM)...", seconds, outPath);

        while (!toolCts.IsCancellationRequested)
        {
            double elapsed = (DateTimeOffset.UtcNow - start).TotalSeconds;
            if (elapsed >= seconds) break;

            int n = capturer.Buffer.TryRead(input);
            if (n == 0)
            {
                await Task.Delay(2, toolCts.Token);
                continue;
            }

            int resampled = resampler.Process(input.AsSpan(0, n), output);
            totalChunks++;

            var vadResult = vad.Process(output.AsSpan(0, resampled));

            for (int i = 0; i < resampled; i++)
            {
                float s = Math.Clamp(output[i], -1f, 1f);
                pcm[i] = (short)(s * short.MaxValue);
            }

            wav.WritePcm16(pcm.AsSpan(0, resampled));

            if (vadResult.State == VadState.Speech)
            {
                speechSeconds += resampled / 16000.0;
                rmsAccum += vadResult.Rms;
                voicedChunks++;
            }

            if (DateTimeOffset.UtcNow - lastPrint >= TimeSpan.FromMilliseconds(500))
            {
                lastPrint = DateTimeOffset.UtcNow;
                string state = vadResult.State == VadState.Speech ? "SPEECH " : "silence";
                Console.WriteLine($"t={elapsed,5:F1}s rms={vadResult.Rms,7:F4} [{state}] buffered={capturer.Buffer.Count,6}");
            }
        }

        capturer.Stop();

        // Drain remaining buffered samples.
        while (true)
        {
            int n = capturer.Buffer.TryRead(input);
            if (n == 0) break;

            int resampled = resampler.Process(input.AsSpan(0, n), output);
            for (int i = 0; i < resampled; i++)
            {
                float s = Math.Clamp(output[i], -1f, 1f);
                pcm[i] = (short)(s * short.MaxValue);
            }

            wav.WritePcm16(pcm.AsSpan(0, resampled));
        }

        double speechRatio = totalChunks == 0 ? 0 : (double)voicedChunks / totalChunks;
        double avgRms = voicedChunks == 0 ? 0 : rmsAccum / voicedChunks;
        LogSink.Default.Info(
            "Done. SpeechSeconds={0:F1} SpeechRatio={1:P1} AvgVoicedRms={2:F4} Chunks={3} WavBytes={4}",
            speechSeconds, speechRatio, avgRms, totalChunks, wav.DataBytes);
        Console.WriteLine($"Wrote {outPath} ({wav.DataBytes} bytes, 16 kHz mono PCM).");

        return toolCts.IsCancellationRequested ? 1 : 0;
    }
}