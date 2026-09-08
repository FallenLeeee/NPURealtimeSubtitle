namespace RealtimeSubtitle.Core.Translation;

public readonly record struct TranslationRequest(
    string SourceText,
    DateTimeOffset Timestamp,
    string? ContextPrefix = null, // optional: previous 1-2 sentences joined (TranslationContext)
    int SubtitleLineId = -1);     // stable line id the translation should land on (P6-13)

public readonly record struct TranslationResult(
    string TargetText,
    string Device,
    TimeSpan Elapsed,
    bool FallbackToCpu);