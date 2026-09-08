namespace RealtimeSubtitle.Core.Translation;

/// <summary>
/// 机器翻译引擎接口面。实现一次翻译一个句子，贪婪地，
/// 报告实际使用的设备（NPU 与 CPU 回退）和经过的时间。
/// </summary>
public interface ITranslator : IDisposable
{
    /// <summary>翻译一个请求；对于单个坏句子永不抛出（失败时返回空）。</summary>
    Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken = default);

    /// <summary>此实例绑定的设备（"NPU" 或 "CPU"）。</summary>
    string Device { get; }
}