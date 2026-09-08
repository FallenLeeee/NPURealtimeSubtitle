namespace RealtimeSubtitle.Core.Audio;

public enum VadState
{
    Silence,
    Speech,
}

public readonly record struct VadResult(VadState State, float Rms, bool Transition);

/// <summary>
/// 基于 16 kHz 单声道采样的轻量级语音活动检测器。
/// 基于 RMS，带有最小语音持续时间和拖尾，因此短促的点击和词间间隙不会切断话语。
/// </summary>
public sealed class VoiceActivityDetector
{
    private readonly float _threshold;
    private readonly int _minSpeechFrames; // 在处理块中
    private readonly int _hangoverFrames;
    private readonly int _sampleRate;

    private VadState _state = VadState.Silence;
    private int _speechFrames;
    private int _hangoverFramesLeft;

    public VoiceActivityDetector(float threshold = 0.01f, int minSpeechMs = 150, int hangoverMs = 400, int sampleRate = 16000)
    {
        _threshold = threshold;
        _sampleRate = sampleRate;
        _minSpeechFrames = Math.Max(1, (int)Math.Round(minSpeechMs / 1000.0 * 100.0)); // assume 10 ms chunks
        _hangoverFrames = Math.Max(1, (int)Math.Round(hangoverMs / 1000.0 * 100.0));
    }

    public VadState State => _state;

    /// <summary>处理一个块（名义上约 10 毫秒的单声道采样）并返回新状态。</summary>
    public VadResult Process(ReadOnlySpan<float> monoSamples)
    {
        float rms = ComputeRms(monoSamples);
        bool voiced = rms >= _threshold;
        bool transition = false;

        switch (_state)
        {
            case VadState.Silence:
                if (voiced)
                {
                    _speechFrames++;
                    if (_speechFrames >= _minSpeechFrames)
                    {
                        _state = VadState.Speech;
                        _hangoverFramesLeft = _hangoverFrames;
                        transition = true;
                    }
                }
                else
                {
                    _speechFrames = 0;
                }

                break;

            case VadState.Speech:
                if (voiced)
                {
                    _hangoverFramesLeft = _hangoverFrames;
                }
                else if (--_hangoverFramesLeft <= 0)
                {
                    _state = VadState.Silence;
                    _speechFrames = 0;
                    transition = true;
                }

                break;
        }

        return new VadResult(_state, rms, transition);
    }

    public void Reset()
    {
        _state = VadState.Silence;
        _speechFrames = 0;
        _hangoverFramesLeft = 0;
    }

    private static float ComputeRms(ReadOnlySpan<float> samples)
    {
        if (samples.IsEmpty) return 0f;

        double sum = 0;
        foreach (float s in samples)
        {
            sum += s * s;
        }

        return (float)Math.Sqrt(sum / samples.Length);
    }
}