using RealtimeSubtitle.Core.Audio;
using RealtimeSubtitle.Core.Configuration;
using RealtimeSubtitle.Core.Diagnostics;
using RealtimeSubtitle.Core.Speech;
using RealtimeSubtitle.Core.Subtitle;
using RealtimeSubtitle.Core.Translation;
using RealtimeSubtitle.Windows;

namespace RealtimeSubtitle.App.Services;

/// <summary>
/// Composition root (plan §4 Services/AppServices): wires recognizer → queue → subtitle
/// manager → overlay. Demo mode uses a transcript feed; live mode uses the real loopback
/// capture + Whisper classic recognizer through the same <see cref="ISpeechRecognizer"/> seam.
/// </summary>
public sealed class AppServices : IDisposable
{
    private ISpeechRecognizer _recognizer;
    private readonly TranslationQueue _queue;
    private readonly AppConfig _config;
    private readonly CancellationTokenSource _lifetime = new();
    private CaptureFeeder? _feeder;
    private WasapiLoopbackCapturer? _capturer;
    private int _completedCount;

    // P6-14: partial-level translation throttle. In continuous speech (lyrics) the ASR
    // backend emits partials every ~1.2 s but finals are rare (a whole stanza is one
    // segment), so translating only on Final made the subtitle appear never to catch up.
    // We translate changed partials too, throttled and deduplicated so the queue never
    // floods, and the provisional translation lands on the SAME line by id (P6-13).
    private string? _lastPartialQueued;
    private DateTimeOffset _lastPartialQueuedAt = DateTimeOffset.MinValue;

    public AppServices(AppConfig config, string translationModelPath, ISpeechRecognizer recognizer, LogSink log)
    {
        _recognizer = recognizer;
        _config = config;

        config.Subtitle.Mode = config.SubtitleMode; // root schema drives the subtitle style
        Subtitles = new SubtitleManager(config.Subtitle);

        // GUI's 翻译设备 dropdown (auto/NPU/CPU) previously never reached the runtime:
        // this line hard-coded "auto" and disregarded config.Translation.Device, so users
        // could neither force CPU nor see the real device. Route it through now (P6-12).
        string transDevice = string.IsNullOrWhiteSpace(config.Translation.Device)
            ? "auto"
            : config.Translation.Device;
        Log = log;
        Log.Info("Translation device from config: {0}", transDevice);
        var translator = new OpenVinoTranslator(translationModelPath, transDevice, log);
        _queue = new TranslationQueue(translator, capacity: Math.Max(2, config.Translation.MaxQueue));
        TranslationQueue = _queue;

        BindRecognizer(recognizer);

        _queue.Completed += (request, result) =>
        {
            if (result.TargetText.Length > 0)
            {
                // P6-13: translations land on the exact subtitle line by id — text-matching
                // against Current dropped the translation whenever the next partial arrived
                // first (UI showed source only, never the translation).
                Subtitles.OnTranslated(request.SubtitleLineId, result.TargetText, DateTimeOffset.Now);
                Interlocked.Increment(ref _completedCount);
            }
        };
    }

    /// <summary>
    /// Routes an ASR result to the subtitle/translation pipeline. Audio-event annotations
    /// ([Music], &lt;|music|&gt;, …) are stripped from the leading edge so lyrics behind them
    /// survive ("[Music] Hello love" → "Hello love", P6-11); a line that is pure junk
    /// (e.g. just "[Music]", "♪ ♪") is dropped entirely.
    /// </summary>
    private static string CleanAsrText(string? text)
    {
        return AsrJunkFilter.StripAnnotations(text);
    }

