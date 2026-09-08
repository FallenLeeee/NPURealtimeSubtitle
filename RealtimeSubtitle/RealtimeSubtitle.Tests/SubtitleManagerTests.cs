using RealtimeSubtitle.Core.Configuration;
using RealtimeSubtitle.Core.Subtitle;

namespace RealtimeSubtitle.Tests;

/// <summary>
/// Guards the subtitle update path (P6-13): translation results must land on the exact
/// subtitle line BY ID, and a partial from the NEXT sentence must not clobber the source
/// text of a line that is still waiting for its translation. Regression test for the bug
/// where the UI showed the source but never the translation.
/// </summary>
public class SubtitleManagerTests
{
    private static SubtitleManager Create() =>
        new(new SubtitleConfig { Mode = "bilingual", DurationMs = 6000 });

    [Fact]
    public void FinalThenTranslated_ByLineId_UpdatesUi()
    {
        var m = Create();
        SubtitleSnapshot? snapshot = null;
        m.CurrentChanged += s => snapshot = s;

        int lineId = m.OnSourceFinal("hello world", DateTimeOffset.Now);
        Assert.Equal("hello world", m.Current!.SourceText);
        Assert.Equal(SubtitleItemState.Translating, m.Current.State);

        m.OnTranslated(lineId, "你好，世界", DateTimeOffset.Now);
        Assert.Equal("你好，世界", m.Current.TranslatedText);
        Assert.Equal(SubtitleItemState.Completed, m.Current.State);
        Assert.NotNull(snapshot);
        Assert.Equal("hello world", snapshot.Value.SourceText);
        Assert.Equal("你好，世界", snapshot.Value.TranslatedText);
    }

    [Fact]
    public void NextPartial_DoesNotClobberLineWaitingForTranslation()
    {
        var m = Create();
        int lineId = m.OnSourceFinal("first sentence", DateTimeOffset.Now);

        // A partial from the NEXT sentence arrives while the first is still translating:
        // a fresh preview line opens (Current moves on), the finalized line slides to history.
        m.OnSourcePartial("second", DateTimeOffset.Now);
        Assert.Equal("second", m.Current!.SourceText);
        Assert.Equal(SubtitleItemState.Recognizing, m.Current.State);

        // The finalized line's source text must be untouched in history.
        var first = m.History.FirstOrDefault(h => h.Id == lineId);
        Assert.NotNull(first);
        Assert.Equal("first sentence", first.SourceText);

        // The late translation still lands on the first line by id (data stays coherent
        // for history views); the line stays Expired since it already slid off the display.
        m.OnTranslated(lineId, "第一句", DateTimeOffset.Now);
        Assert.Equal("第一句", first.TranslatedText);
        Assert.Equal(SubtitleItemState.Expired, first.State);
    }

    [Fact]
    public void TranslationWithUnknownLineId_IsIgnoredSafely()
    {
        var m = Create();
        m.OnSourceFinal("a", DateTimeOffset.Now);
        m.OnTranslated(99999, "译文", DateTimeOffset.Now); // no such line
        Assert.Equal(string.Empty, m.Current!.TranslatedText);
        Assert.Equal(SubtitleItemState.Translating, m.Current.State);
    }

    [Fact]
    public void TextBasedOverload_StillMatchesCurrentLine()
    {
        var m = Create();
        m.OnSourceFinal("where are you going?", DateTimeOffset.Now);
        m.OnTranslated("where are you going?", "你要去哪里？", DateTimeOffset.Now);
        Assert.Equal("你要去哪里？", m.Current!.TranslatedText);
        Assert.Equal(SubtitleItemState.Completed, m.Current.State);
    }

    [Fact]
    public void PartialBeforeFinal_UpdatesPreviewOnly()
    {
        var m = Create();
        SubtitleSnapshot? snapshot = null;
        m.CurrentChanged += s => snapshot = s;

        m.OnSourcePartial("hel", DateTimeOffset.Now);
        Assert.Equal("hel", m.Current!.SourceText);
        Assert.Equal(SubtitleItemState.Recognizing, m.Current.State);

        // Second partial of the SAME sentence updates the preview (still Recognizing).
        m.OnSourcePartial("hello", DateTimeOffset.Now);
        Assert.Equal("hello", m.Current.SourceText);
        Assert.Equal(SubtitleItemState.Recognizing, m.Current.State);
        Assert.Equal("hello", snapshot!.Value.SourceText);
    }

    [Fact]
    public void PartialTranslation_LandsOnPreviewLine_AndShowsWhileRecognizing()
    {
        // P6-14: in continuous speech (lyrics), finals are rare — partials must carry a
        // provisional translation. The preview line stays Recognizing (a final may still
        // refine it) but the bilingual snapshot already shows the translation.
        var m = Create();
        SubtitleSnapshot? snapshot = null;
        m.CurrentChanged += s => snapshot = s;

        int lineId = m.OnSourcePartial("時を巡って今", DateTimeOffset.Now);
        Assert.Equal(SubtitleItemState.Recognizing, m.Current!.State);

        m.OnTranslated(lineId, "沿着时间流转，如今", DateTimeOffset.Now);
        Assert.Equal("沿着时间流转，如今", m.Current.TranslatedText);
        Assert.Equal(SubtitleItemState.Recognizing, m.Current.State); // final may still refine
        Assert.NotNull(snapshot);
        Assert.Equal("時を巡って今", snapshot.Value.SourceText);
        Assert.Equal("沿着时间流转，如今", snapshot.Value.TranslatedText);
    }

    [Fact]
    public void PartialTranslation_OfNextSentence_DoesNotClobberFinalizedLine()
    {
        // P6-14: a partial from the NEXT sentence opens a fresh preview line; its provisional
        // translation must go to THAT line, leaving the finalized line untouched.
        var m = Create();
        int firstId = m.OnSourceFinal("first sentence", DateTimeOffset.Now);

        int nextId = m.OnSourcePartial("second", DateTimeOffset.Now);
        Assert.NotEqual(firstId, nextId);
        Assert.Equal("second", m.Current!.SourceText);

        m.OnTranslated(nextId, "第二句", DateTimeOffset.Now);
        Assert.Equal("第二句", m.Current.TranslatedText);
        Assert.Equal(SubtitleItemState.Recognizing, m.Current.State);

        var first = m.History.FirstOrDefault(h => h.Id == firstId);
        Assert.NotNull(first);
        Assert.Equal(string.Empty, first.TranslatedText);
        Assert.Equal(SubtitleItemState.Expired, first.State);
    }

    [Fact]
    public void Clear_EmitsEmptySnapshot_ForInterludeJunk()
    {
        // P6-22: a pure-[Music] interlude must blank the overlay, not freeze the last lyric.
        var m = Create();
        SubtitleSnapshot? snapshot = null;
        m.CurrentChanged += s => snapshot = s;

        m.OnSourcePartial("I feel love", DateTimeOffset.Now);
        Assert.Equal("I feel love", m.Current!.SourceText);

        m.Clear();
        Assert.Null(m.Current);
        Assert.Empty(m.History);
        Assert.NotNull(snapshot);
        Assert.Equal(string.Empty, snapshot.Value.SourceText);
        Assert.Equal(string.Empty, snapshot.Value.TranslatedText);
    }
}