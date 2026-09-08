namespace RealtimeSubtitle.Core.Models;

/// <summary>A model bundle the app can provision (checkout dir, offline zip, or remote zip).</summary>
public sealed record ModelEntry(
    string Id,
    string DisplayName,
    string MarkerFile,
    IReadOnlyList<string> RequiredFiles);

/// <summary>
/// Well-known models used by the pipeline. The artifacts are produced by
/// tools/model-convert/convert_models.py and can be (a) used in place from the checkout
/// (tools/model-convert/out/&lt;id&gt;), (b) shipped as an offline zip
/// (tools/model-convert/dist/&lt;id&gt;.zip), or (c) downloaded from Models.BaseUrl.
/// </summary>
public static class ModelCatalog
{
    public const string WhisperModel = "whisper-base-int8";   // default ASR model (accuracy)
    public const string WhisperTinyInt8 = "whisper-tiny-int8"; // smaller, faster
    public const string WhisperSmallInt8 = "whisper-small-int8"; // larger, most accurate
    public const string WhisperEnModel = "whisper-en-base-int8";   // English-only whisper
    public const string WhisperEnTinyInt8 = "whisper-en-tiny-int8";
    public const string WhisperEnSmallInt8 = "whisper-en-small-int8";
    public const string Qwen3AsrModel = "qwen3-asr-1.7b-int8";  // Chinese-focused ASR (P6-5)
    public const string SenseVoiceModel = "sensevoice-small-int8"; // Japanese-focused ASR (P6-6)
    public const string OpusMtEnZhInt8 = "opus-mt-en-zh-int8";
    public const string OpusMtJaZhInt8 = "opus-mt-ja-zh-int8"; // Japanese → Chinese translation (P6-10)

    /// <summary>Maps the config ASR model size (tiny|base|small) to its multilingual bundle id.</summary>
    public static string WhisperModelId(string size) => size switch
    {
        "tiny" => WhisperTinyInt8,
        "small" => WhisperSmallInt8,
        _ => WhisperModel,
    };

    /// <summary>Maps the config ASR model size to its English-only (whisper.en) bundle id.</summary>
    public static string WhisperEnModelId(string size) => size switch
    {
        "tiny" => WhisperEnTinyInt8,
        "small" => WhisperEnSmallInt8,
        _ => WhisperEnModel,
    };

    /// <summary>
    /// Routing (P6-4): the ASR bundle depends on the selected source language —
    /// zh → Qwen3-ASR, ja → SenseVoice, en → whisper.en, auto/others → multilingual whisper.
    /// </summary>
    public static string AsrModelId(string language, string size) => language switch
    {
        "zh" => Qwen3AsrModel,
        "ja" => SenseVoiceModel,
        "en" => WhisperEnModelId(size),
        _ => WhisperModelId(size),
    };

    /// <summary>Display name of the ASR model bundle routed for a language.</summary>
    public static string AsrModelDisplayName(string language, string size) =>
        Get(AsrModelId(language, size)).DisplayName;

    /// <summary>
    /// Translation routing (P6-10): the target is always zh; the source follows the ASR
    /// language selection. ja → dedicated ja→zh marian; everything else (en/auto) → en→zh.
    /// </summary>
    public static string TranslationModelId(string language) =>
        language == "ja" ? OpusMtJaZhInt8 : OpusMtEnZhInt8;

    private static readonly ModelEntry Whisper = new(
        WhisperModel,
        "Whisper base（语音识别）",
        "whisper_meta.json",
        new[]
        {
            "openvino_encoder_model.xml",
            "openvino_encoder_model.bin",
            "openvino_decoder_model.xml",
            "openvino_decoder_model.bin",
            "mel_filters.bin",
            "whisper_meta.json",
        });

    private static readonly ModelEntry WhisperTiny = new(
        WhisperTinyInt8,
        "Whisper tiny（语音识别，备用）",
        "whisper_meta.json",
        new[]
        {
            "openvino_encoder_model.xml",
            "openvino_encoder_model.bin",
            "openvino_decoder_model.xml",
            "openvino_decoder_model.bin",
            "mel_filters.bin",
            "whisper_meta.json",
        });

