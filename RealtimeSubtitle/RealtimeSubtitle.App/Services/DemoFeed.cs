using RealtimeSubtitle.Core.Speech;

namespace RealtimeSubtitle.App.Services;

/// <summary>
/// Transcript-driven ASR stand-in used for the Phase 4 end-to-end UI loop until the real
/// Whisper backend lands (decisions.md U4a/P3-5). Reads one sentence per line and emits
/// growing partials followed by a final, mirroring the recognizer seam contract.
/// </summary>
public sealed class DemoFeed : ISpeechRecognizer
{
    private readonly string[] _lines;
    private CancellationTokenSource? _cts;

    public DemoFeed(string transcriptPath)
    {
        _lines = File.ReadAllLines(transcriptPath)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToArray();
    }

    public event Action<AsrPartial>? Partial;
    public event Action<AsrFinal>? Final;
    public event Action<Exception>? Error;

    public void WritePcm16(ReadOnlySpan<byte> pcmBytes, DateTimeOffset timestamp)
    {
        // demo feed ignores raw audio
    }

    public void Start(CancellationToken token)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        _ = RunAsync(_cts.Token);
    }

    private async Task RunAsync(CancellationToken token)
    {
        try
        {
            var rnd = new Random(42);
            foreach (string line in _lines)
            {
                // growing partial preview
                for (int i = 1; i <= line.Length && !token.IsCancellationRequested; i += 2)
                {
                    Partial?.Invoke(new AsrPartial(line[..Math.Min(i, line.Length)], DateTimeOffset.Now));
                    await Task.Delay(60, token);
                }

                Final?.Invoke(new AsrFinal(line, DateTimeOffset.Now));
                await Task.Delay(1200 + rnd.Next(800), token);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
    }

    public void Dispose()
    {
        Stop();
        _cts?.Dispose();
    }
}