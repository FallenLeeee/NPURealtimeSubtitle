using RealtimeSubtitle.Core.Audio;

namespace RealtimeSubtitle.Tests;

public class AudioResamplerTests
{
    private static float[] SineStereo(double freq, int sampleRate, double seconds, double amp = 0.5)
    {
        int frames = (int)(sampleRate * seconds);
        var data = new float[frames * 2];
        for (int f = 0; f < frames; f++)
        {
            double v = amp * Math.Sin(2 * Math.PI * freq * f / sampleRate);
            data[f * 2] = (float)v;
            data[f * 2 + 1] = (float)v;
        }

        return data;
    }

    private static double GoertzelMag(ReadOnlySpan<float> x, double targetFreq, int sampleRate)
    {
        double w = 2 * Math.PI * targetFreq / sampleRate;
        double coeff = 2 * Math.Cos(w);
        double s0 = 0, s1 = 0, s2 = 0;
        for (int i = 0; i < x.Length; i++)
        {
            s0 = x[i] + coeff * s1 - s2;
            s2 = s1;
            s1 = s0;
        }

        return Math.Sqrt(s1 * s1 + s2 * s2 - coeff * s1 * s2);
    }

    [Fact]
    public void FortyEightKHzToSixteenKHz_PreservesToneFrequency()
    {
        var resampler = new AudioResampler(inputChannels: 2, inputSampleRate: 48000, outputSampleRate: 16000);
        float[] input = SineStereo(440, 48000, 1.0);
        var output = new float[resampler.MaxOutputLength(input.Length)];

        int n = resampler.Process(input, output);

        Assert.True(n > 15000 && n < 16500, $"unexpected output length {n}");
        double mag440 = GoertzelMag(output.AsSpan(0, n), 440, 16000);
        double mag1000 = GoertzelMag(output.AsSpan(0, n), 1000, 16000);
        double mag100 = GoertzelMag(output.AsSpan(0, n), 100, 16000);

        Assert.True(mag440 > 20 * mag1000, $"440 Hz magnitude {mag440} should dominate 1000 Hz {mag1000}");
        Assert.True(mag440 > 20 * mag100, $"440 Hz magnitude {mag440} should dominate 100 Hz {mag100}");
    }

    [Theory]
    [InlineData(48000, 16000)]
    [InlineData(44100, 16000)]
    [InlineData(96000, 16000)]
    public void OutputLength_MatchesRateRatio(int inRate, int outRate)
    {
        var resampler = new AudioResampler(inputChannels: 2, inputSampleRate: inRate, outputSampleRate: outRate);
        double seconds = 0.2;
        float[] input = SineStereo(300, inRate, seconds);
        var output = new float[resampler.MaxOutputLength(input.Length)];

        int n = resampler.Process(input, output);

        int expected = (int)(inRate * seconds * outRate / (double)inRate);
        Assert.InRange(n, expected - 4, expected + 4);
    }

    [Fact]
    public void StereoDownmix_AverageOfInvertedChannels_IsZero()
    {
        var resampler = new AudioResampler(inputChannels: 2, inputSampleRate: 48000, outputSampleRate: 16000);
        // Left = +0.5, Right = -0.5 constant → mono average must be ≈ 0.
        var input = new float[48000 * 2];
        for (int i = 0; i < input.Length; i += 2)
        {
            input[i] = 0.5f;
            input[i + 1] = -0.5f;
        }

        var output = new float[resampler.MaxOutputLength(input.Length)];
        int n = resampler.Process(input, output);

        for (int i = 0; i < n; i++)
        {
            Assert.InRange(Math.Abs(output[i]), 0f, 0.01f);
        }
    }

    [Fact]
    public void ChunkedFeeding_MatchesSingleShot()
    {
        int inRate = 48000;
        float[] input = SineStereo(1000, inRate, 0.5);
        var single = new AudioResampler(2, inRate, 16000);
        var singleOut = new float[single.MaxOutputLength(input.Length)];
        int singleN = single.Process(input, singleOut);

        var chunked = new AudioResampler(2, inRate, 16000);
        var chunkedOut = new float[chunked.MaxOutputLength(input.Length)];
        int total = 0;
        const int chunk = 74; // 37 stereo frames; not a divisor of 48000 → forces cross-boundary interpolation
        for (int off = 0; off < input.Length; off += chunk)
        {
            int len = Math.Min(chunk, input.Length - off);
            total += chunked.Process(input.AsSpan(off, len), chunkedOut.AsSpan(total));
        }

        Assert.Equal(singleN, total);
        for (int i = 0; i < total; i++)
        {
            Assert.InRange(Math.Abs(singleOut[i] - chunkedOut[i]), 0f, 1e-4f);
        }
    }

    [Fact]
    public void Reset_ClearsStreamingState()
    {
        var resampler = new AudioResampler(2, 48000, 16000);
        var buff = new float[resampler.MaxOutputLength(4800)];
        resampler.Process(new float[4800], buff);

        resampler.Reset();

        var fresh = new AudioResampler(2, 48000, 16000);
        var buff2 = new float[resampler.MaxOutputLength(4800)];
        int n1 = resampler.Process(new float[4800], buff);
        int n2 = fresh.Process(new float[4800], buff2);
        Assert.Equal(n1, n2);
    }
}