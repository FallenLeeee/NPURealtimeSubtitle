namespace RealtimeSubtitle.Core.Speech;

/// <summary>
/// Filters ASR results that are "audio-event annotations" rather than speech — whisper tags
/// instrumental music / applause / laughter as <c>[Music]</c> (also seen as a mid-generation
/// fragment like <c>Music]</c>), SenseVoice emits <c>&lt;|Music|&gt;</c>-style event tokens,
/// and these junk bits must not surface as subtitles or displace real lyrics (P6-7).
///
/// Two modes (P6-11): <see cref="IsJunk"/> drops a line entirely (pure annotation),
/// <see cref="StripAnnotations"/> removes a *leading* annotation so real text that follows
/// it ("[Music] Hello love") still surfaces ("Hello love").
/// </summary>
public static class AsrJunkFilter
{
    private static readonly string[] Standalone =
    {
        "music", "♪", "♫", "song", "歌曲", "音乐", "歌声",
        "applause", "掌声", "laughter", "笑声", "silence",
        "instrumental", "noise", "melody",
    };

    private static readonly string[] Prefixes =
    {
        "[music", "[song", "[歌曲", "[音乐", "[applause", "[laughter", "(music", "music]",
        "[instrumental", "[noise", "[melody", "♪", "♫",
        "<|music|>", "<|applause|>", "<|laughter|>", "<|noise|>", "<|silence|>",
    };

    /// <summary>Trim characters applied before matching (surviving annotation fragments).</summary>
    private static readonly char[] TrimChars =
        { '[', ']', '(', ')', ' ', '\t', '♪', '♫', ':', '：', '·', '—', '-', '<', '>', '"', '.', '!', '？', '?' };

    /// <summary>True when the text is only an audio-event annotation (or empty).</summary>
    public static bool IsJunk(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return true;

        string t = text.Trim();
        string lower = t.ToLowerInvariant();

        // 1) Raw prefix match FIRST (markers kept) so "[Music playing]", "<|music|>", "♪ ♪",
        //    "Music]" all hit before any trimming removes the bracket that anchors them.
        foreach (string prefix in Prefixes)
        {
            if (lower.StartsWith(prefix, StringComparison.Ordinal)) return true;
        }

        // 2) Then strip surviving marker fragments and match standalone words:
        //    "Music]" -> "Music"; "<|music|>" -> "music"; "♪ ♪" -> "".
        string stripped = t.Trim(TrimChars);
        if (stripped.Length == 0) return true;

        string lowered = stripped.ToLowerInvariant();
        foreach (string j in Standalone)
        {
            if (lowered == j) return true;
        }

        return false;
    }

    /// <summary>
    /// Strips a leading audio-event annotation so text behind it survives, then re-checks the
    /// remainder. Returns the cleaned text, or empty when the whole line is junk.
    /// Handles: "[Music] Hello love" → "Hello love"; "♪ ♪" / "[Music]" / "Music]" → "".
    /// A line that is not junk returns unchanged (trimmed).
    /// </summary>
    public static string StripAnnotations(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        string trimmed = text.Trim();
        string stripped = StripLeadingAnnotation(trimmed);
        if (stripped.Length == 0) return string.Empty;

        return IsJunk(stripped) ? string.Empty : stripped.Trim();
    }

    /// <summary>Removes one leading "[...]" / "(...)" / "♪ ♫" annotation (Whisper-style).</summary>
    private static string StripLeadingAnnotation(string text)
    {
        // "[Music] Hello love" → strip "[Music]"
        int close = text.IndexOf(']');
        if (text.StartsWith('[') && close > 0)
        {
            string inside = text[1..close];
            if (IsAnnotationWord(inside))
            {
                return text[(close + 1)..].TrimStart();
            }
        }

        // "(music)" (SenseVoice-style event) → strip
        int parenClose = text.IndexOf(')');
        if (text.StartsWith('(') && parenClose > 0)
        {
            string inside = text[1..parenClose];
            if (IsAnnotationWord(inside))
            {
                return text[(parenClose + 1)..].TrimStart();
            }
        }

        // "<|music|>" style with no following text → handled by IsJunk; with following text
        // ("<|music|> hello") strip the token.
        int lt = text.IndexOf("<|", StringComparison.Ordinal);
        if (lt >= 0)
        {
            int gt = text.IndexOf("|>", lt + 2, StringComparison.Ordinal);
            if (gt > 0)
            {
                string inside = text[(lt + 2)..gt];
                if (IsAnnotationWord(inside))
                {
                    string before = text[..lt];
                    string after = text[(gt + 2)..];
                    string joined = (before + after).Trim();
                    return joined;
                }
            }
        }

        // Leading "♪ ♫" run → strip
        int i = 0;
        while (i < text.Length && (text[i] == '♪' || text[i] == '♫' || char.IsWhiteSpace(text[i])))
        {
            i++;
        }

        return i > 0 ? text[i..].TrimStart() : text;
    }

    private static bool IsAnnotationWord(string inside)
    {
        string lowered = inside.Trim().Trim(TrimChars).ToLowerInvariant();
        if (lowered.Length == 0) return false;
        if (Standalone.Contains(lowered)) return true;

        // multi-word annotations like "music playing" / "applause" inside brackets
        return lowered.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .All(word => Standalone.Contains(word));
    }
}