    private void BindRecognizer(ISpeechRecognizer recognizer)
    {
        recognizer.Partial += p =>
        {
            string clean = CleanAsrText(p.Text);
            if (clean.Length == 0) return; // pure audio-event annotation

            // P6-14: provisional translation alongside the live preview. Translate changed
            // partials (throttled + deduplicated) so lyrics scroll WITH their translation
            // even though SentenceVoice finals are rare in continuous singing.
            int lineId = Subtitles.OnSourcePartial(clean, p.Timestamp);
            if (ShouldTranslatePartial(clean))
            {
                _queue.TryEnqueue(new TranslationRequest(clean, p.Timestamp, SubtitleLineId: lineId));
            }
        };
        recognizer.Final += f =>
        {
            string clean = CleanAsrText(f.Text);
            if (clean.Length == 0) return; // pure audio-event annotation
            // P6-13: capture the stable line id so the late translation lands on THIS line
            // (matching by text failed when the next sentence's partial arrived first).
            int lineId = Subtitles.OnSourceFinal(clean, f.Timestamp);
            _queue.TryEnqueue(new TranslationRequest(clean, f.Timestamp, SubtitleLineId: lineId));
        };
    }

    /// <summary>
    /// P6-14: throttle partial translation to changed text at most ~1/s. A partial grows
    /// sentence-by-sentence ("時を巡って今…"), so translating every tick would flood the
    /// queue with near-identical sentences; dedupe the exact text and cap the cadence.
    /// Finals always translate (unchanged path), so the final correction still lands.
    /// </summary>
    private bool ShouldTranslatePartial(string clean)
    {
        if (string.Equals(clean, _lastPartialQueued, StringComparison.Ordinal)) return false;
        if (DateTimeOffset.UtcNow - _lastPartialQueuedAt < TimeSpan.FromMilliseconds(700)) return false;

        _lastPartialQueued = clean;
        _lastPartialQueuedAt = DateTimeOffset.UtcNow;
        return true;
    }

    public SubtitleManager Subtitles { get; }
    public TranslationQueue TranslationQueue { get; }
    public LogSink Log { get; }
    public int CompletedTranslations => Volatile.Read(ref _completedCount);

    public static AppServices CreateDemo(string transcriptPath, string translationModelPath, LogSink log)
    {
        var (config, issues) = AppConfigLoader.Load();
        foreach (string issue in issues) log.Warn("config: {0}", issue);

        return new AppServices(config, translationModelPath, new DemoFeed(transcriptPath), log);
    }

    /// <summary>
    /// Live mode: real input → ASR → translation → subtitles. The ASR backend comes from
    /// config <c>Asr.PreferredBackend</c> (+ language routing, P6-4):
    ///   "legacy"      → Windows.Media.SpeechRecognition (mic, streaming; no loopback feeder),
    ///   "whisper"/…   → language-routed OpenVINO backend on the loopback capture:
    ///                     Asr.Language auto → multilingual whisper
    ///                     "en"              → whisper.en (mono)
    ///                     "zh"              → Qwen3-ASR
    ///                     "ja"              → SenseVoiceSmall
    /// </summary>
    public static AppServices CreateLive(string translationModelPath, string? asrModelDir, LogSink log)
    {
        var (config, issues) = AppConfigLoader.Load();
        foreach (string issue in issues) log.Warn("config: {0}", issue);

        string backend = config.Asr.PreferredBackend;
        if (backend == "windows-ai")
        {
            // Windows AI Speech 在本机不可用（0x8A1F022F，WindowsAppSDK#6561）；按用户需求
            // 优先走扬声器回环 Whisper，缺模型时才退回麦克风 legacy。
            log.Warn("Asr.PreferredBackend='windows-ai' 不可用 → 改用 Whisper 回环（扬声器）。");
            backend = "whisper";
        }

        ISpeechRecognizer recognizer;
        if (backend != "whisper" || asrModelDir is null)
        {
            if (asrModelDir is null && backend == "whisper")
            {
                log.Warn("ASR 模型缺失 → 回退 Windows 语音识别（麦克风）。");
            }

            recognizer = new LegacySpeechRecognizer(NormalizeSpeechLanguage(config.SourceLanguage), log);
        }
        else
        {
            recognizer = BuildRecognizer(config, asrModelDir, log);
        }

        var services = new AppServices(config, translationModelPath, recognizer, log);

        // Loopback feeder only for PCM-push backends (legacy reads the mic itself).
        if (recognizer is not LegacySpeechRecognizer)
        {
            var capturer = new WasapiLoopbackCapturer(log);
            services._capturer = capturer;
            services._feeder = new CaptureFeeder(capturer, recognizer, config.Vad, log);
            capturer.Error += services._feeder.OnCapturerError;
        }

        return services;
    }

