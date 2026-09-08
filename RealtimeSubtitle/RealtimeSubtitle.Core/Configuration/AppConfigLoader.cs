using System.Text.Json;
using System.Text.Json.Serialization;

namespace RealtimeSubtitle.Core.Configuration;

public static class AppConfigLoader
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static string DefaultConfigPath { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RealtimeSubtitle", "config.json");

    /// <summary>
    /// Loads the config from <paramref name="path"/> (defaults to %LOCALAPPDATA%\RealtimeSubtitle\config.json).
    /// Missing file or unparsable content yield defaults; invalid field values are replaced by defaults
    /// and reported via <see cref="AppConfig.Validate"/> issues.
    /// </summary>
    public static (AppConfig Config, IReadOnlyList<string> Issues) Load(string? path = null)
    {
        string configPath = string.IsNullOrWhiteSpace(path) ? DefaultConfigPath : path;
        var issues = new List<string>();

        if (!File.Exists(configPath))
        {
            return (new AppConfig(), issues);
        }

        try
        {
            string json = File.ReadAllText(configPath);
            var config = JsonSerializer.Deserialize<AppConfig>(json, Options);
            if (config is null)
            {
                issues.Add("Config file parsed to null; using defaults.");
                return (new AppConfig(), issues);
            }

            issues.AddRange(config.Validate());
            return (config, issues);
        }
        catch (Exception ex)
        {
            issues.Add($"Config file could not be read ({ex.GetType().Name}: {ex.Message}); using defaults.");
            return (new AppConfig(), issues);
        }
    }

    /// <summary>Atomically saves the config (temp file + move).</summary>
    public static void Save(AppConfig config, string? path = null)
    {
        string configPath = string.IsNullOrWhiteSpace(path) ? DefaultConfigPath : path;
        string? directory = Path.GetDirectoryName(configPath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        string temp = configPath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(config, Options));
        File.Move(temp, configPath, overwrite: true);
    }
}