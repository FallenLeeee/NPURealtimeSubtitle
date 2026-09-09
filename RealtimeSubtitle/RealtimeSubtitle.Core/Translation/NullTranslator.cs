namespace RealtimeSubtitle.Core.Translation;

/// <summary>
/// No-op translator used when Chinese recognition already produces the target language
/// (P6-25). Keeps the queue shape without compiling Marian.
/// </summary>
public sealed class NullTranslator : ITranslator
{
    public string Device => "none";

    public Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken = default) =>
        Task.FromResult(new TranslationResult(string.Empty, Device, TimeSpan.Zero, FallbackToCpu: false));

    public void Dispose()
    {
    }
}
