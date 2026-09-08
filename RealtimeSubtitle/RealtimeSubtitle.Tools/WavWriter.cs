using System.Text;

namespace RealtimeSubtitle.Tools;

/// <summary>Minimal PCM16 WAV writer with lazy RIFF header patching.</summary>
internal sealed class WavWriter : IDisposable
{
    private readonly BinaryWriter _bw;
    private readonly long _dataSizePos;
    private long _dataBytes;

    public WavWriter(string path, int sampleRate, short channels, short bitsPerSample)
    {
        _bw = new BinaryWriter(File.Create(path));
        _bw.Write(Encoding.ASCII.GetBytes("RIFF"));
        _bw.Write(0); // RIFF size placeholder
        _bw.Write(Encoding.ASCII.GetBytes("WAVE"));
        _bw.Write(Encoding.ASCII.GetBytes("fmt "));
        _bw.Write(16);
        _bw.Write((short)1); // PCM
        _bw.Write(channels);
        _bw.Write(sampleRate);
        _bw.Write(sampleRate * channels * bitsPerSample / 8);
        _bw.Write((short)(channels * bitsPerSample / 8));
        _bw.Write(bitsPerSample);
        _bw.Write(Encoding.ASCII.GetBytes("data"));
        _dataSizePos = _bw.BaseStream.Position;
        _bw.Write(0); // data size placeholder
    }

    public long DataBytes => _dataBytes;

    public void WritePcm16(ReadOnlySpan<short> samples)
    {
        foreach (short s in samples)
        {
            _bw.Write(s);
        }

        _dataBytes += samples.Length * sizeof(short);
    }

    public void FinalizeHeader()
    {
        _bw.BaseStream.Position = 4;
        _bw.Write((int)(36 + _dataBytes));
        _bw.BaseStream.Position = _dataSizePos;
        _bw.Write((int)_dataBytes);
        _bw.Flush();
    }

    public void Dispose()
    {
        FinalizeHeader();
        _bw.Dispose();
    }
}