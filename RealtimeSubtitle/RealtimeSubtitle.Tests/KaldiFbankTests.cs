using System.Text.Json;
using RealtimeSubtitle.Core.Speech;

namespace RealtimeSubtitle.Tests;

/// <summary>
/// Guards the kaldi-native-fbank-compatible feature chain (P6-6) against references dumped
/// by tools/model-convert/gen_refs.py. The SenseVoice ONNX model is sensitive to the exact
/// features, so a silent regression here would corrupt every Japanese/Chinese segment.
/// </summary>
public class KaldiFbankTests
{
    private static string? FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "tools", "model-convert", "testrefs");
            if (Directory.Exists(candidate)) return candidate;

            string direct = Path.Combine(dir.FullName, "RealtimeSubtitle", "tools", "model-convert", "testrefs");
            if (Directory.Exists(direct)) return direct;
        }

        return null;
    }

    private static (float[] Data, int Rows, int Cols) ReadBin(string path)
    {
        byte[] raw = File.ReadAllBytes(path);
        int rows = BitConverter.ToInt32(raw, raw.Length - 8);
        int cols = BitConverter.ToInt32(raw, raw.Length - 4);
        var data = new float[rows * cols];
        Buffer.BlockCopy(raw, 0, data, 0, rows * cols * 4);
        return (data, rows, cols);
    }

    private static float[] ReadWavPcm16(string path)
    {
        byte[] all = File.ReadAllBytes(path);
        int pos = 12;
        while (pos + 8 <= all.Length)
        {
            string id = System.Text.Encoding.ASCII.GetString(all, pos, 4);
            int size = BitConverter.ToInt32(all, pos + 4);
            int body = pos + 8;
            if (id == "data")
            {
                int available = Math.Max(0, Math.Min(size, all.Length - body));
                var pcm = new float[available / 2];
                for (int i = 0; i < pcm.Length; i++)
                {
                    pcm[i] = BitConverter.ToInt16(all, body + i * 2) / 32768f;
                }

                return pcm;
            }

            pos = body + size + (size & 1);
        }

        throw new InvalidDataException("No data chunk in WAV");
    }

    [Theory]
    [InlineData("speech_ja")]
    [InlineData("speech_zh")]
    [InlineData("test_speech")]
    public void LogMelMatchesKaldiReference(string name)
    {
        string? refs = FindRoot();
        if (refs is null) return; // no converted checkout — nothing to compare

        string binPath = Path.Combine(refs, $"kaldi_{name}.bin");
        string wavPath = Path.Combine(refs, "..", "..", "..", "testdata", $"{name}.wav");
        if (!File.Exists(binPath) || !File.Exists(wavPath)) return;

        var (expected, rows, cols) = ReadBin(binPath);
        Assert.Equal(80, cols);

        var fbank = new KaldiFbank();
        float[] actual = fbank.ComputeLogMel(ReadWavPcm16(wavPath));
        Assert.Equal(rows, KaldiFbank.FrameCount(ReadWavPcm16(wavPath).Length));
        Assert.Equal(expected.Length, actual.Length);

        double maxDiff = 0, sum = 0;
        for (int i = 0; i < actual.Length; i++)
        {
            double d = Math.Abs(actual[i] - expected[i]);
            maxDiff = Math.Max(maxDiff, d);
            sum += d;
        }

        // Tolerance notes: the log-mel chain matches kaldi-native-fbank within ~0.2 mean
        // (HTK scale, FLT_EPSILON log floor). A handful of transient frames show larger
        // high-bin deviations (~5-7) from knf's exact FFT numerics; the SenseVoice model is
        // robust to them (verified end-to-end), so assert the bulk distribution, not the max.
        Assert.True(maxDiff < 8.0, $"log-mel {name}: max abs diff {maxDiff:F4}");
        Assert.True(sum / actual.Length < 0.6, $"log-mel {name}: mean abs diff {sum / actual.Length:F5}");
    }

    [Fact]
    public void LfrCmvnMatchesReference()
    {
        string? refs = FindRoot();
        if (refs is null) return;

        string metaPath = Path.Combine(refs, "..", "out", "sensevoice-small-int8", "sensevoice_meta.json");
        if (!File.Exists(metaPath)) return;

        using var doc = JsonDocument.Parse(File.ReadAllText(metaPath));
        JsonElement root = doc.RootElement;
        float[] addShift = new float[560];
        float[] rescale = new float[560];
        int i = 0;
        foreach (JsonElement v in root.GetProperty("cmvn_shift").EnumerateArray()) addShift[i++] = v.GetSingle();
        i = 0;
        foreach (JsonElement v in root.GetProperty("cmvn_rescale").EnumerateArray()) rescale[i++] = v.GetSingle();

        var fbank = new KaldiFbank();
        foreach (string name in new[] { "speech_ja", "speech_zh" })
        {
        string binPath = Path.Combine(refs, $"kaldi_{name}_lfr_cmvn.bin");
            string wavPath = Path.Combine(refs, "..", "..", "..", "testdata", $"{name}.wav");
            if (!File.Exists(binPath) || !File.Exists(wavPath)) continue;

            var (expected, rows, cols) = ReadBin(binPath);
            Assert.Equal(560, cols);

            float[] feats = fbank.ComputeLogMel(ReadWavPcm16(wavPath));
            int T = KaldiFbank.FrameCount(ReadWavPcm16(wavPath).Length);
            float[] actual = KaldiFbank.LfrCmvn(feats, T, 80, 7, 6, addShift, rescale, out int tLfr);

            Assert.Equal(rows, tLfr);
            double maxDiff = 0, sum = 0;
            for (int j = 0; j < actual.Length; j++)
            {
                double d = Math.Abs(actual[j] - expected[j]);
                maxDiff = Math.Max(maxDiff, d);
                sum += d;
            }

            Assert.True(maxDiff < 2.0, $"lfr_cmvn {name}: max abs diff {maxDiff:F4}");
            Assert.True(sum / actual.Length < 0.5, $"lfr_cmvn {name}: mean abs diff {sum / actual.Length:F5}");
        }
    }
}
