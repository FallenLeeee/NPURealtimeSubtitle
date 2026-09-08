using System.Numerics;

namespace RealtimeSubtitle.Core.Speech;

/// <summary>
/// OpenAI-whisper 对数梅尔频谱图（n_fft=400, hop=160, 80 slaney mels, 16 kHz）。
/// 梅尔滤波器在模型转换时转储（mel_filters.bin），因此 slaney
/// 计算在 Python 中进行；此类实现 STFT→功率→滤波→对数归一化链
/// （torch.stft center=True 语义，幅度减去奈奎斯特 bin，然后
/// log10(clamp(1e-10)) → max−8 截断 → (x+4)/4）。输出：[nMels * 帧]，帧上限
/// 为 3000（30 秒 @ 100 帧/秒）；音频先零填充到 30 秒，匹配特征提取器的形状约定 [1, 80, 3000]。
///
/// 性能（P6-1）：DFT 使用预计算的浮点基和
/// <see cref="Vector{T}"/> SIMD 点积计算，帧并行处理。之前的标量双精度实现成本约 0.4 毫秒每 10 毫秒音频，
/// 这主导了短片段；<see cref="ComputeReference"/> 保留该版本用于回归测试。
/// </summary>
public sealed class MelSpectrogram
{
    private const int NFFT = 400;
    private const int Hop = 160;
    private const int MaxFrames = 3000;
    private const int SampleRate = 16000;
    private const double PadSeconds = 30.0;

    private readonly int _nMels;
    private readonly int _nBins; // columns of the dumped filterbank (200 or 201)
    private readonly float[] _filters; // row-major [nMels * nBins]
    private readonly float[] _hann;
    private readonly float[] _cosTable; // [nBins * NFFT]: cos(2πkn/NFFT)
    private readonly float[] _sinTable; // [nBins * NFFT]: sin(2πkn/NFFT)

    public MelSpectrogram(string filtersBinPath)
    {
        byte[] raw = File.ReadAllBytes(filtersBinPath);
        int header = 2 * sizeof(int);
        int rows = BitConverter.ToInt32(raw, raw.Length - 8);
        int cols = BitConverter.ToInt32(raw, raw.Length - 4);
        if (raw.Length - header != rows * cols * sizeof(float))
        {
            throw new InvalidDataException($"mel_filters.bin size mismatch: {raw.Length - header} != {rows * cols * 4}");
        }

        _nMels = rows;
        _nBins = cols;
        _filters = new float[rows * cols];
        Buffer.BlockCopy(raw, 0, _filters, 0, rows * cols * sizeof(float));

        _hann = new float[NFFT];
        for (int n = 0; n < NFFT; n++)
        {
            // torch.hann_window(400): 0.5 - 0.5*cos(2πn/(N-1))
            _hann[n] = (float)(0.5 - 0.5 * Math.Cos(2.0 * Math.PI * n / (NFFT - 1)));
        }

        _cosTable = new float[cols * NFFT];
        _sinTable = new float[cols * NFFT];
        for (int k = 0; k < cols; k++)
        {
            double phaseStep = -2.0 * Math.PI * k / NFFT;
            for (int n = 0; n < NFFT; n++)
            {
                double angle = phaseStep * n;
                _cosTable[k * NFFT + n] = (float)Math.Cos(angle);
                _sinTable[k * NFFT + n] = (float)Math.Sin(angle);
            }
        }
    }

    public int FramesCap => MaxFrames;

    /// <summary>Computes [nMels * 3000] log-mel features (0-padded tail beyond audio).</summary>
    public float[] Compute(float[] audio16k)
    {
        int target = (int)(PadSeconds * SampleRate);
        int n = Math.Min(audio16k.Length, target); // truncate longer audio to 30 s
        int frames = Math.Min(1 + n / Hop, MaxFrames); // L' = n + 400 → 1 + (L' - 400)/hop
        var output = new float[_nMels * MaxFrames]; // trailing frames stay zero
        if (frames <= 0) return output;

        // Windowed frames (center-padded by n_fft/2, torch.stft center=True).
        var win = new float[frames * NFFT];
        int pad = NFFT / 2;
        for (int f = 0; f < frames; f++)
        {
            int src0 = f * Hop - pad;
            int dst = f * NFFT;
            for (int j = 0; j < NFFT; j++)
            {
                int src = src0 + j;
                win[dst + j] = (uint)src < (uint)n ? audio16k[src] * _hann[j] : 0f;
            }
        }

        // Power spectrum per frame (SIMD dot products against the precomputed basis).
        var mags = new float[frames * _nBins];
        Parallel.For(0, frames, f => PowerSpectrum(win, f * NFFT, mags, f * _nBins));

        // Mel projection + log, tracking the global max for the (x+4)/4 normalization.
        var mel = new float[frames * _nMels];
        float maxSpec = float.NegativeInfinity;
        var maxLock = new object();
        Parallel.For(0, frames, () => float.NegativeInfinity, (f, _, localMax) =>
        {
            int magBase = f * _nBins;
            int melBase = f * _nMels;
            for (int m = 0; m < _nMels; m++)
            {
                float acc = Dot(_filters, m * _nBins, mags, magBase, _nBins);
                if (acc < 1e-10f) acc = 1e-10f;
                float v = MathF.Log10(acc);
                mel[melBase + m] = v;
                if (v > localMax) localMax = v;
            }

            return localMax;
        }, localMax =>
        {
            lock (maxLock)
            {
                if (localMax > maxSpec) maxSpec = localMax;
            }
        });

        // normalize: clip to max-8 then (x+4)/4, then transpose to [mels, frames]
        Parallel.For(0, frames, f =>
        {
            float floor = maxSpec - 8.0f;
            int melBase = f * _nMels;
            for (int m = 0; m < _nMels; m++)
            {
                float v = mel[melBase + m];
                if (v < floor) v = floor;
                output[m * MaxFrames + f] = (v + 4.0f) * 0.25f;
            }
        });

        return output;
    }

