using System.Text;
using System.Text.Json;

namespace RealtimeSubtitle.Core.Translation;

/// <summary>
/// 由 tools/model-convert 生成的分词器数据（在转换时从 HF marian 分词器构建）。
/// 片段按 id 顺序排列；id 是列表索引。
/// </summary>
public sealed class MarianTokenizerModel
{
    public required string[] SrcPieces { get; init; }
    public required string[] TgtPieces { get; init; }
    public int BosId { get; init; }
    public int EosId { get; init; }
    public int UnkId { get; init; }
    public int PadId { get; init; }
    public int DecoderStartId { get; init; } = 0;
    public bool Lowercase { get; init; } = true;

    public static MarianTokenizerModel Load(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;
        return new MarianTokenizerModel
        {
            SrcPieces = root.GetProperty("src_pieces").EnumerateArray().Select(e => e.GetString() ?? string.Empty).ToArray(),
            TgtPieces = root.GetProperty("tgt_pieces").EnumerateArray().Select(e => e.GetString() ?? string.Empty).ToArray(),
            BosId = root.TryGetProperty("bos_id", out var b) ? b.GetInt32() : 1,
            EosId = root.TryGetProperty("eos_id", out var e) ? e.GetInt32() : 0,
            UnkId = root.TryGetProperty("unk_id", out var u) ? u.GetInt32() : 1,
            PadId = root.TryGetProperty("pad_id", out var p) ? p.GetInt32() : 65000,
            DecoderStartId = root.TryGetProperty("decoder_start_id", out var d) ? d.GetInt32() : 0,
            Lowercase = !root.TryGetProperty("lowercase", out var l) || l.GetBoolean(),
        };
    }
}

/// <summary>
/// Marian 模型的最小 SentencePiece 兼容分词器（决策：避免完整的 SentencePiece 移植；
/// 使用贪婪最长匹配分段加上 ▁-空格规范化对片段表进行搜索）。
/// 对子词词汇效果良好，对字符级目标（例如中文）是精确的。
/// 编码/解码与转换器输出对称。
/// </summary>
public sealed class MarianTokenizer
{
    private readonly MarianTokenizerModel _model;
    private readonly string[] _piecesByLenDesc; // 贪婪搜索的中间阶段（源）

    public MarianTokenizer(MarianTokenizerModel model)
    {
        _model = model;
        _piecesByLenDesc = model.SrcPieces
            .OrderByDescending(p => p.Length)
            .ToArray();
    }

    public MarianTokenizerModel Model => _model;

    /// <summary>将源文本编码为片段 id（不添加 BOS/EOS；调用者填充）。</summary>
    public int[] Encode(string text)
    {
        string s = Normalize(text);
        var ids = new List<int>(32);

        while (s.Length > 0)
        {
            string? match = null;
            foreach (string piece in _piecesByLenDesc)
            {
                if (piece.Length == 0 || piece.Length > s.Length) continue;
                if (s.StartsWith(piece, StringComparison.Ordinal))
                {
                    match = piece;
                    break;
                }
            }

            if (match is null)
            {
                ids.Add(_model.UnkId);
                s = s.Length > 1 ? s[1..] : string.Empty;
                continue;
            }

            ids.Add(Array.IndexOf(_model.SrcPieces, match));
            s = s[match.Length..];
        }

        return ids.ToArray();
    }

    /// <summary>将目标片段 id 解码为显示文本（▁ → 空格，去除前导空格）。</summary>
    public string Decode(IReadOnlyList<int> ids)
    {
        var sb = new StringBuilder(64);
        foreach (int id in ids)
        {
            if (id < 0 || id >= _model.TgtPieces.Length) continue;
            if (id == _model.UnkId || id == _model.PadId) continue;
            string piece = _model.TgtPieces[id];
            if (piece.Length == 0) continue;
            sb.Append(piece.Replace('\u2581', ' ')); // ▁ → 空格
        }

        return sb.ToString().TrimStart(' ');
    }

    private string Normalize(string text)
    {
        string s = text.Normalize(NormalizationForm.FormC).Trim();
        if (_model.Lowercase) s = s.ToLowerInvariant();
        // SentencePiece 约定：空格变为 ▁ (U+2581)，每个单词边界一个。
        s = "\u2581" + s.Replace(' ', '\u2581');
        return s;
    }
}