namespace RealtimeSubtitle.Core.Speech;

/// <summary>
/// 语音识别接口面。音频通过 <see cref="IPcm16Sink.WritePcm16"/> 到达（16 kHz/16 位/单声道）。
/// 具体后端可以是 Windows AI 语音 API 或传统的 Windows.Media 识别器。
/// 实现必须在回调线程上从不执行翻译工作——它们引发事件，让管道决定工作在哪里发生。
/// </summary>
public interface ISpeechRecognizer : IPcm16Sink, IDisposable
{
    /// <summary>部分结果（Recognizing）——用于实时源预览。</summary>
    event Action<AsrPartial>? Partial;

    /// <summary>最终结果（Recognized）——翻译触发器。</summary>
    event Action<AsrFinal>? Final;

    /// <summary>意外失败（模型卸载、音频推送错误、后端崩溃）。</summary>
    event Action<Exception>? Error;

    /// <summary>启动识别。立即返回；结果异步到达。</summary>
    void Start(CancellationToken token);

    /// <summary>优雅停止识别（如果后端支持则刷新待处理的最终结果）。</summary>
    void Stop();
}