    /// <summary>Scalar double reference implementation (kept for regression tests).</summary>
    internal float[] ComputeReference(float[] audio16k)
    {
        int target = (int)(PadSeconds * SampleRate);
        int n = Math.Min(audio16k.Length, target);
        var padded = new double[n + 2 * (NFFT / 2)];
        for (int i = 0; i < n; i++) padded[NFFT / 2 + i] = audio16k[i];

        int frames = Math.Min(1 + n / Hop, MaxFrames);
        var magnitudes = new double[frames * _nBins];
        var frame = new double[NFFT];

        for (int f = 0; f < frames; f++)
        {
            int start = f * Hop;
            for (int nn = 0; nn < NFFT; nn++) frame[nn] = padded[start + nn] * _hann[nn];

            for (int k = 0; k < _nBins; k++)
            {
                double re = 0, im = 0;
                int tbl = k * NFFT;
                for (int nn = 0; nn < NFFT; nn++)
                {
                    double x = frame[nn];
                    re += x * _cosTable[tbl + nn];
                    im += x * _sinTable[tbl + nn];
                }

                magnitudes[f * _nBins + k] = re * re + im * im;
            }
        }

        double maxSpec = double.NegativeInfinity;
        var mel = new double[frames * _nMels];
        for (int f = 0; f < frames; f++)
        {
            for (int m = 0; m < _nMels; m++)
            {
                double acc = 0;
                int row = m * _nBins;
                int colBase = f * _nBins;
                for (int k = 0; k < _nBins; k++) acc += _filters[row + k] * magnitudes[colBase + k];
                double v = Math.Max(acc, 1e-10);
                mel[f * _nMels + m] = Math.Log10(v);
                if (mel[f * _nMels + m] > maxSpec) maxSpec = mel[f * _nMels + m];
            }
        }

        var output = new float[_nMels * MaxFrames];
        for (int f = 0; f < frames; f++)
        {
            for (int m = 0; m < _nMels; m++)
            {
                double v = mel[f * _nMels + m];
                if (v < maxSpec - 8.0) v = maxSpec - 8.0;
                output[m * MaxFrames + f] = (float)((v + 4.0) / 4.0);
            }
        }

        return output;
    }

    /// <summary>Re²+Im² for every bin of one windowed frame, SIMD-accumulated.</summary>
    private void PowerSpectrum(float[] win, int winOffset, float[] mags, int magOffset)
    {
        int width = Vector<float>.Count;
        for (int k = 0; k < _nBins; k++)
        {
            int tbl = k * NFFT;
            var accRe = Vector<float>.Zero;
            var accIm = Vector<float>.Zero;
            int j = 0;
            for (; j <= NFFT - width; j += width)
            {
                var x = new Vector<float>(win, winOffset + j);
                accRe += x * new Vector<float>(_cosTable, tbl + j);
                accIm += x * new Vector<float>(_sinTable, tbl + j);
            }

            float re = Vector.Dot(accRe, Vector<float>.One);
            float im = Vector.Dot(accIm, Vector<float>.One);
            for (; j < NFFT; j++)
            {
                float x = win[winOffset + j];
                re += x * _cosTable[tbl + j];
                im += x * _sinTable[tbl + j];
            }

            mags[magOffset + k] = re * re + im * im;
        }
    }

    private static float Dot(float[] a, int aOffset, float[] b, int bOffset, int count)
    {
        int width = Vector<float>.Count;
        var acc = Vector<float>.Zero;
        int i = 0;
        for (; i <= count - width; i += width)
        {
            acc += new Vector<float>(a, aOffset + i) * new Vector<float>(b, bOffset + i);
        }

        float sum = Vector.Dot(acc, Vector<float>.One);
        for (; i < count; i++) sum += a[aOffset + i] * b[bOffset + i];
        return sum;
    }
}
