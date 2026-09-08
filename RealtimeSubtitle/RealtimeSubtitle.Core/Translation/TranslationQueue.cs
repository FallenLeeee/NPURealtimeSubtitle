using System.Threading.Channels;

namespace RealtimeSubtitle.Core.Translation;

/// <summary>
/// 有界生产者/消费者翻译队列。实时策略（设计文档 §8）：
/// 优先最新句子 —— 当队列已满时，丢弃最旧的排队项目。
/// 单个工作线程串行消费，因此 NPU 推理从不争用。
/// </summary>
public sealed class TranslationQueue : IDisposable
{
    private readonly Channel<TranslationRequest> _channel;
    private readonly Task _worker;
    private readonly ITranslator _translator;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly int _capacity;

    public TranslationQueue(ITranslator translator, int capacity = 8)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _translator = translator;
        _capacity = capacity;

        _channel = Channel.CreateBounded<TranslationRequest>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });

        _worker = Task.Run(WorkerLoop);
    }

    /// <summary>每次完成翻译时引发（成功或空）。</summary>
    public event Action<TranslationRequest, TranslationResult>? Completed;

    /// <summary>每次因队列已满而丢弃项目时引发。</summary>
    public event Action<TranslationRequest>? Dropped;

    public int Capacity => _capacity;

    /// <summary>底层翻译器解析到的设备（例如 "NPU" 或 "CPU"）。</summary>
    public string TranslatorDevice => _translator.Device;

    public bool TryEnqueue(TranslationRequest request)
    {
        bool ok = _channel.Writer.TryWrite(request);
        if (!ok) Dropped?.Invoke(request);
        return ok;
    }

    private async Task WorkerLoop()
    {
        await foreach (TranslationRequest request in _channel.Reader.ReadAllAsync(_lifetime.Token))
        {
            TranslationResult result;
            try
            {
                result = await _translator.TranslateAsync(request, _lifetime.Token);
            }
            catch (Exception)
            {
                result = new TranslationResult(string.Empty, _translator.Device, TimeSpan.Zero, FallbackToCpu: false);
            }

            Completed?.Invoke(request, result);
        }
    }

    /// <summary>停止接受新工作并排空进行中的项目。</summary>
    public void Dispose()
    {
        _channel.Writer.TryComplete();
        _lifetime.Cancel();
        try { _worker.GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
        _lifetime.Dispose();
    }
}