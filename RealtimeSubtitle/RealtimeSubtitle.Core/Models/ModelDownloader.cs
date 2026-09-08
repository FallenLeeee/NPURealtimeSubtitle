using System.IO.Compression;

namespace RealtimeSubtitle.Core.Models;

/// <summary>资源准备的进度；<see cref="Fraction"/> 是 0..1，<see cref="Stage"/> 是简短标签。</summary>
public sealed record ModelProgress(string Stage, double Fraction);

/// <summary>HTTP 下载 + zip 解压辅助工具（进度感知、原子操作、取消安全）。</summary>
public static class ModelDownloader
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(30) };

    /// <summary>将 <paramref name="zipUrl"/> 下载到 <paramref name="targetZipPath"/> 并显示进度。</summary>
    public static async Task DownloadZipAsync(string zipUrl, string targetZipPath, IProgress<ModelProgress>? progress, CancellationToken ct)
    {
        string? directory = Path.GetDirectoryName(targetZipPath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        string tmp = targetZipPath + ".part";
        using (var response = await Http.GetAsync(zipUrl, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            response.EnsureSuccessStatusCode();
            long total = response.Content.Headers.ContentLength ?? 0;
            progress?.Report(new ModelProgress("连接服务器", 0.0));

            await using var src = await response.Content.ReadAsStreamAsync(ct);
            await using var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
            var buffer = new byte[1 << 16];
            long done = 0;
            int read;
            while ((read = await src.ReadAsync(buffer, ct)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, read), ct);
                done += read;
                if (total > 0)
                {
                    double fraction = Math.Min(0.85, done / (double)total);
                    progress?.Report(new ModelProgress($"下载 {done / (1024d * 1024):F1}/{total / (1024d * 1024):F1} MB", fraction));
                }
            }
        }

        if (File.Exists(targetZipPath)) File.Delete(targetZipPath);
        File.Move(tmp, targetZipPath);
        progress?.Report(new ModelProgress("下载完成", 0.88));
    }

    /// <summary>
    /// 将 zip 解压到 <paramref name="targetDir"/>（原子操作：先解压到临时目录，
    /// 然后移动文件）。处理平面 zip 和带有单个顶级文件夹的 zip。
    /// </summary>
    public static void ExtractZip(string zipPath, string targetDir, IProgress<ModelProgress>? progress)
    {
        Directory.CreateDirectory(targetDir);
        string tmpDir = targetDir + ".tmp";
        if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, recursive: true);
        Directory.CreateDirectory(tmpDir);

        try
        {
            ZipFile.ExtractToDirectory(zipPath, tmpDir);

            string[] directFiles = Directory.GetFiles(tmpDir);
            string[] topDirs = Directory.GetDirectories(tmpDir);
            string source = topDirs.Length == 1 && directFiles.Length == 0 ? topDirs[0] : tmpDir;

            foreach (string file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(source, file);
                string dest = Path.Combine(targetDir, rel);
                string? destDir = Path.GetDirectoryName(dest);
                if (!string.IsNullOrEmpty(destDir)) Directory.CreateDirectory(destDir);
                File.Move(file, dest, overwrite: true);
            }
        }
        finally
        {
            if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, recursive: true);
        }
    }

    /// <summary>将现有本地 zip 解压到 <paramref name="targetDir"/>（用于离线包）。</summary>
    public static Task ProvisionFromZipAsync(string zipPath, string targetDir, IProgress<ModelProgress>? progress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        progress?.Report(new ModelProgress("解压本地模型包", 0.9));
        return Task.Run(() => ExtractZip(zipPath, targetDir, progress), ct);
    }
}
