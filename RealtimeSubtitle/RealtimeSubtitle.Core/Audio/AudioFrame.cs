namespace RealtimeSubtitle.Core.Audio;

/// <summary>捕获设备混合格式描述。</summary>
public readonly record struct AudioFormat(int SampleRate, int Channels, int BitsPerSample, int FormatTag)
{
    /// <summary>WAVE_FORMAT_IEEE_FLOAT</summary>
    public bool IsFloat32 => FormatTag == 3 && BitsPerSample == 32;

    /// <summary>WAVE_FORMAT_PCM</summary>
    public bool IsPcm16 => FormatTag == 1 && BitsPerSample == 16;
}

/// <summary>捕获设备混合格式的交错 PCM 采样块。</summary>
/// <remarks>
/// <see cref="Samples"/> 是由生产者/消费者管道拥有的堆数组，稍后阶段可能从池中租用。
/// 对于第一阶段，它是一个普通数组；不要修改它。
/// </remarks>
public readonly record struct AudioFrame(float[] Samples, int Channels, int SampleRate, DateTimeOffset Timestamp);