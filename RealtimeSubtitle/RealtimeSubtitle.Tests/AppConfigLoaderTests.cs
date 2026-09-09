using RealtimeSubtitle.Core.Configuration;

namespace RealtimeSubtitle.Tests;

public class AppConfigLoaderTests
{
    [Fact]
    public void MissingFile_ReturnsDefaults_NoIssues()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "nope.json");

        var (config, issues) = AppConfigLoader.Load(path);

        Assert.NotNull(config);
        Assert.Empty(issues);
        Assert.Equal("zh-CN", config.TargetLanguage);
        Assert.Equal("auto", config.Translation.Device);
        Assert.Equal(16000, config.Audio.SampleRate);
    }

    [Fact]
    public void SaveThenLoad_RoundTrips()
    {
        string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        string path = Path.Combine(dir, "config.json");
        var config = new AppConfig
        {
            SourceLanguage = "ja-JP",
            TargetLanguage = "zh-CN",
            SubtitleMode = "translation",
            Translation = { Device = "CPU", MaxQueue = 4 },
            Subtitle = { FontSize = 40, Opacity = 0.7 },
        };

        AppConfigLoader.Save(config, path);
        var (loaded, issues) = AppConfigLoader.Load(path);

        Assert.Empty(issues);
        Assert.Equal("ja-JP", loaded.SourceLanguage);
        Assert.Equal("translation", loaded.SubtitleMode);
        Assert.Equal("CPU", loaded.Translation.Device);
        Assert.Equal(4, loaded.Translation.MaxQueue);
        Assert.Equal(40, loaded.Subtitle.FontSize);
        Assert.Equal(0.7, loaded.Subtitle.Opacity);
    }

    [Fact]
    public void InvalidJson_FallsBackToDefaults()
    {
        string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        string path = Path.Combine(dir, "config.json");
        Directory.CreateDirectory(dir);
        File.WriteAllText(path, "{ not valid json !!!");

        var (config, issues) = AppConfigLoader.Load(path);

        Assert.NotNull(config);
        Assert.Single(issues);
        Assert.Contains("could not be read", issues[0]);
    }

    [Fact]
    public void InvalidValues_AreReported()
    {
        string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        string path = Path.Combine(dir, "config.json");
        Directory.CreateDirectory(dir);
        File.WriteAllText(path, """
            {
              "subtitleMode": "xyz",
              "audio": { "sampleRate": 48000 },
              "translation": { "device": "GPU" },
              "asr": { "device": "TPU" }
            }
            """);

        var (config, issues) = AppConfigLoader.Load(path);

        Assert.NotNull(config);
        Assert.Contains(issues, i => i.Contains("SubtitleMode", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(issues, i => i.Contains("16000", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(issues, i => i.Contains("Translation.Device", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(issues, i => i.Contains("Asr.Device", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AsrDevice_DefaultsToAuto()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "nope.json");

        var (config, issues) = AppConfigLoader.Load(path);

        Assert.Empty(issues);
        Assert.Equal("auto", config.Asr.Device);
    }

    [Fact]
    public void ZhBackend_DefaultsToSenseVoice()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "nope.json");

        var (config, issues) = AppConfigLoader.Load(path);

        Assert.Empty(issues);
        Assert.Equal("sensevoice", config.Asr.ZhBackend);
    }

    [Fact]
    public void ZhBackend_Invalid_IsReported()
    {
        string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        string path = Path.Combine(dir, "config.json");
        Directory.CreateDirectory(dir);
        File.WriteAllText(path, """{ "asr": { "zhBackend": "whisper" } }""");

        var (_, issues) = AppConfigLoader.Load(path);

        Assert.Contains(issues, i => i.Contains("Asr.ZhBackend", StringComparison.Ordinal));
    }
}