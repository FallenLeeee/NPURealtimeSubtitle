namespace RealtimeSubtitle.Core.Speech;

/// <summary>
/// SenseVoiceSmall ONNX 后端（P6-6）的 Kaldi 兼容对数梅尔滤波器，
/// 精确镜像 kaldi-native-fbank 的默认值（通过数值验证与
/// tools/model-convert/testrefs/kaldi_*.bin 对比）：
///   帧 25 ms / 移动 10 ms，每帧 DC 去除 + 预加重 0.97，汉明窗，
///   snip_edges 帧框架，FFT 大小 512（四舍五入到 2 的幂），|X|² 功率谱，
///   HTK 比例上的 80 个梅尔 bin（2595·log10(1+f/700)，低频 20 Hz，高频 8 kHz），
///   未归一化三角形滤波器，log(max(x, FLT_EPSILON)) — ln(FLT_EPSILON) ≈ -15.9424。
/// 识别器随后堆叠 LFR 7/6 帧并在将 ONNX 模型输入之前应用 AM.MVN（AddShift/Rescale）。
/// </summary>
public sealed class KaldiFbank
{
    public const int SampleRate = 16000;
    public const int FrameLength = 400;  // 25 ms
    public const int FrameShift = 160;   // 10 ms
    public const int FftSize = 512;
    public const int NumBins = 257;      // FftSize/2 + 1
    public const int NumMels = 80;
    private const float Preemph = 0.97f;
    private const float LowFreq = 20f;
    private const float HighFreq = 8000f;

    private readonly float[] _hann;      // [FrameLength]
    private readonly float[] _filters;   // [NumMels * NumBins] 行优先

    public KaldiFbank()
    {
        _hann = new float[FrameLength];
        for (int n = 0; n < FrameLength; n++)
        {
            _hann[n] = (float)(0.54 - 0.46 * Math.Cos(2.0 * Math.PI * n / (FrameLength - 1)));
        }

        _filters = BuildSlaneyFilters();
    }

    /// <summary>给定采样数的 snip_edges 帧数。</summary>
    public static int FrameCount(int sampleCount) =>
        sampleCount >= FrameLength ? 1 + (sampleCount - FrameLength) / FrameShift : 0;

    /// <summary>计算（T × NumMels）对数梅尔特征，行优先。</summary>
    public float[] ComputeLogMel(float[] audio16k)
    {
        int frames = FrameCount(audio16k.Length);
        if (frames <= 0) return Array.Empty<float>();

        var output = new float[frames * NumMels];
        var re = new double[FftSize];
        var im = new double[FftSize];
        var frame = new double[FrameLength];
        var power = new double[NumBins];

        for (int f = 0; f < frames; f++)
        {
            // 帧 + 每帧 DC 去除 + 预加重 + 汉明（kaldi ProcessWindow 顺序）。
            double mean = 0;
            int src = f * FrameShift;
            for (int i = 0; i < FrameLength; i++) mean += audio16k[src + i];
            mean /= FrameLength;

            for (int i = 0; i < FrameLength; i++) frame[i] = audio16k[src + i] - mean;
            for (int i = FrameLength - 1; i > 0; i--) frame[i] -= Preemph * frame[i - 1];

            for (int i = 0; i < FrameLength; i++)
            {
                re[i] = frame[i] * _hann[i];
                im[i] = 0;
            }

            for (int i = FrameLength; i < FftSize; i++)
            {
                re[i] = 0;
                im[i] = 0;
            }

            Fft512(re, im);

            for (int k = 0; k < NumBins; k++) power[k] = re[k] * re[k] + im[k] * im[k];

            int row = f * NumMels;
            for (int m = 0; m < NumMels; m++)
            {
                double acc = 0;
                int fBase = m * NumBins;
                for (int k = 0; k < NumBins; k++) acc += _filters[fBase + k] * power[k];
                // kaldi-native-fbank: log(max(x, FLT_EPSILON)) — ln(FLT_EPSILON) ≈ -15.9424。
                output[row + m] = (float)Math.Log(Math.Max(acc, 1.1920929e-7));
            }
        }

        return output;
    }