    /// <summary>Builds the language-routed OpenVINO recognizer for the configured model dir.</summary>
    public static ISpeechRecognizer BuildRecognizer(AppConfig config, string asrModelDir, LogSink log)
    {
        string language = config.Asr.Language ?? "auto";
        log.Info("ASR routing: language={0} model={1}", language, asrModelDir);
        return language switch
        {
            "zh" => new OpenVinoQwen3AsrRecognizer(asrModelDir, "zh", log),
            "ja" => new OpenVinoSenseVoiceRecognizer(asrModelDir, log, vad: config.Vad),
            // "en" → whisper.en (mono flag in the model) ; "auto"/others → multilingual whisper.
            _ => new OpenVinoWhisperClassicRecognizer(asrModelDir, device: "auto",
                language: config.SourceLanguage, log, vad: config.Vad),
        };
    }

    /// <summary>Maps the config source language ("auto"/Bcp47) to a Windows speech language.</summary>
    private static string? NormalizeSpeechLanguage(string sourceLanguage)
    {
        if (string.IsNullOrWhiteSpace(sourceLanguage) || sourceLanguage == "auto") return null;
        // Windows.Media.SpeechRecognition wants IETF tags like en-US / zh-CN / ja-JP.
        string tag = sourceLanguage.Replace('_', '-');
        return tag.Length >= 2 ? tag : null;
    }

    public void Start()
    {
        _recognizer.Start(_lifetime.Token);
        if (_feeder is not null && _capturer is not null)
        {
            _capturer.Start(_lifetime.Token);
            _feeder.Start(_lifetime.Token);
            Log.Info("Live pipeline started: loopback → whisper → translate → subtitles.");
        }
        else
        {
            Log.Info("Pipeline started (demo feed).");
        }
    }

    /// <summary>
    /// Hot-swaps the ASR backend (model size OR language routing) while the pipeline keeps
    /// running: the loopback capture continues, only the feed/recognizer pair is rebuilt.
    /// No-op unless the pipeline runs a loopback-backed backend.
    /// </summary>
    public void SwitchAsrBackend(Func<ISpeechRecognizer> recognizerFactory)
    {
        if (_feeder is null || _capturer is null || _recognizer is LegacySpeechRecognizer)
        {
            Log.Warn("ASR backend switch skipped (not a loopback pipeline).");
            return;
        }

        var oldFeeder = _feeder;
        var oldRecognizer = _recognizer;

        oldFeeder.StopPipeline(); // stop pushing into the old recognizer; capturer keeps running
        _capturer.Error -= oldFeeder.OnCapturerError;
        oldRecognizer.Stop();
        oldRecognizer.Dispose();

        var next = recognizerFactory();
        BindRecognizer(next);
        _recognizer = next;

        _feeder = new CaptureFeeder(_capturer, next, _config.Vad, Log);
        _capturer.Error += _feeder.OnCapturerError;
        _feeder.Start(_lifetime.Token);
        next.Start(_lifetime.Token);
        Log.Info("ASR backend hot-switched ({0}).", next.GetType().Name);
    }

    /// <summary>Convenience: hot-swaps just the whisper model directory.</summary>
    public void SwitchAsrModel(string whisperModelDir)
    {
        SwitchAsrBackend(() =>
        {
            var (config, _) = AppConfigLoader.Load();
            return new OpenVinoWhisperClassicRecognizer(whisperModelDir, "auto", config.SourceLanguage, Log, vad: config.Vad);
        });
    }

    public void Dispose()
    {
        try { _lifetime.Cancel(); } catch { }
        _feeder?.Dispose();
        _capturer?.Dispose();
        _recognizer.Stop();
        _queue.Dispose();
        _recognizer.Dispose();
        _lifetime.Dispose();
    }
}