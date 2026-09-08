namespace RealtimeSubtitle.Core.Models;

/// <summary>
/// Locates model directories and offline bundles across the well-known roots:
/// the install root (%LOCALAPPDATA%\RealtimeSubtitle\models) and the model-convert
/// checkout layout (tools/model-convert/out, tools/model-convert/dist) found by walking
/// up from the current directory / app base. Relative-path lookups against the process
/// working directory alone are what previously produced "未找到翻译模型" when the app
/// was launched from a bin folder.
/// </summary>
public static class ModelPaths
{
    /// <summary>Installed-model root; downloads and extractions land here.</summary>
    public static string DefaultInstallRoot() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RealtimeSubtitle", "models");

    /// <summary>Roots that may contain a model-convert checkout (each yields tools/model-convert).</summary>
    public static IEnumerable<string> RepoSearchRoots()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            var dir = new DirectoryInfo(start);
            for (int depth = 0; dir is not null && depth <= 6; depth++, dir = dir.Parent)
            {
                string candidate = Path.Combine(dir.FullName, "tools", "model-convert");
                if (seen.Add(candidate)) yield return candidate;
            }
        }
    }

    /// <summary>First existing directory that contains every required file, or null.</summary>
    public static string? FindModelDir(ModelEntry entry, string? installRoot = null)
    {
        string root = string.IsNullOrWhiteSpace(installRoot) ? DefaultInstallRoot() : installRoot;
        var candidates = new List<string> { Path.Combine(root, entry.Id) };
        foreach (string repo in RepoSearchRoots())
        {
            candidates.Add(Path.Combine(repo, "out", entry.Id));
        }

        return candidates.FirstOrDefault(dir => IsComplete(entry, dir));
    }

    /// <summary>An offline bundle zip shipped next to the checkout (tools/model-convert/dist/&lt;id&gt;.zip).</summary>
    public static string? FindOfflineZip(ModelEntry entry)
    {
        foreach (string repo in RepoSearchRoots())
        {
            string zip = Path.Combine(repo, "dist", entry.Id + ".zip");
            if (File.Exists(zip)) return zip;
        }

        return null;
    }

    public static bool IsComplete(ModelEntry entry, string dir) =>
        Directory.Exists(dir) && entry.RequiredFiles.All(f => File.Exists(Path.Combine(dir, f)));
}
