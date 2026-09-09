using RealtimeSubtitle.Core.Configuration;

namespace RealtimeSubtitle.Core.Subtitle;

/// <summary>
/// 拥有字幕行（设计文档 §10/16）：部分更新当前预览，最终将行移至 Translating（翻译由管道注入），
/// 过期将行滑动到有界历史中。每次可见更改时引发 <see cref="CurrentChanged"/>。
/// </summary>
public sealed class SubtitleManager
{
    private readonly SubtitleConfig _config;
    private readonly List<SubtitleItem> _history = new();
    private int _nextId;

    /// <summary>构建管理器时使用的字幕样式配置。</summary>
    public SubtitleConfig Config => _config;

    /// <summary>最近的可见行（直到第一个部分到达之前为 null）。</summary>
    public SubtitleItem? Current { get; private set; }

    public IReadOnlyList<SubtitleItem> History => _history;

    public event Action<SubtitleSnapshot>? CurrentChanged;

    public SubtitleManager(SubtitleConfig config)
    {
        _config = config;
    }

    /// <summary>Partial ASR text updates the live preview only (P6-13: never overwrite a
    /// finalized line — the translation must land on the line it was requested for).
    /// Returns the line id the partial belongs to, so a provisional translation (P6-14:
    /// lyrics-scene partials) can be delivered to the same line.</summary>
    public int OnSourcePartial(string text, DateTimeOffset now)
    {
        if (Current is null || Current.State == SubtitleItemState.Expired)
        {
            Current = NewLine(text, now, SubtitleItemState.Recognizing);
        }
        else if (Current.State == SubtitleItemState.Recognizing)
        {
            Current.SourceText = text;
            Current.EndTime = DateTimeOffset.MaxValue; // sticky: stay until next sentence
        }
        else
        {
            // A final has already been submitted for the current line (Translating/Completed);
            // this partial belongs to the NEXT sentence — start a fresh preview line instead
            // of clobbering the finalized source text.
            PushLine(NewLine(text, now, SubtitleItemState.Recognizing));
        }

        Publish();
        return Current.Id;
    }

    /// <summary>Final ASR text replaces the preview and enters the translating state.
    /// Returns the stable line id the translation must be delivered to (P6-13).</summary>
    public int OnSourceFinal(string text, DateTimeOffset now)
    {
        var line = new SubtitleItem
        {
            Id = _nextId++,
            SourceText = text,
            StartTime = now,
            EndTime = DateTimeOffset.MaxValue, // sticky: stay until next sentence
            State = SubtitleItemState.Translating,
        };
        PushLine(line);
        return line.Id;
    }

    /// <summary>
    /// Injects the translation for the line identified by <paramref name="lineId"/> (P6-13).
    /// Matching by id instead of source text fixes the race where the next sentence's partial
    /// overwrote <see cref="Current"/>.SourceText and the late translation was dropped — the
    /// UI then never showed the translation.
    /// </summary>
    public void OnTranslated(int lineId, string translatedText, DateTimeOffset now)
    {
        if (lineId < 0)
        {
            // Fallback path (callers without a line id, e.g. legacy probes): match Current by text.
            if (Current is not null) OnTranslated(Current.Id, translatedText, now);
            return;
        }

        if (Current is not null && Current.Id == lineId)
        {
            Current.TranslatedText = translatedText;
            if (Current.State == SubtitleItemState.Translating)
            {
                Current.State = SubtitleItemState.Completed;
            }

            Current.EndTime = DateTimeOffset.MaxValue; // sticky: stay until next sentence
            Publish();
        }
        else
        {
            // Line already slid to history (next sentence started) — keep its data coherent
            // for history views, but no UI update is needed.
            var historic = _history.FirstOrDefault(h => h.Id == lineId);
            if (historic is not null)
            {
                historic.TranslatedText = translatedText;
                if (historic.State == SubtitleItemState.Translating)
                {
                    historic.State = SubtitleItemState.Completed;
                }
            }
        }
    }

    /// <summary>Compatibility overload: text-based lookup on the current line only.</summary>
    public void OnTranslated(string sourceText, string translatedText, DateTimeOffset now)
    {
        if (Current is not null && string.Equals(Current.SourceText, sourceText, StringComparison.Ordinal))
        {
            OnTranslated(Current.Id, translatedText, now);
        }
    }

    private SubtitleItem NewLine(string text, DateTimeOffset now, SubtitleItemState state)
    {
        return new SubtitleItem
        {
            Id = _nextId++,
            SourceText = text,
            StartTime = now,
            EndTime = DateTimeOffset.MaxValue, // sticky: stay until next sentence
            State = state,
        };
    }

    private void PushLine(SubtitleItem line)
    {
        if (Current is not null)
        {
            Current.State = SubtitleItemState.Expired;
            _history.Add(Current);
            if (_history.Count > 20) _history.RemoveAt(0);
        }

        Current = line;
        Publish();
    }

    /// <summary>
    /// P6-22: a pure-audio-event segment (interlude / [Music] burst — ASR emits junk with no
    /// lyrics) must CLEAR the overlay instead of freezing it on the last lyric line. The
    /// capture keeps feeding junk (never a Final with text), so without this the subtitle
    /// stays stuck on the previous line and reads as "字幕没输出了".
    /// </summary>
    public void Clear()
    {
        if (Current is null && _history.Count == 0) return;
        Current = null;
        _history.Clear();
        CurrentChanged?.Invoke(new SubtitleSnapshot(string.Empty, string.Empty, SubtitleItemState.Expired));
    }

    private void Publish()
    {
        if (Current is null) return;

        string source = Current.SourceText;
        string translated = Current.TranslatedText;
        string line1 = string.Empty;
        string line2 = string.Empty;

        switch (_config.Mode)
        {
            case "source":
                line1 = source;
                break;
            case "translation":
                // P6-14: partial-level translations may already be present while the line
                // is still Recognizing — show them instead of blinking the source.
                line1 = translated.Length > 0 ? translated : source;
                break;
            default: // bilingual
                line1 = source;
                line2 = translated.Length > 0 ? translated : string.Empty;
                break;
        }

        CurrentChanged?.Invoke(new SubtitleSnapshot(line1, line2, Current.State));
    }
}