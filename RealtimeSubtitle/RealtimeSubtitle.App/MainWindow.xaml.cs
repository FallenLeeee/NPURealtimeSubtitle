using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using RealtimeSubtitle.Core.Configuration;
using RealtimeSubtitle.Core.Diagnostics;
using RealtimeSubtitle.Core.Models;
using RealtimeSubtitle.Overlay.Wpf;
using Windows.UI.ViewManagement;
using Path = System.IO.Path;

namespace RealtimeSubtitle.App;

/// <summary>
/// Control panel: mode/subtitle-style/ASR-engine/translation-device selection, user-configured
/// translation model path, live model status, one-click provisioning (checkout dir → offline
/// dist zip → Models.BaseUrl), and the start/stop pipeline control. The overlay itself is the
/// WPF per-pixel-transparent window hosted by <see cref="SubtitleOverlayHost"/>.
/// </summary>
public sealed partial class MainWindow : Window
{
    private static readonly SolidColorBrush OkBrush = new(Microsoft.UI.ColorHelper.FromArgb(255, 0x4C, 0xC3, 0x8A));
    private static readonly SolidColorBrush BadBrush = new(Microsoft.UI.ColorHelper.FromArgb(255, 0xE5, 0x48, 0x4D));
    private static readonly SolidColorBrush NeutralBrush = new(Microsoft.UI.ColorHelper.FromArgb(255, 0x9B, 0xA1, 0xA6));

    private readonly LogSink _log = new() { MinLevel = LogLevel.Info };
    private readonly AppConfig _config;
    private readonly ModelProvisioner _provisioner;
    private readonly string _transcriptPath;
    private readonly DispatcherQueueTimer _statsTimer;
    private readonly UISettings _uiSettings = new();
    private Services.AppServices? _services;
    private bool _starting;

    public MainWindow()
    {
        InitializeComponent();
        (_config, var issues) = AppConfigLoader.Load();
        foreach (string issue in issues) _log.Warn("config: {0}", issue);
        _provisioner = new ModelProvisioner(_config.Models, _log);

        // Persistent file log (P6-11): the GUI used to keep logs only in the in-memory
        // LogBox ring; write a rolling file too so runtime issues ([Music] floods, crashes)
        // can be inspected after the fact.
        try
        {
            _log.LogFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "RealtimeSubtitle", "logs", $"app-{DateTime.Now:yyyyMMdd-HHmmss}.log");
        }
        catch (Exception ex)
        {
            _log.Warn("file log unavailable: {0}", ex.Message);
        }

        _log.LineWritten += _ => DispatcherQueue.TryEnqueue(() =>
        {
            LogBox.Text = string.Join("\n", _log.Recent(24));
            try { LogScroller.ChangeView(null, double.MaxValue, null); } catch { }
        });

        string candidate = Path.Combine(AppContext.BaseDirectory, "demo.txt");
        _transcriptPath = File.Exists(candidate)
            ? candidate
            : Path.Combine(Environment.CurrentDirectory, "demo.txt");

        try { AppWindow.Resize(new global::Windows.Graphics.SizeInt32(820, 900)); } catch { }

        // P6-15: title bar must follow the Windows default app mode. AppWindowTitleBar
        // defaults to TitleBarTheme.Legacy, which does NOT track the system light/dark
        // setting — the caption stays grey regardless of the OS theme. UseDefaultAppMode
        // makes DWM re-render the caption when the user switches Windows theme; we also
        // listen to UISettings.ColorValuesChanged so a live switch refreshes immediately.
        try
        {
            if (AppWindowTitleBar.IsCustomizationSupported())
            {
                ApplyTitleBarTheme();
                _uiSettings.ColorValuesChanged += (_, _) =>
                    DispatcherQueue.TryEnqueue(ApplyTitleBarTheme);
                _log.Info("Title bar follows Windows default app mode (UseDefaultAppMode).");
            }
            else
            {
                _log.Info("AppWindowTitleBar customization unsupported; caption keeps system theme.");
            }
        }
        catch (Exception ex)
        {
            _log.Warn("title bar theme setup failed: {0}", ex.Message);
        }

