using RealtimeSubtitle.Core.Configuration;
using RealtimeSubtitle.Core.Diagnostics;

namespace RealtimeSubtitle.Core.Models;

/// <summary>当找不到或无法提供模型时抛出；<see cref="Message"/> 是面向用户的（中文）。</summary>
public sealed class ModelMissingException : Exception
{
    public ModelMissingException(string message) : base(message) { }
}

/// <summary>
/// 确保管道启动前模型包可用。优先级：
///   1. 已安装/完整的目录（安装根目录或 model-convert 检出 out/ 目录）；
///   2. 随检出一起分发的离线 zip（tools/model-convert/dist/<id>.zip）；
///   3. 来自 <see cref="ModelsConfig.BaseUrl"/> 的远程 zip（例如内网镜像）。
/// 如果都不成功，则抛出带有可操作指导的 <see cref="ModelMissingException"/>。
/// </summary>
public sealed class ModelProvisioner
{
    private readonly ModelsConfig _config;
    private readonly LogSink? _log;

    public ModelProvisioner(ModelsConfig config, LogSink? log = null)
    {
        _config = config ?? new ModelsConfig();
        _log = log;
    }

    public string InstallRoot =>
        string.IsNullOrWhiteSpace(_config.Directory)
            ? ModelPaths.DefaultInstallRoot()
            : Path.GetFullPath(_config.Directory);

    /// <summary>模型第一个存在的完整目录，或 null。</summary>
    public string? FindLocal(string modelId) =>
        ModelPaths.FindModelDir(ModelCatalog.Get(modelId), InstallRoot);

    /// <summary>
    /// 返回包含模型完整副本的目录，在需要时从离线包或远程源提供。
    /// 进度通过 <paramref name="progress"/> 报告（UI 友好）。
    /// </summary>
    public async Task<string> EnsureAsync(string modelId, IProgress<ModelProgress>? progress = null, CancellationToken ct = default)
    {
        ModelEntry entry = ModelCatalog.Get(modelId);

        string? local = FindLocal(modelId);
        if (local is not null) return local;

        string targetDir = Path.Combine(InstallRoot, entry.Id);

        // 随检出一起分发的离线包。
        string? zip = ModelPaths.FindOfflineZip(entry);
        if (zip is not null)
        {
            _log?.Info("Provisioning {0} from offline bundle {1}", entry.Id, zip);
            await ModelDownloader.ProvisionFromZipAsync(zip, targetDir, progress, ct);
            if (!ModelPaths.IsComplete(entry, targetDir))
            {
                throw new ModelMissingException($"{entry.DisplayName}：本地包解压后文件不完整（{zip}）。");
            }

            _log?.Info("Provisioned {0} → {1}", entry.Id, targetDir);
            return targetDir;
        }

        // 远程源（Models.BaseUrl）。
        if (!string.IsNullOrWhiteSpace(_config.BaseUrl))
        {
            string zipUrl = $"{_config.BaseUrl.TrimEnd('/')}/{entry.Id}.zip";
            _log?.Info("Downloading {0} from {1}", entry.Id, zipUrl);
            string zipPath = Path.Combine(InstallRoot, entry.Id + ".zip");
            try
            {
                await ModelDownloader.DownloadZipAsync(zipUrl, zipPath, progress, ct);
                progress?.Report(new ModelProgress("解压模型", 0.9));
                await Task.Run(() => ModelDownloader.ExtractZip(zipPath, targetDir, progress), ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new ModelMissingException($"{entry.DisplayName}：下载失败（{ex.Message}）。请检查网络或改用本地离线包。");
            }

            if (!ModelPaths.IsComplete(entry, targetDir))
            {
                throw new ModelMissingException($"{entry.DisplayName}：下载内容不完整。");
            }

            _log?.Info("Provisioned {0} → {1}", entry.Id, targetDir);
            return targetDir;
        }

        throw new ModelMissingException(BuildMissingMessage(entry));
    }

    private string BuildMissingMessage(ModelEntry entry) =>
        $"{entry.DisplayName} 未找到（{entry.Id}）。\n" +
        "请任选一种方式补齐模型：\n" +
        "  1) 把模型 zip 放到 tools/model-convert/dist/<id>.zip（本地离线包）；\n" +
        "  2) 在 %LOCALAPPDATA%\\RealtimeSubtitle\\config.json 中设置 Models.BaseUrl（模型下载地址）；\n" +
        "  3) 或先运行 tools/model-convert 下的转换脚本生成模型。";
}
