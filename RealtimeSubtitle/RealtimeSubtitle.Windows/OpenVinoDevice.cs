using OpenVinoSharp;

namespace RealtimeSubtitle.Windows;

/// <summary>
/// 共享 OpenVINO 设备配置（决策 P6-3）。NPU 编译器对于这些图较慢
/// （whisper-small 编码器 ≈ 14 秒，marian 编码器 ≈ 2 秒），
/// 并且经典 API 没有跨进程重用 CompiledModel 的方法，
/// 因此插件的磁盘 blob 缓存使模型热交换和应用重启变得可接受：
/// 缓存的编译在约 0.2 秒内加载。
/// </summary>
internal static class OpenVinoDevice
{
    private static string CacheDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RealtimeSubtitle", "cache", "openvino");

    /// <summary>为给定设备启用 OpenVINO blob 缓存（尽力而为）。</summary>
    public static void ApplyCache(OpenVinoSharp.Core core, params string[] devices)
    {
        try
        {
            Directory.CreateDirectory(CacheDir);
            foreach (string device in devices)
            {
                core.SetProperty(device, "CACHE_DIR", CacheDir);
            }
        }
        catch
        {
            // 缓存仅是性能优化 —— 永不因它而使管道失败。
        }
    }
}