        SetupModeCombo();
        SetupDisplayCombo();
        SetupAsrCombo();
        SetupLanguageCombo();
        SetupWhisperModelCombo();
        SetupTransDeviceCombo();
        TransModelBox.Text = _config.Translation.ModelPath ?? "";
        RefreshModelStatus();
        UpdateEngineHint();
        UpdateWhisperModelEnabled();
        SetStatus("就绪", NeutralBrush);

        _statsTimer = DispatcherQueue.CreateTimer();
        _statsTimer.Interval = TimeSpan.FromSeconds(2);
        _statsTimer.IsRepeating = true;
        _statsTimer.Tick += (_, _) =>
        {
            if (_services is not null)
            {
                ActionHint.Text = $"运行中 · 已完成翻译 {_services.CompletedTranslations} 条 · 覆盖层位于屏幕下方居中";
            }
        };
        _statsTimer.Start();

        Closed += (_, _) =>
        {
            _statsTimer.Stop();
            _services?.Dispose();
            _services = null;
            SubtitleOverlayHost.Stop();
        };
    }

    /// <summary>
    /// P6-15: makes the caption follow the Windows default app mode (light/dark) instead of
    /// the AppWindowTitleBar default (Legacy), and re-applies it when the OS theme changes.
    /// </summary>
    private void ApplyTitleBarTheme()
    {
        try
        {
            if (AppWindowTitleBar.IsCustomizationSupported())
            {
                AppWindow.TitleBar.PreferredTheme = TitleBarTheme.UseDefaultAppMode;
            }
        }
        catch (Exception ex)
        {
            _log.Warn("ApplyTitleBarTheme failed: {0}", ex.Message);
        }
    }

    // ---------- UI setup ----------

    private void SetupModeCombo()
    {
        ModeCombo.SelectedIndex = 0; // 演示字幕（可随时切换）
        UpdateModeHint();
    }

    private void SetupDisplayCombo()
    {
        foreach (var item in DisplayCombo.Items)
        {
            if (item is ComboBoxItem cbi && cbi.Tag as string == _config.SubtitleMode)
            {
                DisplayCombo.SelectedItem = cbi;
                break;
            }
        }
    }

    private void SetupAsrCombo()
    {
        foreach (var item in AsrCombo.Items)
        {
            if (item is ComboBoxItem cbi && cbi.Tag as string == _config.Asr.PreferredBackend)
            {
                AsrCombo.SelectedItem = cbi;
                break;
            }
        }
    }

    private void SetupLanguageCombo()
    {
        foreach (var item in LanguageCombo.Items)
        {
            if (item is ComboBoxItem cbi && cbi.Tag as string == (_config.Asr.Language ?? "auto"))
            {
                LanguageCombo.SelectedItem = cbi;
                break;
            }
        }
    }

    private void SetupWhisperModelCombo()
    {
        foreach (var item in WhisperModelCombo.Items)
        {
            if (item is ComboBoxItem cbi && cbi.Tag as string == _config.Asr.Model)
            {
                WhisperModelCombo.SelectedItem = cbi;
                break;
            }
        }
    }

    private void SetupTransDeviceCombo()
    {
        foreach (var item in TransDeviceCombo.Items)
        {
            if (item is ComboBoxItem cbi && cbi.Tag as string == _config.Translation.Device)
            {
                TransDeviceCombo.SelectedItem = cbi;
                break;
            }
        }
    }

    private void OnModeChanged(object sender, SelectionChangedEventArgs e) => UpdateModeHint();

    private void UpdateModeHint()
    {
        string? mode = (ModeCombo.SelectedItem as ComboBoxItem)?.Tag as string;
        ModeHint.Text = mode == "live"
            ? "实时识别系统音频/麦克风 → 翻译 → 字幕上屏。需要语音识别与翻译。"
            : "按 demo.txt 剧本演示 → 翻译 → 字幕上屏。只需要翻译模型。";
    }

    private void OnDisplayChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DisplayCombo.SelectedItem is ComboBoxItem cbi && cbi.Tag is string tag)
        {
            _config.SubtitleMode = tag;
            SaveConfig();
        }
    }

    private void OnAsrChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AsrCombo.SelectedItem is ComboBoxItem cbi && cbi.Tag is string tag)
        {
            _config.Asr.PreferredBackend = tag;
            SaveConfig();
            UpdateEngineHint();
            UpdateWhisperModelEnabled();
            RefreshModelStatus();
        }
    }

    /// <summary>Language routing change (P6-4): persists, syncs SourceLanguage, and while
    /// running hot-swaps the recognizer (loopback capture keeps going).</summary>
    private async void OnLanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LanguageCombo.SelectedItem is not ComboBoxItem cbi || cbi.Tag is not string tag) return;
        if ((_config.Asr.Language ?? "auto") == tag) return;

        _config.Asr.Language = tag;
        _config.SourceLanguage = tag switch
        {
            "zh" => "zh-CN",
            "en" => "en-US",
            "ja" => "ja-JP",
            _ => "auto",
        };
        SaveConfig();
        UpdateEngineHint();
        UpdateWhisperModelEnabled();
        RefreshModelStatus();

        if (_services is not null && _config.Asr.PreferredBackend != "legacy")
        {
            try
            {
                string dir = await EnsureModelAsync(ModelCatalog.AsrModelId(tag, _config.Asr.Model));
                _services.SwitchAsrBackend(() => Services.AppServices.BuildRecognizer(_config, dir, _log));
                SetStatus($"已热切换识别语言：{DescribeLanguage(tag)}", OkBrush);
                _log.Info("ASR language hot-switched to {0}", tag);
            }
            catch (Exception ex)
            {
                _log.Error("ASR language switch failed: {0}", ex);
                SetStatus($"识别语言切换失败：{ex.Message}", BadBrush);
            }
        }
    }

    /// <summary>Model size hot-switch: persists the choice and, while running, swaps the
    /// whisper recognizer in-place (loopback capture keeps going). Whisper-family only
    /// (auto / en); the combo is disabled for the zh/ja specialised backends.</summary>
    private async void OnWhisperModelChanged(object sender, SelectionChangedEventArgs e)
    {
        if (WhisperModelCombo.SelectedItem is not ComboBoxItem cbi || cbi.Tag is not string tag) return;
        if (_config.Asr.Model == tag) return; // programmatic re-select

        _config.Asr.Model = tag;
        SaveConfig();
        RefreshModelStatus();

        string language = _config.Asr.Language ?? "auto";
        if (_services is not null && _config.Asr.PreferredBackend != "legacy"
            && language is "auto" or "en")
        {
            try
            {
                string dir = await EnsureModelAsync(ModelCatalog.AsrModelId(language, tag));
                _services.SwitchAsrBackend(() => Services.AppServices.BuildRecognizer(_config, dir, _log));
                SetStatus($"已热切换识别模型：Whisper {tag}", OkBrush);
                _log.Info("ASR model hot-switched to {0}", tag);
            }
            catch (Exception ex)
            {
                _log.Error("ASR model switch failed: {0}", ex);
                SetStatus($"识别模型切换失败：{ex.Message}", BadBrush);
            }
        }
    }

    private void UpdateWhisperModelEnabled()
    {
        bool whisper = _config.Asr.PreferredBackend != "legacy";
        string language = _config.Asr.Language ?? "auto";
        // The size dropdown only applies to the whisper family (auto/en).
        WhisperModelCombo.IsEnabled = whisper && language is "auto" or "en";
    }

    private void OnTransDeviceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TransDeviceCombo.SelectedItem is ComboBoxItem cbi && cbi.Tag is string tag)
        {
            _config.Translation.Device = tag;
            SaveConfig();
        }
    }

    private void OnSaveModelClick(object sender, RoutedEventArgs e)
    {
        string path = TransModelBox.Text.Trim();
        _config.Translation.ModelPath = path;
        SaveConfig();
        SetStatus(string.IsNullOrEmpty(path) ? "已清除自定义翻译模型路径" : $"已保存翻译模型目录：{path}", NeutralBrush);
        RefreshModelStatus();
    }

    private void UpdateEngineHint()
    {
        string? asr = (AsrCombo.SelectedItem as ComboBoxItem)?.Tag as string;
        string language = _config.Asr.Language ?? "auto";
        if (asr == "whisper")
        {
            EngineHint.Text = language switch
            {
                "zh" => "中文 → Qwen3-ASR 1.7B（最准；约 0.5-2s/段，仅出终稿）。识别扬声器/系统音频（回环），无需麦克风权限。",
                "ja" => "日语 → SenseVoiceSmall（单程快速，~0.1-0.2s/段）→ opus-mt ja→zh 翻译。识别扬声器/系统音频（回环），无需麦克风权限。",
                "en" => "英语 → Whisper .en（tiny/base/small 生效；~0.1-0.4s/段）。识别扬声器/系统音频（回环），无需麦克风权限。",
                _ => "自动 → 多语言 Whisper（自动检测语言；~0.1-0.4s/段）。识别扬声器/系统音频（回环），无需麦克风权限；识别模型可热切换（tiny/base/small）。",
            };
        }
        else
        {
            EngineHint.Text = "Windows 语音识别：麦克风输入（需要麦克风权限）。要识别系统音频请选 Whisper。";
        }
    }

    private static string DescribeLanguage(string language) => language switch
    {
        "zh" => "中文（Qwen3-ASR）",
        "en" => "英语（Whisper .en）",
        "ja" => "日语（SenseVoice）",
        _ => "自动（多语言 Whisper）",
    };

    private void SaveConfig()
    {
        try { AppConfigLoader.Save(_config); } catch (Exception ex) { _log.Warn("config save failed: {0}", ex.Message); }
    }

    // ---------- model status + provisioning ----------

    private void RefreshModelStatus()
    {
        string backend = _config.Asr.PreferredBackend;
        string language = _config.Asr.Language ?? "auto";
        bool whisperNeeded = backend != "legacy";
        string asrModelId = ModelCatalog.AsrModelId(language, _config.Asr.Model);
        string asrModelName = ModelCatalog.Get(asrModelId).DisplayName;

        if (whisperNeeded)
        {
            SetModelRow(WhisperDot, WhisperStatus, asrModelId, $"（{DescribeLanguage(language)} 需要）");
        }
        else
        {
            WhisperDot.Fill = NeutralBrush;
            WhisperStatus.Text = "语音识别：未启用（当前用 Windows 语音识别）";
        }

        string? custom = _config.Translation.ModelPath;
        if (!string.IsNullOrWhiteSpace(custom))
        {
            bool exists = Directory.Exists(custom);
            MarianDot.Fill = exists ? OkBrush : BadBrush;
            MarianStatus.Text = exists
                ? $"Marian 翻译模型：自定义已就绪（{custom}）"
                : $"Marian 翻译模型：自定义路径不存在（{custom}）——将回退到默认模型";
        }
        else
        {
            // 翻译按源语言路由（P6-10）：ja → ja→zh 专用模型，其余 → en→zh。
            SetModelRow(MarianDot, MarianStatus, ModelCatalog.TranslationModelId(language), "");
        }

        bool missing = (whisperNeeded && _provisioner.FindLocal(asrModelId) is null)
                    || (string.IsNullOrWhiteSpace(custom) && _provisioner.FindLocal(ModelCatalog.TranslationModelId(language)) is null);
        DownloadButton.IsEnabled = missing;
        ModelsDetail.Text = missing
            ? $"缺失的模型点击“下载缺失模型”自动补齐（安装目录：{_provisioner.InstallRoot}）。" +
              (string.IsNullOrWhiteSpace(_config.Models.BaseUrl) ? "" : $" 下载源：{_config.Models.BaseUrl}")
            : $"模型已就绪（安装目录：{_provisioner.InstallRoot}）。";
    }

    private void SetModelRow(Ellipse dot, TextBlock status, string modelId, string suffix)
    {
        string? dir = _provisioner.FindLocal(modelId);
        string name = ModelCatalog.Get(modelId).DisplayName;
        if (dir is not null)
        {
            dot.Fill = OkBrush;
            status.Text = $"{name}：已就绪（{dir}）";
        }
        else
        {
            dot.Fill = BadBrush;
            status.Text = $"{name}：缺失{suffix}";
        }
    }

    private async void OnDownloadClick(object sender, RoutedEventArgs e)
    {
        if (_services is not null) return;
        DownloadButton.IsEnabled = false;
        DownloadProgress.Visibility = Visibility.Visible;
        DownloadProgress.Value = 0;
        try
        {
            // Download only what the current configuration actually needs (language-routed
            // translation + the language-routed ASR model); other bundles stay available
            // via WavDumpTool.
            string transModelId = ModelCatalog.TranslationModelId(_config.Asr.Language ?? "auto");
            var needed = new List<string> { transModelId };
            if (_config.Asr.PreferredBackend != "legacy")
            {
                needed.Add(ModelCatalog.AsrModelId(_config.Asr.Language ?? "auto", _config.Asr.Model));
            }

            foreach (string modelId in needed)
            {
                if (_provisioner.FindLocal(modelId) is not null) continue;
                ModelEntry entry = ModelCatalog.Get(modelId);
                SetStatus($"正在获取模型：{entry.DisplayName} …", NeutralBrush);
                await _provisioner.EnsureAsync(modelId, new Progress<ModelProgress>(p =>
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        DownloadProgress.Value = p.Fraction * 100;
                        DownloadPercent.Text = p.Stage;
                    })));
                _log.Info("Model provisioned: {0}", modelId);
            }

            RefreshModelStatus();
            SetStatus("模型已就绪", OkBrush);
        }
        catch (Exception ex)
        {
            _log.Error("Model provisioning failed: {0}", ex);
            SetStatus($"模型获取失败：{ex.Message}", BadBrush);
        }
        finally
        {
            DownloadProgress.Visibility = Visibility.Collapsed;
            DownloadPercent.Text = "";
            DownloadButton.IsEnabled = true;
        }
    }

    private async Task<string> EnsureModelAsync(string modelId)
    {
        string? local = _provisioner.FindLocal(modelId);
        if (local is not null) return local;

        ModelEntry entry = ModelCatalog.Get(modelId);
        SetStatus($"正在获取模型：{entry.DisplayName} …", NeutralBrush);
        DownloadProgress.Visibility = Visibility.Visible;
        DownloadProgress.Value = 0;
        try
        {
            return await _provisioner.EnsureAsync(modelId, new Progress<ModelProgress>(p =>
                DispatcherQueue.TryEnqueue(() =>
                {
                    DownloadProgress.Value = p.Fraction * 100;
                    DownloadPercent.Text = p.Stage;
                })));
        }
        finally
        {
            DownloadProgress.Visibility = Visibility.Collapsed;
            DownloadPercent.Text = "";
        }
    }

    /// <summary>User-configured translation model dir (when valid), else the provisioned default.</summary>
    private async Task<string> ResolveTranslationModelAsync()
    {
        string? custom = _config.Translation.ModelPath;
        if (!string.IsNullOrWhiteSpace(custom))
        {
            if (Directory.Exists(custom))
            {
                _log.Info("Using configured translation model: {0}", custom);
                return custom;
            }

            _log.Warn("Configured translation model dir does not exist ({0}); falling back.", custom);
        }

        // 翻译按源语言路由（P6-10）：ja → ja→zh 模型，其余 → en→zh。
        return await EnsureModelAsync(ModelCatalog.TranslationModelId(_config.Asr.Language ?? "auto"));
    }

    // ---------- pipeline control ----------

    private async void OnStartClick(object sender, RoutedEventArgs e)
    {
        if (_services is not null)
        {
            StopPipeline();
            return;
        }

        if (_starting) return;
        _starting = true;
        StartButton.IsEnabled = false;

        try
        {
            string mode = (ModeCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "demo";
            bool live = mode == "live";
            string backend = _config.Asr.PreferredBackend;
            // 回环/whisper 相关后端都需要 whisper 模型；只有 legacy（麦克风）不需要。
            bool whisperNeeded = live && backend != "legacy";

            string marianDir = await ResolveTranslationModelAsync();
            string? asrDir = null;
            if (whisperNeeded) asrDir = await EnsureModelAsync(ModelCatalog.AsrModelId(_config.Asr.Language ?? "auto", _config.Asr.Model));

            if (!File.Exists(_transcriptPath))
            {
                throw new InvalidOperationException($"未找到演示文稿：{_transcriptPath}");
            }

            SetStatus("启动中 …", NeutralBrush);
            _services = live
                ? Services.AppServices.CreateLive(marianDir, asrDir, _log)
                : Services.AppServices.CreateDemo(_transcriptPath, marianDir, _log);

            SubtitleOverlayHost.Start(_services.Subtitles.Config);
            _services.Subtitles.CurrentChanged += s => SubtitleOverlayHost.PushSnapshot(s);
            _services.Start();

            SetStatus(live
                ? $"运行中：{DescribeAsr(backend)}（{DescribeLanguage(_config.Asr.Language ?? "auto")}） → 翻译（{_services.TranslationQueue.TranslatorDevice}）→ 字幕"
                : "运行中：演示字幕（demo.txt → 翻译 → 字幕）", OkBrush);
            StartButton.Content = "停止";
            ModeCombo.IsEnabled = false;
            DisplayCombo.IsEnabled = false;
            AsrCombo.IsEnabled = false;
            LanguageCombo.IsEnabled = false;
            TransDeviceCombo.IsEnabled = false;
            WhisperModelCombo.IsEnabled = false;
            ActionHint.Text = "运行中 · 覆盖层位于屏幕下方居中";
            _log.Info("Pipeline started ({0}); asr={1}", live ? "live" : "demo", backend);
        }
        catch (Exception ex)
        {
            _log.Error("Start failed: {0}", ex);
            SetStatus($"启动失败：{ex.Message}", BadBrush);
        }
        finally
        {
            _starting = false;
            StartButton.IsEnabled = true;
        }
    }

    private static string DescribeAsr(string backend) =>
        backend == "whisper" ? "Whisper 本地识别" : "Windows 语音识别";

    private void StopPipeline()
    {
        _services?.Dispose();
        _services = null;
        SubtitleOverlayHost.Stop();
        StartButton.Content = "开始";
        ModeCombo.IsEnabled = true;
        DisplayCombo.IsEnabled = true;
        AsrCombo.IsEnabled = true;
        LanguageCombo.IsEnabled = true;
        TransDeviceCombo.IsEnabled = true;
        WhisperModelCombo.IsEnabled = true;
        UpdateWhisperModelEnabled();
        ActionHint.Text = "";
        SetStatus("已停止", NeutralBrush);
    }

    private void SetStatus(string text, SolidColorBrush color)
    {
        Status.Text = text;
        StatusDot.Fill = color;
    }
}
