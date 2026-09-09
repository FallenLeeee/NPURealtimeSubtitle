using RealtimeSubtitle.Core.Speech;

namespace RealtimeSubtitle.Tests;

/// <summary>Guards the audio-event annotation filter (P6-7): [Music] must never surface as a
/// subtitle, while real lyrics/quotes that merely contain the word must pass.</summary>
public class AsrJunkFilterTests
{
    [Theory]
    [InlineData("[Music]")]
    [InlineData("Music]")]
    [InlineData("[Music")]
    [InlineData("Music")]
    [InlineData("♪")]
    [InlineData("♪ ♪")]
    [InlineData("[歌曲]")]
    [InlineData("歌曲")]
    [InlineData("[Applause]")]
    [InlineData("<|music|>")]
    [InlineData("[Music playing]")]
    [InlineData("[Instrumental]")]
    [InlineData("[noise]")]
    [InlineData("")]
    [InlineData("  ")]
    // P6-19: whisper.en song-section tags arrive as *music* / *outro* / *intro* —
    // these must be filtered like the bracket form.
    [InlineData("*music*")]
    [InlineData("*outro*")]
    [InlineData("*intro*")]
    [InlineData("*applause*")]
    [InlineData("(outro music)")]
    // P6-21: whisper.en BGM descriptors "(upbeat music)" / "(soft music)" —
    // every descriptor word is an event word → filtered.
    [InlineData("(upbeat music)")]
    [InlineData("(mellow music)")]
    // Music-track audio tags (whisper.en on instrumental/pauses)
    [InlineData("[BLANK_AUDIO]")]
    [InlineData("[blank_audio]")]
    [InlineData("[no audio]")]
    [InlineData("[no speech]")]
    [InlineData("[buzzing]")]
    [InlineData("[static]")]
    [InlineData("[white noise]")]
    // P6-23: whisper.en "(inaudible)" / "[inaudible]" tags — not lyrics.
    [InlineData("[inaudible]")]
    [InlineData("(inaudible)")]
    [InlineData("[unintelligible]")]
    public void JunkIsFiltered(string text) => Assert.True(AsrJunkFilter.IsJunk(text));

    [Theory]
    [InlineData("Music is my life")]
    [InlineData("hello world")]
    [InlineData("你好，这是字幕系统的测试")]
    [InlineData("The song I played last night")]
    [InlineData("这是歌词")]
    public void RealTextPasses(string text) => Assert.False(AsrJunkFilter.IsJunk(text));

    // P6-11: leading annotations must be stripped so lyrics behind them survive,
    // while pure-annotation lines are dropped entirely.
    [Theory]
    [InlineData("[Music] Hello love", "Hello love")]
    [InlineData("<|music|> hello", "hello")]
    [InlineData("[Music] 你好，这是歌词", "你好，这是歌词")]
    [InlineData("♪ ♫ hello", "hello")]
    [InlineData("Music]", "")]
    [InlineData("[Music]", "")]
    [InlineData("♪ ♪", "")]
    [InlineData("<|applause|>", "")]
    [InlineData("  hello  ", "hello")]
    [InlineData("Where are you going?", "Where are you going?")]
    // P6-19: star-wrapped whisper.en section tags — stripped when followed by lyrics,
    // dropped entirely when alone.
    [InlineData("*music*", "")]
    [InlineData("*outro*", "")]
    [InlineData("*music* I feel love", "I feel love")]
    [InlineData("(outro music)", "")]
    // P6-21: descriptor + music → dropped; descriptor + real lyric → lyric survives.
    [InlineData("(upbeat music)", "")]
    [InlineData("(upbeat music) hello", "hello")]
    public void StripAnnotationsCleans(string input, string expected)
        => Assert.Equal(expected, AsrJunkFilter.StripAnnotations(input));
}