    /// <summary>
    /// LFR 7/6 堆叠 + AM.MVN 归一化：y = (stacked + addShift) * rescale。
    /// 输入是（T × featDim）行优先；输出是（T_lfr × featDim*m）行优先。
    /// </summary>
    public static float[] LfrCmvn(float[] feats, int T, int featDim, int m, int n,
        float[] addShift, float[] rescale, out int tLfr)
    {
        int outDim = featDim * m;
        if (T < m)
        {
            tLfr = 0;
            return Array.Empty<float>();
        }

        tLfr = (T - m) / n + 1;
        var output = new float[tLfr * outDim];
        for (int i = 0; i < tLfr; i++)
        {
            int rowBase = i * n * featDim;
            int outBase = i * outDim;
            for (int j = 0; j < m; j++)
            {
                int inBase = rowBase + j * featDim;
                int cmvnBase = j * featDim; // 每个 LFR 行的相同每行 CMVN
                int o = outBase + cmvnBase;
                for (int d = 0; d < featDim; d++)
                {
                    output[o + d] = (feats[inBase + d] + addShift[cmvnBase + d]) * rescale[cmvnBase + d];
                }
            }
        }

        return output;
    }

    // ---------- HTK 梅尔滤波器（kaldi-native-fbank 默认，无归一化）----------

    private float[] BuildSlaneyFilters()
    {
        var weights = new float[NumMels * NumBins];

        double fMinMel = HzToMel(LowFreq);
        double fMaxMel = HzToMel(HighFreq);
        var melF = new double[NumMels + 2];
        for (int i = 0; i < melF.Length; i++)
        {
            melF[i] = fMinMel + (fMaxMel - fMinMel) * i / (NumMels + 1);
        }

        for (int m = 0; m < NumMels; m++)
        {
            double leftHz = MelToHz(melF[m]);
            double centerHz = MelToHz(melF[m + 1]);
            double rightHz = MelToHz(melF[m + 2]);
            int row = m * NumBins;

            for (int k = 0; k < NumBins; k++)
            {
                double f = k * SampleRate / (double)FftSize;
                double w = 0;
                if (f >= leftHz && f <= centerHz)
                {
                    w = (f - leftHz) / (centerHz - leftHz);
                }
                else if (f > centerHz && f <= rightHz)
                {
                    w = (rightHz - f) / (rightHz - centerHz);
                }

                weights[row + k] = (float)w;
            }
        }

        return weights;
    }

    /// <summary>HTK 梅尔比例：mel(f) = 2595·log10(1 + f/700)。</summary>
    private static double HzToMel(double freq) => 2595.0 * Math.Log10(1.0 + freq / 700.0);

    private static double MelToHz(double mel) => 700.0 * (Math.Pow(10.0, mel / 2595.0) - 1.0);

    /// <summary>原地迭代基-2 FFT（大小 512）。</summary>
    private static void Fft512(double[] re, double[] im)
    {
        const int n = FftSize;

        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }

        for (int len = 2; len <= n; len <<= 1)
        {
            double ang = -2.0 * Math.PI / len;
            double wRe = Math.Cos(ang);
            double wIm = Math.Sin(ang);
            for (int i = 0; i < n; i += len)
            {
                double curRe = 1, curIm = 0;
                int half = len / 2;
                for (int k = 0; k < half; k++)
                {
                    int a = i + k;
                    int b = a + half;
                    double uRe = re[a], uIm = im[a];
                    double vRe = re[b] * curRe - im[b] * curIm;
                    double vIm = re[b] * curIm + im[b] * curRe;
                    re[a] = uRe + vRe;
                    im[a] = uIm + vIm;
                    re[b] = uRe - vRe;
                    im[b] = uIm - vIm;

                    double nextRe = curRe * wRe - curIm * wIm;
                    curIm = curRe * wIm + curIm * wRe;
                    curRe = nextRe;
                }
            }
        }
    }
}