    private static readonly ModelEntry WhisperSmall = new(
        WhisperSmallInt8,
        "Whisper small（语音识别，最准）",
        "whisper_meta.json",
        new[]
        {
            "openvino_encoder_model.xml",
            "openvino_encoder_model.bin",
            "openvino_decoder_model.xml",
            "openvino_decoder_model.bin",
            "mel_filters.bin",
            "whisper_meta.json",
        });

    private static readonly ModelEntry WhisperEn = new(
        WhisperEnModel,
        "Whisper .en base（英语专精）",
        "whisper_meta.json",
        new[]
        {
            "openvino_encoder_model.xml",
            "openvino_encoder_model.bin",
            "openvino_decoder_model.xml",
            "openvino_decoder_model.bin",
            "mel_filters.bin",
            "whisper_meta.json",
        });

    private static readonly ModelEntry WhisperEnTiny = new(
        WhisperEnTinyInt8,
        "Whisper .en tiny（英语专精，备用）",
        "whisper_meta.json",
        new[]
        {
            "openvino_encoder_model.xml",
            "openvino_encoder_model.bin",
            "openvino_decoder_model.xml",
            "openvino_decoder_model.bin",
            "mel_filters.bin",
            "whisper_meta.json",
        });

    private static readonly ModelEntry WhisperEnSmall = new(
        WhisperEnSmallInt8,
        "Whisper .en small（英语专精，最准）",
        "whisper_meta.json",
        new[]
        {
            "openvino_encoder_model.xml",
            "openvino_encoder_model.bin",
            "openvino_decoder_model.xml",
            "openvino_decoder_model.bin",
            "mel_filters.bin",
            "whisper_meta.json",
        });

    private static readonly ModelEntry Qwen3Asr = new(
        Qwen3AsrModel,
        "Qwen3-ASR 1.7B（中文专精，慢但准）",
        "prompt_template.json",
        new[]
        {
            "audio_encoder_model.xml",
            "audio_encoder_model.bin",
            "thinker_embeddings_model.xml",
            "thinker_embeddings_model.bin",
            "decoder_prefill_kv_model.xml",
            "decoder_prefill_kv_model.bin",
            "decoder_kv_model.xml",
            "decoder_kv_model.bin",
            "vocab.json",
            "merges.txt",
            "prompt_template.json",
            "preprocessor_config.json",
            "mel_filters_128.bin",
        });

    private static readonly ModelEntry SenseVoice = new(
        SenseVoiceModel,
        "SenseVoiceSmall（日语专精，单程快速）",
        "sensevoice_meta.json",
        new[]
        {
            "model_fp32_static.onnx", // pure-FP32 static [1,N,560] — correct on NPU (P6-9)
            "model_static.onnx",      // static int8 — deterministic CPU fallback
            "model_quant.onnx",       // dynamic fallback for older dirs
            "am.mvn",
            "tokens.json",
            "config.yaml",
            "sensevoice_meta.json",
        });

    private static readonly ModelEntry Marian = new(
        OpusMtEnZhInt8,
        "Marian opus-mt en→zh（翻译）",
        "tokenizer.json",
        new[]
        {
            "openvino_encoder_model.xml",
            "openvino_encoder_model.bin",
            "openvino_decoder_model.xml",
            "openvino_decoder_model.bin",
            "tokenizer.json",
        });

    private static readonly ModelEntry MarianJaZh = new(
        OpusMtJaZhInt8,
        "Marian opus-mt ja→zh（日语翻译）",
        "tokenizer.json",
        new[]
        {
            "openvino_encoder_model.xml",
            "openvino_encoder_model.bin",
            "openvino_decoder_model.xml",
            "openvino_decoder_model.bin",
            "tokenizer.json",
        });

    public static IReadOnlyList<ModelEntry> All { get; } = new[]
    {
        Whisper, WhisperTiny, WhisperSmall,
        WhisperEn, WhisperEnTiny, WhisperEnSmall,
        Qwen3Asr, SenseVoice,
        Marian, MarianJaZh,
    };

    public static ModelEntry Get(string id) =>
        All.FirstOrDefault(e => e.Id == id)
        ?? throw new ArgumentException($"Unknown model id: {id}", nameof(id));
}
