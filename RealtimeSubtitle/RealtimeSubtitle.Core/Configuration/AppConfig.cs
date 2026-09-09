namespace RealtimeSubtitle.Core.Configuration;

/// <summary>根配置模型。镜像 v3.0 计划（§7）中的 JSON 模式。</summary>
public sealed class AppConfig
{
    public string SourceLanguage { get; set; } = "auto";    // auto | en-US | ja-JP | zh-CN | ko-KR ...
    public string TargetLanguage { get; set; } = "zh-CN";
    public string SubtitleMode { get; set; } = "bilingual"; // source | translation | bilingual

    public AudioConfig Audio { get; set; } = new();
    public VadConfig Vad { get; set; } = new();
    public AsrConfig Asr { get; set; } = new();
    public TranslationConfig Translation { get; set; } = new();
    public SubtitleConfig Subtitle { get; set; } = new();
    public DiagnosticsConfig Diagnostics { get; set; } = new();
    public ModelsConfig Models { get; set; } = new();

    /// <summary>当前值的验证问题；空表示有效。</summary>
    public IReadOnlyList<string> Validate()
    {
        var issues = new List<string>();

        if (string.IsNullOrWhiteSpace(SourceLanguage)) issues.Add("SourceLanguage must not be empty.");
        if (string.IsNullOrWhiteSpace(TargetLanguage)) issues.Add("TargetLanguage must not be empty.");
        if (SubtitleMode is not ("source" or "translation" or "bilingual")) issues.Add("SubtitleMode must be one of: source | translation | bilingual.");

        if (Audio is null || Audio.SampleRate != 16000) issues.Add("Audio.SampleRate must be 16000 (ASR contract).");
        if (Audio is null || Audio.Channels != 1) issues.Add("Audio.Channels must be 1.");

        if (Vad is null || Vad.Threshold < 0 || Vad.Threshold > 1) issues.Add("Vad.Threshold must be within [0, 1].");
        if (Vad is not null && (Vad.MinSpeechMs <= 0 || Vad.HangoverMs <= 0)) issues.Add("Vad.MinSpeechMs/HangoverMs must be positive.");

        if (Asr is null || Asr.PreferredBackend is not ("windows-ai" or "legacy" or "whisper"))
            issues.Add("Asr.PreferredBackend must be one of: windows-ai | legacy | whisper.");
        if (Asr is not null && Asr.Device is not ("auto" or "NPU" or "CPU"))
            issues.Add("Asr.Device must be one of: auto | NPU | CPU.");
        if (Asr is not null && Asr.ZhBackend is not (null or "" or "sensevoice" or "qwen3"))
            issues.Add("Asr.ZhBackend must be one of: sensevoice | qwen3.");

        if (Translation is null) issues.Add("Translation section is missing.");
        else
        {
            if (Translation.Device is not ("auto" or "NPU" or "CPU")) issues.Add("Translation.Device must be one of: auto | NPU | CPU.");
            if (Translation.MaxQueue <= 0) issues.Add("Translation.MaxQueue must be positive.");
            if (Translation.MaxSrcLen <= 0 || Translation.MaxDstLen <= 0) issues.Add("Translation.MaxSrcLen/MaxDstLen must be positive.");
            if (Translation.BeamSize <= 0) issues.Add("Translation.BeamSize must be positive.");
            if (Translation.Context is { Enabled: true } && Translation.Context.Sentences is < 0 or > 3) issues.Add("Translation.Context.Sentences must be within [0, 3].");
        }

        if (Subtitle is null) issues.Add("Subtitle section is missing.");
        else
        {
            if (Subtitle.Opacity is < 0 or > 1) issues.Add("Subtitle.Opacity must be within [0, 1].");
            if (Subtitle.Position is null || Subtitle.Position.XRatio is < 0 or > 1 || Subtitle.Position.YRatio is < 0 or > 1)
                issues.Add("Subtitle.Position ratios must be within [0, 1].");
            if (Subtitle.MaxLines <= 0) issues.Add("Subtitle.MaxLines must be positive.");
        }

        return issues;
    }
}

public sealed class AudioConfig
{
    public string Device { get; set; } = "default"; // default | device id (enumeration fills this)
    public int SampleRate { get; set; } = 16000;    // 由 ASR 约定固定
    public int Channels { get; set; } = 1;
}

