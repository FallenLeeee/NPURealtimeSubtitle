using System.Text.Json;

namespace RealtimeSubtitle.Core.Speech;

/// <summary>
/// 从 whisper_meta.json 构建的 Whisper BPE 解码器（由 tools/model-convert 转储）：
/// 片段、语言 token id、控制特殊标记以及 GPT2 字节映射（unicode → byte）。
/// 实现字节级解码（tiktoken 风格）和语言 token 辅助函数。
/// </summary>
public sealed class WhisperTokenDecoder
{
    public required string[] Pieces { get; init; }
    public required Dictionary<string, int> LangIds { get; init; }
    public required Dictionary<string, int> SpecialIds { get; init; } // sot/eos/transcribe/notimestamps
    public required Dictionary<string, byte> ByteMap { get; init; }   // unicode 字符 → 字节

    /// <summary>单语言模型（whisper.en）：提示中没有语言 token 和检测。</summary>
    public bool Mono { get; init; }

    public int EosId => SpecialIds.GetValueOrDefault("eos", 50257);
    public int SotId => SpecialIds.GetValueOrDefault("sot", 50258);
    public int TranscribeId => SpecialIds.GetValueOrDefault("transcribe", 50359);
    public int NoTimestampsId => SpecialIds.GetValueOrDefault("notimestamps", 50363);

    public static WhisperTokenDecoder Load(string metaPath)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(metaPath));
        var root = doc.RootElement;

        var byteMap = new Dictionary<string, byte>();
        foreach (JsonProperty prop in root.GetProperty("byte_map").EnumerateObject())
        {
            byteMap[prop.Name] = (byte)prop.Value.GetInt32();
        }

        var langIds = new Dictionary<string, int>();
        foreach (JsonProperty prop in root.GetProperty("lang_ids").EnumerateObject())
        {
            langIds[prop.Name] = prop.Value.GetInt32();
        }

        var specials = new Dictionary<string, int>();
        foreach (JsonProperty prop in root.GetProperty("special_ids").EnumerateObject())
        {
            specials[prop.Name] = prop.Value.GetInt32();
        }

        bool mono = root.TryGetProperty("mono", out JsonElement monoEl) && monoEl.GetBoolean();
        return new WhisperTokenDecoder
        {
            Pieces = root.GetProperty("pieces").EnumerateArray().Select(e => e.GetString() ?? string.Empty).ToArray(),
            LangIds = langIds,
            SpecialIds = specials,
            ByteMap = byteMap,
            Mono = mono,
        };
    }

    /// <summary>使用 GPT2 字节级规则解码 token id；控制 token 被跳过。</summary>
    public string Decode(IReadOnlyList<int> ids)
    {
        var bytes = new List<byte>(64);
        var parts = new List<string>(4);

        foreach (int id in ids)
        {
            if (id < 0 || id >= Pieces.Length) continue;
            string piece = Pieces[id];

            // 字节后备片段看起来像 "<0x41>"
            if (piece.Length == 6 && piece.StartsWith("<0x", StringComparison.Ordinal) && piece.EndsWith('>'))
            {
                bytes.Add(Convert.ToByte(piece.Substring(3, 2), 16));
                continue;
            }

            if (piece.StartsWith("<|", StringComparison.Ordinal) && piece.EndsWith("|>", StringComparison.Ordinal))
            {
                // 控制token（语言 / sot / eos 等）——不是转录的一部分
                continue;
            }

            // 字节级片段：映射标记字符和字节表，否则 ASCII，否则 UTF-8。
            foreach (char c in piece)
            {
                byte b;
                if (c == '\u0120') b = 0x20;        // 'Ġ' → 空格
                else if (c == '\u010A') b = 0x0A;   // 'Ċ' → 换行
                else if (c == '\u0109') b = 0x09;   // 'ĉ' → 制表符
                else if (ByteMap.TryGetValue(c.ToString(), out byte mapped)) b = mapped;
                else if (c < 128) b = (byte)c;
                else
                {
                    foreach (byte b2 in System.Text.Encoding.UTF8.GetBytes(c.ToString()))
                    {
                        bytes.Add(b2);
                    }

                    continue;
                }

                bytes.Add(b);
            }
        }

        string text = System.Text.Encoding.UTF8.GetString(bytes.ToArray());
        if (parts.Count == 0 && text.Length > 0)
        {
            // 保持简单：整个文本
        }

        return text.Trim();
    }

    /// <summary>返回语言代码（"en"、"zh"、"ja"）的语言 token id，或 -1。</summary>
    public int LanguageId(string lang)
    {
        if (LangIds.TryGetValue(lang, out int id)) return id;
        // 容忍 "en-US" 风格
        string two = lang.Length >= 2 ? lang[..2].ToLowerInvariant() : lang.ToLowerInvariant();
        return LangIds.GetValueOrDefault(two, -1);
    }
}