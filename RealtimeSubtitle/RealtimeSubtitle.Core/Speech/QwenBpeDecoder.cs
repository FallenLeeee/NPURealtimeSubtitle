using System.Text;
using System.Text.Json;

namespace RealtimeSubtitle.Core.Speech;

/// <summary>
/// Qwen byte-level BPE decoder built from the tokenizer files shipped with the Qwen3-ASR
/// OpenVINO bundle (vocab.json). Qwen uses the GPT2 byte-encoding convention: non-ASCII
/// bytes are represented as Unicode chars via the standard bytes-to-unicode table
/// (e.g. 'Ġ' → 0x20, 'ł' → 0xBD), and ids ≥ 151643 are added special tokens to skip.
/// </summary>
public sealed class QwenBpeDecoder
{
    private const int FirstSpecialId = 151643;

    private readonly string[] _pieces;      // id → piece string
    private readonly Dictionary<char, byte> _byteTable;

    private QwenBpeDecoder(string[] pieces, Dictionary<char, byte> byteTable)
    {
        _pieces = pieces;
        _byteTable = byteTable;
    }

    public static QwenBpeDecoder Load(string vocabJsonPath)
    {
        var vocab = JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(vocabJsonPath))
                    ?? throw new InvalidDataException("vocab.json is empty");
        int maxId = vocab.Values.Count > 0 ? vocab.Values.Max() + 1 : 0;
        var pieces = new string[maxId];
        foreach ((string piece, int id) in vocab)
        {
            if (id >= 0 && id < maxId) pieces[id] = piece;
        }

        return new QwenBpeDecoder(pieces, BuildGpt2ByteTable());
    }

    /// <summary>Decodes token ids to text (special tokens are skipped).</summary>
    public string Decode(IReadOnlyList<int> ids)
    {
        var bytes = new List<byte>(128);
        foreach (int id in ids)
        {
            if (id < 0 || id >= _pieces.Length || id >= FirstSpecialId) continue;
            string piece = _pieces[id];
            if (string.IsNullOrEmpty(piece)) continue;

            foreach (char c in piece)
            {
                if (_byteTable.TryGetValue(c, out byte b)) bytes.Add(b);
                else if (c < 128) bytes.Add((byte)c);
            }
        }

        return Encoding.UTF8.GetString(bytes.ToArray()).Trim();
    }

    /// <summary>GPT2 bytes-to-unicode table (tiktoken convention, used by Qwen/whisper).</summary>
    private static Dictionary<char, byte> BuildGpt2ByteTable()
    {
        var selfBytes = new HashSet<int>();
        for (int b = 0x21; b <= 0x7E; b++) selfBytes.Add(b);
        for (int b = 0xA1; b <= 0xAC; b++) selfBytes.Add(b);
        for (int b = 0xAE; b <= 0xFF; b++) selfBytes.Add(b);

        var table = new Dictionary<char, byte>();
        int n = 0;
        for (int b = 0; b < 256; b++)
        {
            if (selfBytes.Contains(b))
            {
                table[(char)b] = (byte)b;
            }
            else
            {
                table[(char)(256 + n)] = (byte)b;
                n++;
            }
        }

        return table;
    }
}