public sealed class VadConfig
{
    public bool Enabled { get; set; } = true;
    public float Threshold { get; set; } = 0.01f;
    public int MinSpeechMs { get; set; } = 150;
    public int HangoverMs { get; set; } = 400;
}

public sealed class AsrConfig
{
    // whisper = 系统音频回环上的 OpenVINO Whisper 经典；
    // legacy = Windows.Media.SpeechRecognition（麦克风，需要麦克风权限）；
    // windows-ai = Microsoft.Windows.AI.Speech（仅在有能力的机器上可用）。
    public string PreferredBackend { get; set; } = "whisper";
    // Whisper 模型大小：tiny | base | small（默认 base；可通过 GUI 热交换）。
    public string Model { get; set; } = "base";
    // ASR 推理设备（P6-18，GUI 与翻译设备分开设置）：auto | NPU | CPU。
    // auto → 各后端默认（whisper classic/SenseVoice 尝试 NPU；Qwen3 固定 CPU）。
    public string Device { get; set; } = "auto";
    // ASR 路由的源语言（P6-4）：auto | zh | en | ja。
    // auto → 多语言 whisper；zh → ZhBackend（默认 SenseVoice）；ja → SenseVoice；en → whisper.en。
    public string Language { get; set; } = "auto";
    // 中文识别后端（P6-24）：sensevoice = 快/NPU/流式（默认）；qwen3 = 1.7B 高精度但慢。
    public string ZhBackend { get; set; } = "sensevoice";
    public bool UsePartialPreview { get; set; } = true;
}

public sealed class TranslationConfig
{
    public string Backend { get; set; } = "openvino";
    public string Device { get; set; } = "auto";    // auto | NPU | CPU
    public string ResolvedDevice { get; set; } = ""; // 运行时确定，仅显示
    public string ModelPath { get; set; } = "";     // 空 = %LOCALAPPDATA%\RealtimeSubtitle\models\<src>-<tgt>\
    public int MaxQueue { get; set; } = 8;
    public int MaxSrcLen { get; set; } = 64;        // 静态形状最大编码器 token
    public int MaxDstLen { get; set; } = 64;        // 静态形状最大解码器 token
    public int BeamSize { get; set; } = 1;          // 按设计贪婪（延迟优先）
    public TranslationContextConfig Context { get; set; } = new();
}

public sealed class TranslationContextConfig
{
    public bool Enabled { get; set; } = false;
    public int Sentences { get; set; } = 2;
}

public sealed class SubtitleConfig
{
    public string Mode { get; set; } = "bilingual"; // source | translation | bilingual（镜像根 SubtitleMode）
    public int FontSize { get; set; } = 32;
    public int MaxLines { get; set; } = 2;
    /// <summary>Legacy dwell window (ms). Sticky subtitles ignore this — a line stays until
    /// the next real sentence replaces it.</summary>
    public int DurationMs { get; set; } = 6000;
    public double Opacity { get; set; } = 0.9;      // 整窗口 alpha
    public SubtitlePositionConfig Position { get; set; } = new();
    public bool ShowSource { get; set; } = true;
    public bool ShowTranslation { get; set; } = true;
    public bool ShowTail { get; set; } = false;
}

public sealed class SubtitlePositionConfig
{
    public double XRatio { get; set; } = 0.5;
    public double YRatio { get; set; } = 0.9;
}

public sealed class DiagnosticsConfig
{
    public string LogLevel { get; set; } = "info"; // debug | info | warn | error
    public int LatencyWindowSec { get; set; } = 10;
}

public sealed class ModelsConfig
{
    /// <summary>模型安装根目录；空 = %LOCALAPPDATA%\RealtimeSubtitle/models。</summary>
    public string Directory { get; set; } = "";

    /// <summary>
    /// 模型下载的基础 URL（&lt;BaseUrl&gt;/&lt;model-id&gt;.zip）。空 = 仅离线
    /// （检出 out/ 目录和 tools/model-convert/dist 包）。
    /// 使用例如内网镜像或 dist zip 的托管副本。huggingface.co 从某些网络无法访问，
    /// 因此没有内置公共 HF 默认值。
    /// </summary>
    public string BaseUrl { get; set; } = "";

    /// <summary>管道启动时是否自动下载缺失的模型。</summary>
    public bool AutoDownload { get; set; } = true;
}