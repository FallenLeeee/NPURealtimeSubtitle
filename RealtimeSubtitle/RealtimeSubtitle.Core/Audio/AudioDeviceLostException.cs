namespace RealtimeSubtitle.Core.Audio;

/// <summary>当音频端点消失时引发（未插入、驱动程序重置、睡眠/唤醒）。</summary>
public sealed class AudioDeviceLostException : Exception
{
    public AudioDeviceLostException(string message = "The audio device was lost or invalidated.")
        : base(message)
    {
    }
}