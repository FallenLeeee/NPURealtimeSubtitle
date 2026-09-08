namespace RealtimeSubtitle.Core.Speech;

public enum AsrModelReadyState
{
    /// <summary>模型已安装，设备支持该 API。</summary>
    Ready,

    /// <summary>受支持，但需要下载模型（同意 + EnsureReadyAsync）。</summary>
    NotReady,

    /// <summary>用户在 Windows 设置中禁用了 AI 组件。</summary>
    DisabledByUser,

    /// <summary>硬件/驱动程序/策略不支持该 API。回退到传统后端。</summary>
    NotSupportedOnCurrentSystem,

    /// <summary>无法确定状态。</summary>
    Unknown,
}

/// <summary>
/// 拥有 Windows AI 语音模型生命周期：就绪检查、用户同意门控，
/// 以及通过 EnsureReadyAsync 进行（重新）下载。
/// </summary>
public interface ISpeechModelManager
{
    /// <summary>当前模型就绪状态；在创建识别器之前调用。</summary>
    AsrModelReadyState GetReadyState();

    /// <summary>当必须下载模型时为真（即状态为 <see cref="AsrModelReadyState.NotReady"/>）。</summary>
    bool RequiresConsent { get; }

    /// <summary>
    /// 确保语音模型就绪。调用者负责在 <see cref="RequiresConsent"/> 为真时显示同意对话框（生产 UX）。
    /// </summary>
    Task EnsureReadyAsync(IProgress<double>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>为此后端创建具体识别器（未就绪时抛出）。</summary>
    ISpeechRecognizer CreateRecognizer();
}