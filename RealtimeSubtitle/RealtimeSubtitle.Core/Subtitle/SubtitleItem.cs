namespace RealtimeSubtitle.Core.Subtitle;

public enum SubtitleItemState
{
    Recognizing, // 源文本可见，翻译待处理
    Translating,  // 最终结果已接收，等待翻译结果
    Completed,    // 源文本 + 翻译均可用
    Expired,      // 超出显示窗口，移至历史
}

/// <summary>一行字幕（设计文档 §10）。</summary>
public sealed class SubtitleItem
{
    public required int Id { get; init; }
    public required string SourceText { get; set; }
    public string TranslatedText { get; set; } = string.Empty;
    public required DateTimeOffset StartTime { get; init; }
    public DateTimeOffset EndTime { get; set; }
    public SubtitleItemState State { get; set; } = SubtitleItemState.Recognizing;
}

/// <summary>由覆盖层的调度程序消耗的不可变快照。</summary>
public readonly record struct SubtitleSnapshot(
    string SourceText,
    string TranslatedText,
    SubtitleItemState State);