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
    public void StripAnnotationsCleans(string input, string expected)
        => Assert.Equal(expected, AsrJunkFilter.StripAnnotations(input));
}