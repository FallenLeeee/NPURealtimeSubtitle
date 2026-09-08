namespace RealtimeSubtitle.Core.Audio;

/// <summary>
/// 流式降混（N 声道 → 1）和采样率转换器（任意采样率 → 16 kHz 默认）
/// 使用线性插值后接一阶低通抗混叠滤波器。
/// 整数比（48k→16k、96k→16k）通过插值精确通过，因此常见的 3:1 和 6:1 情况下不引入重采样误差。
/// </summary>
public sealed class AudioResampler : IAudioResampler
{
    private readonly int _inChannels;
    private readonly int _inSampleRate;
    private readonly int _outSampleRate;
    private readonly double _ratio;      // 输入/输出
    private readonly double _lowPassA;

    private readonly List<float> _pending = new(64); // mono samples awaiting consumption
    private long _pendingStart;          // absolute stream index of _pending[0]
    private double _inPos;               // fractional input position of the next output sample
    private double _lowPassState;

    public AudioResampler(int inputChannels, int inputSampleRate, int outputSampleRate = 16000)
    {
        if (inputChannels <= 0) throw new ArgumentOutOfRangeException(nameof(inputChannels));
        if (inputSampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(inputSampleRate));
        if (outputSampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(outputSampleRate));

        _inChannels = inputChannels;
        _inSampleRate = inputSampleRate;
        _outSampleRate = outputSampleRate;
        _ratio = (double)inputSampleRate / outputSampleRate;

        // 一阶低通：截止频率 ≈ 0.42 * 输出采样率，在输出为 16k 时保持语音（< 8 kHz）完整
        // 同时衰减奈奎斯特频率以上的混叠。
        double fc = 0.42 * outputSampleRate;
        _lowPassA = 1.0 - Math.Exp(-2.0 * Math.PI * fc / outputSampleRate);
    }

    public void Reset()
    {
        _pending.Clear();
        _pendingStart = 0;
        _inPos = 0;
        _lowPassState = 0;
    }

    public int MaxOutputLength(int inputSampleCount)
    {
        int frames = inputSampleCount / _inChannels;
        return (int)Math.Ceiling(frames / _ratio) + 8;
    }

    public int Process(ReadOnlySpan<float> input, Span<float> output)
    {
        int frames = input.Length / _inChannels;
        int written = 0;

        // 将此块降混到待处理的单声道队列中。
        for (int f = 0; f < frames; f++)
        {
            double sum = 0;
            for (int c = 0; c < _inChannels; c++)
            {
                sum += input[f * _inChannels + c];
            }

            _pending.Add((float)(sum / _inChannels));
        }

        // 当我们有插值所需的两个采样时，发出输出。
        while (written < output.Length)
        {
            double rel = _inPos - _pendingStart;
            int i = (int)rel;
            if (i + 1 >= _pending.Count) break;

            double frac = rel - i;
            double s = _pending[i] * (1.0 - frac) + _pending[i + 1] * frac;

            // 一阶低通。
            _lowPassState += _lowPassA * (s - _lowPassState);
            output[written++] = (float)_lowPassState;

            _inPos += _ratio;
        }

        // 丢弃绝对索引落后于下一个输出位置的采样，保留下一次插值所需的两个采样。
        // 这确保每次调用时 _pending 都保持有界。
        int floorRel = (int)(_inPos - _pendingStart);
        int toRemove = Math.Max(0, Math.Min(floorRel, _pending.Count - 2));
        if (toRemove > 0)
        {
            _pending.RemoveRange(0, toRemove);
            _pendingStart += toRemove;
        }

        return written;
    }
}