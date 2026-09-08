namespace RealtimeSubtitle.Core.Audio;

/// <summary>
/// 流式降混 + 采样率转换器，生成目标采样率的单声道流。
/// 实现必须在 <see cref="Process"/> 调用之间保持状态（块边界不得引入不连续性），
/// 并且必须在 <see cref="Reset"/> 后可重用。
/// </summary>
public interface IAudioResampler
{
    /// <summary>清除所有内部流式状态。</summary>
    void Reset();

    /// <summary>给定输入采样数时输出采样数的上限。</summary>
    int MaxOutputLength(int inputSampleCount);

    /// <summary>
    /// 消耗交错输入采样并写入单声道输出采样。
    /// </summary>
    /// <returns>写入到 <paramref name="output"/> 的采样数。</returns>
    int Process(ReadOnlySpan<float> input, Span<float> output);
}