namespace RealtimeSubtitle.Core.Audio;

/// <summary>
/// 回环音频捕获源。从当前默认渲染设备获取原始混合格式交错浮点采样，
/// 并写入 <see cref="Buffer"/>。
/// </summary>
/// <remarks>
/// 捕获在专用线程上启动，该线程执行 COM 读取并追加到环形缓冲区。
/// 它从不阻塞在消费者上，也不执行 AI 工作。
/// </remarks>
public interface IAudioCapturer : IDisposable
{
    /// <summary>端点的混合格式（采样率/声道/位）。</summary>
    AudioFormat Format { get; }

    /// <summary>接收原始交错混合格式浮点数的环形缓冲区。</summary>
    RingBuffer<float> Buffer { get; }

    /// <summary>当发生不可恢复的捕获错误时引发（例如设备丢失）。</summary>
    event Action<Exception>? Error;

    /// <summary>启动捕获。立即返回；帧异步流动。</summary>
    void Start(CancellationToken token);

    /// <summary>停止捕获并加入捕获线程。</summary>
    void Stop();
}