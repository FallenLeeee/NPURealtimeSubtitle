using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using RealtimeSubtitle.Core.Diagnostics;
using RealtimeSubtitle.Core.Models;
using RealtimeSubtitle.Core.Speech;
using RealtimeSubtitle.Overlay.Wpf;
using RealtimeSubtitle.Windows;

namespace RealtimeSubtitle.App;

/// <summary>
/// WinUI 3 app entry point. Supports a headless --ai-probe mode used during Phase 2
/// verification (the AI APIs require package identity, so the probe lives inside the
/// packaged app instead of a separate console tool — see docs/decisions.md P2-2).
/// </summary>
public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        string[] cmd = Environment.GetCommandLineArgs();
        int probeIndex = Array.IndexOf(cmd, "--ai-probe");
        if (probeIndex >= 0)
        {
            RunAiProbeAsync(cmd, probeIndex);
            return;
        }

        int setupIndex = Array.IndexOf(cmd, "--ai-setup");
        if (setupIndex >= 0)
        {
            RunAiSetupAsync(cmd, setupIndex);
            return;
        }

        int demoIndex = Array.IndexOf(cmd, "--demo");
        if (demoIndex >= 0)
        {
            RunDemoModeAsync(cmd, demoIndex);
            return;
        }

        int liveIndex = Array.IndexOf(cmd, "--live");
        if (liveIndex >= 0)
        {
            RunLiveModeAsync(cmd, liveIndex);
            return;
        }

        int overlayIndex = Array.IndexOf(cmd, "--overlay-probe");
        if (overlayIndex >= 0)
        {
            RunOverlayProbeAsync(cmd, overlayIndex);
            return;
        }

        _window = new MainWindow();
        _window.Activate();
    }

    /// <summary>
    /// Usage: <c>--demo &lt;transcript&gt; [--model &lt;marianDir&gt;] [--probe &lt;out&gt;] [--seconds N]</c>.
    /// Runs the full Phase 4 pipeline (demo feed → marian translation → overlay) headless,
    /// optionally emitting a window-style/snapshot report, then exits.
    /// </summary>
    private static async void RunDemoModeAsync(string[] cmd, int startIndex)
    {
        string? transcript = null, modelDir = null, probeOut = null;
        double seconds = 25;
        for (int i = startIndex + 1; i < cmd.Length; i++)
        {
            switch (cmd[i])
            {
                case "--model" when i + 1 < cmd.Length: modelDir = cmd[++i]; break;
                case "--probe" when i + 1 < cmd.Length: probeOut = cmd[++i]; break;
                case "--seconds" when i + 1 < cmd.Length: double.TryParse(cmd[++i], out seconds); break;
                default:
                    if (transcript is null) transcript = cmd[i];
                    break;
            }
        }

        modelDir ??= ResolveModelDir(ModelCatalog.OpusMtEnZhInt8, Path.Combine("tools", "model-convert", "out", "opus-mt-en-zh-int8"));
        int exitCode = 1;
        try
        {
            if (transcript is null || !File.Exists(transcript) || !Directory.Exists(modelDir))
            {
                File.WriteAllText(probeOut ?? "demo-report.txt", $"DEMO_MISSING transcript={transcript} model={modelDir}");
                return;
            }

            var log = new LogSink { MinLevel = LogLevel.Info };
            using var services = Services.AppServices.CreateDemo(transcript, modelDir, log);
            SubtitleOverlayHost.Start(services.Subtitles.Config);
            services.Subtitles.CurrentChanged += s => SubtitleOverlayHost.PushSnapshot(s);
            services.Start();

            await Task.Delay(TimeSpan.FromSeconds(seconds));

            var hwnd = SubtitleOverlayHost.Hwnd;
            var snap = SubtitleOverlayHost.CurrentSnapshot;
            string report =
                $"STYLES={WindowsInterop.DescribeStyle(hwnd)}\n" +
                $"RECT={WindowsInterop.DescribeRect(hwnd)}\n" +
                $"LINES={snap?.SourceText ?? ""}|{snap?.TranslatedText ?? ""}\n" +
                $"TRANSLATED={services.Subtitles.Current?.TranslatedText ?? ""}\n";
            File.WriteAllText(probeOut ?? "demo-report.txt", report);
            log.Info("Demo done:\n{0}", report);
            exitCode = 0;
        }
        catch (Exception ex)
        {
            File.WriteAllText(probeOut ?? "demo-report.txt", $"DEMO_FAILED=1\nERROR={ex}");
        }
        finally
        {
            SubtitleOverlayHost.Stop();
            Environment.Exit(exitCode);
        }
    }

    /// <summary>
    /// Usage: <c>--live [--model &lt;marianDir&gt;] [--whisper &lt;whisperDir&gt;] [--probe &lt;out&gt;] [--seconds N]</c>.
    /// Runs the full real pipeline (loopback → Whisper → translation → overlay) headless.
    /// </summary>
    private static async void RunLiveModeAsync(string[] cmd, int startIndex)
    {
        string? modelDir = null, whisperDir = null, probeOut = null;
        double seconds = 25;
        for (int i = startIndex + 1; i < cmd.Length; i++)
        {
            switch (cmd[i])
            {
                case "--model" when i + 1 < cmd.Length: modelDir = cmd[++i]; break;
                case "--whisper" when i + 1 < cmd.Length: whisperDir = cmd[++i]; break;
                case "--probe" when i + 1 < cmd.Length: probeOut = cmd[++i]; break;
                case "--seconds" when i + 1 < cmd.Length: double.TryParse(cmd[++i], out seconds); break;
            }
        }

        // ASR and translation models are language-routed (P6-4/P6-10).
        var (cfg, _) = RealtimeSubtitle.Core.Configuration.AppConfigLoader.Load();
        string transModelId = ModelCatalog.TranslationModelId(cfg.Asr.Language ?? "auto");
        string transFallbackDir = Path.Combine("tools", "model-convert", "out", transModelId);
        modelDir ??= ResolveModelDir(transModelId, transFallbackDir);
        string asrModelId = ModelCatalog.AsrModelId(cfg.Asr.Language ?? "auto", cfg.Asr.Model);
        string fallbackDir = Path.Combine("tools", "model-convert", "out", "whisper-base-int8");
        whisperDir ??= ResolveModelDir(asrModelId, fallbackDir);
        int exitCode = 1;
        try
        {
            if (!Directory.Exists(modelDir) || !Directory.Exists(whisperDir))
            {
                File.WriteAllText(probeOut ?? "live-report.txt", $"LIVE_MISSING model={modelDir} whisper={whisperDir}");
                return;
            }

            // P6-20: --live probe also writes the rolling file log (same sink as the GUI) so
            // headless tests are inspectable afterwards (P6-16 translation lines included).
            var log = new LogSink { MinLevel = LogLevel.Info };
            try
            {
                log.LogFile = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "RealtimeSubtitle", "logs", $"app-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            }
            catch { }

            using var services = Services.AppServices.CreateLive(modelDir, whisperDir, log);
            SubtitleOverlayHost.Start(services.Subtitles.Config);
            services.Subtitles.CurrentChanged += s => SubtitleOverlayHost.PushSnapshot(s);
            services.Start();

            await Task.Delay(TimeSpan.FromSeconds(seconds));

            var hwnd = SubtitleOverlayHost.Hwnd;
            var snap = SubtitleOverlayHost.CurrentSnapshot;
            File.WriteAllText(probeOut ?? "live-report.txt",
                $"STYLES={WindowsInterop.DescribeStyle(hwnd)}\n" +
                $"RECT={WindowsInterop.DescribeRect(hwnd)}\n" +
                $"COMPLETED_TRANSLATIONS={services.CompletedTranslations}\n" +
                $"LINES={snap?.SourceText ?? ""}|{snap?.TranslatedText ?? ""}\n");
            exitCode = 0;
        }
        catch (Exception ex)
        {
            File.WriteAllText(probeOut ?? "live-report.txt", $"LIVE_FAILED=1\nERROR={ex}");
        }
        finally
        {
            SubtitleOverlayHost.Stop();
            Environment.Exit(exitCode);
        }
    }

    /// <summary>Usage: <c>--overlay-probe &lt;out&gt;</c> — shows the WPF overlay with sample text, dumps styles.</summary>
    private static async void RunOverlayProbeAsync(string[] cmd, int startIndex)
    {
        string outFile = startIndex + 1 < cmd.Length ? cmd[startIndex + 1] : "overlay-probe.txt";
        try
        {
            var (config, _) = Core.Configuration.AppConfigLoader.Load();
            var manager = new Core.Subtitle.SubtitleManager(config.Subtitle);
            SubtitleOverlayHost.Start(config.Subtitle);
            manager.CurrentChanged += s => SubtitleOverlayHost.PushSnapshot(s);

            manager.OnSourcePartial("Where are you going?", DateTimeOffset.Now);
            manager.OnSourceFinal("Where are you going?", DateTimeOffset.Now);
            manager.OnTranslated("Where are you going?", "你要去哪里？", DateTimeOffset.Now);

            await Task.Delay(TimeSpan.FromSeconds(3));
            var hwnd = SubtitleOverlayHost.Hwnd;
            var snap = SubtitleOverlayHost.CurrentSnapshot;
            File.WriteAllText(outFile,
                $"STYLES={WindowsInterop.DescribeStyle(hwnd)}\n" +
                $"RECT={WindowsInterop.DescribeRect(hwnd)}\n" +
                $"SNAPSHOT={snap?.SourceText ?? ""}|{snap?.TranslatedText ?? ""}\n");
        }
        catch (Exception ex)
        {
            File.WriteAllText(outFile, $"OVERLAY_PROBE_FAILED=1\nERROR={ex}");
        }
        finally
        {
            SubtitleOverlayHost.Stop();
            Environment.Exit(0);
        }
    }

    /// <summary>Usage: <c>--ai-setup &lt;outFile&gt;</c> — diagnostics-only model readiness probe.</summary>
    private static async void RunAiSetupAsync(string[] cmd, int startIndex)
    {
        string? outFile = startIndex + 1 < cmd.Length ? cmd[startIndex + 1] : null;
        try
        {
            var report = await SpeechSetupProbe.RunAsync();
            if (outFile is not null) File.WriteAllText(outFile, report);
            LogSink.Default.Info("Setup probe done:\n{0}", report);
        }
        catch (Exception ex)
        {
            if (outFile is not null)
            {
                File.WriteAllText(outFile, $"SETUP_PROBE_FAILED=1\nERROR={ex}");
            }
        }
        finally
        {
            Environment.Exit(0);
        }
    }

    /// <summary>
    /// Usage: <c>--ai-probe &lt;wavPath&gt; &lt;outFile&gt; [--stream]</c>.
    /// Runs model readiness + recognition, writes a result report to outFile, then exits.
    /// </summary>
    private static async void RunAiProbeAsync(string[] cmd, int startIndex)
    {
        string? wavPath = null, outFile = null;
        bool stream = false;
        for (int i = startIndex + 1; i < cmd.Length; i++)
        {
            switch (cmd[i])
            {
                case "--stream": stream = true; break;
                default:
                    if (wavPath is null) wavPath = cmd[i];
                    else if (outFile is null) outFile = cmd[i];
                    break;
            }
        }

        int exitCode = 1;
        try
        {
            if (wavPath is null || outFile is null)
            {
                WriteProbeResult(outFile, new Exception("Usage: --ai-probe <wav> <out> [--stream]"), exitCode: 2);
                return;
            }

            var log = new LogSink { MinLevel = LogLevel.Info };
            var (transcript, elapsed, readyState) = stream
                ? await RunStreamingAsync(wavPath)
                : await SpeechProbe.RunBatchAsync(wavPath);

            string report =
                $"READY_STATE={readyState}\n" +
                $"MODE={(stream ? "stream" : "batch")}\n" +
                $"ELAPSED_MS={(long)elapsed.TotalMilliseconds}\n" +
                $"TRANSCRIPT={transcript}\n";

            File.WriteAllText(outFile, report);
            LogSink.Default.Info("Probe done: state={0}, elapsed={1} ms, transcript='{2}'", readyState, elapsed.TotalMilliseconds, transcript);
            exitCode = 0;
        }
        catch (Exception ex)
        {
            WriteProbeResult(outFile, ex, exitCode: 1);
        }
        finally
        {
            Environment.Exit(exitCode);
        }
    }

    private static async Task<(string Transcript, TimeSpan Elapsed, AsrModelReadyState ReadyState)>
        RunStreamingAsync(string wavPath)
    {
        // Phase 2 streaming probe: feed the 16 kHz mono PCM WAV through SpeechAudioProvider
        // exactly like the capture pipeline will (WritePcm16 → PushData).
        byte[] pcm = ReadPcm16Wave(wavPath);

        var manager = new SpeechModelManager();
        if (manager.GetReadyState() != AsrModelReadyState.Ready)
        {
            await manager.EnsureReadyAsync();
        }

        using var recognizer = manager.CreateRecognizer();
        var finals = new List<string>();
        var gate = new TaskCompletionSource<bool>();
        recognizer.Final += f => { lock (finals) finals.Add(f.Text); gate.TrySetResult(true); };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        recognizer.Start(cts.Token);

        DateTimeOffset start = DateTimeOffset.UtcNow;
        for (int off = 0; off < pcm.Length; off += 4096)
        {
            int len = Math.Min(4096, pcm.Length - off);
            recognizer.WritePcm16(pcm.AsSpan(off, len), DateTimeOffset.UtcNow);
        }

        await Task.WhenAny(gate.Task, Task.Delay(TimeSpan.FromSeconds(15), cts.Token));
        recognizer.Stop();
        lock (finals) return (string.Join(" | ", finals), DateTimeOffset.UtcNow - start, manager.GetReadyState());
    }

    /// <summary>Resolves a model dir via the multi-root search (install root → checkout out/ dirs).</summary>
    private static string ResolveModelDir(string modelId, string fallbackRelative)
    {
        string? found = ModelPaths.FindModelDir(ModelCatalog.Get(modelId));
        return found ?? Path.GetFullPath(fallbackRelative);
    }

    /// <summary>Parses the raw sample bytes of a PCM16 WAVE file (chunk-walking header).</summary>
    private static byte[] ReadPcm16Wave(string path)
    {
        byte[] all = File.ReadAllBytes(path);
        if (all.Length < 12 || System.Text.Encoding.ASCII.GetString(all, 0, 4) != "RIFF"
                            || System.Text.Encoding.ASCII.GetString(all, 8, 4) != "WAVE")
        {
            throw new InvalidDataException("Not a RIFF/WAVE file.");
        }

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

            pos = body + size + (size & 1);
        }

        throw new InvalidDataException("No 'data' chunk found in WAVE file.");
    }

    private static void WriteProbeResult(string? outFile, Exception ex, int exitCode)
    {
        string report = $"PROBE_FAILED=1\nERROR_TYPE={ex.GetType().Name}\nERROR={ex.Message}\nSTACK={ex}";
        if (outFile is not null) File.WriteAllText(outFile, report);
        LogSink.Default.Error("Probe failed: {0}", ex);
    }
}