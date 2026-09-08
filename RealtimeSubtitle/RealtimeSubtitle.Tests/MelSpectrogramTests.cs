using RealtimeSubtitle.Core.Speech;

namespace RealtimeSubtitle.Tests;

/// <summary>
/// Guards the SIMD/parallel mel rewrite (P6-1) against the scalar double reference: the
/// recognizer's encoder contract is a [1,80,3000] tensor, so a silent numerical regression
/// here would degrade every transcription.
/// </summary>
public class MelSpectrogramTests
{
    /// <summary>Locates a converted model dir (mel_filters.bin) by walking up from the test bin.</summary>
    private static string? FindFilters()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "tools", "model-convert", "out", "whisper-base-int8", "mel_filters.bin");
            if (File.Exists(candidate)) return candidate;

            string direct = Path.Combine(dir.FullName, "RealtimeSubtitle", "tools", "model-convert", "out", "whisper-base-int8", "mel_filters.bin");
            if (File.Exists(direct)) return direct;
        }

        return null;
    }

    private static float[] Speech(int samples)
    {
        // Deterministic pseudo-speech: two tones + a decaying envelope, enough spectral variety.
        var audio = new float[samples];
        for (int i = 0; i < samples; i++)
        {
            double t = i / 16000.0;
            double env = Math.Exp(-1.5 * (t % 0.5));
            audio[i] = (float)(env * (0.4 * Math.Sin(2 * Math.PI * 220 * t) + 0.2 * Math.Sin(2 * Math.PI * 1370 * t)));
        }

        return audio;
    }

    [Fact]
    public void FastPathMatchesReference()
    {
        string? filters = FindFilters();
        if (filters is null) return; // converted model not present in this checkout — nothing to compare

        var mel = new MelSpectrogram(filters!);
        float[] audio = Speech(16000 * 4); // 4 s → 401 frames, well under the 30 s cap

        float[] fast = mel.Compute(audio);
        float[] reference = mel.ComputeReference(audio);

        Assert.Equal(mel.FramesCap * 80, fast.Length);
        float maxDiff = 0;
        for (int i = 0; i < fast.Length; i++)
        {
            maxDiff = Math.Max(maxDiff, Math.Abs(fast[i] - reference[i]));
        }

        Assert.True(maxDiff < 1e-4f, $"max abs diff {maxDiff} exceeds tolerance");
    }

    [Fact]
    public void OutputIsPaddedToThirtySeconds()
    {
        string? filters = FindFilters();
        if (filters is null) return; // converted model not present in this checkout — nothing to compare

        var mel = new MelSpectrogram(filters!);
        float[] output = mel.Compute(Speech(16000)); // 1 s of audio

        Assert.Equal(3000, mel.FramesCap);
        Assert.Equal(3000 * 80, output.Length);

        // Frames beyond the audio must stay exactly zero (the encoder relies on the 30 s pad).
        for (int m = 0; m < 80; m++)
        {
            for (int f = 200; f < 3000; f += 97)
            {
                Assert.Equal(0f, output[m * 3000 + f]);
            }
        }
    }
}
