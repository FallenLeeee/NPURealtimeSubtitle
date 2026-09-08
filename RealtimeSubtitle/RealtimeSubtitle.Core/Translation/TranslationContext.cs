namespace RealtimeSubtitle.Core.Translation;

/// <summary>
/// 可选的短上下文构建器（设计文档 §9）：将前 1-2 个句子添加到当前句子，
/// 以确保代词/主题连贯性。默认关闭（优先考虑延迟）。
/// 具体的 MT 后端决定如何分隔前缀。
/// </summary>
public static class TranslationContext
{
    /// <summary>将最近的历史 + 当前句子合并为单个请求的上下文。</summary>
    public static string BuildPrefix(IReadOnlyList<string> previousSentences, int maxSentences)
    {
        if (maxSentences <= 0 || previousSentences.Count == 0) return string.Empty;
        var taken = previousSentences.Take(maxSentences).ToArray();
        return string.Join(" || ", taken);
    }
}