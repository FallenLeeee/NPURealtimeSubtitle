namespace RealtimeSubtitle.Core.Speech;

/// <summary>部分（进行中）识别结果——仅更新源文本预览。</summary>
public readonly record struct AsrPartial(string Text, DateTimeOffset Timestamp);

/// <summary>一个短语的最终识别结果——唯一进入翻译队列的内容。</summary>
public readonly record struct AsrFinal(string Text, DateTimeOffset Timestamp);

/// <summary>音频适配器将 16 kHz / 16 位 / 单声道 PCM 推送到识别器（ASR 协议格式）。</summary>
public interface IPcm16Sink
{
    /// <summary>消耗连续的 16 kHz、16 位、单声道 PCM 字节块（小端）。</summary>
    void WritePcm16(ReadOnlySpan<byte> pcmBytes, DateTimeOffset timestamp);
}