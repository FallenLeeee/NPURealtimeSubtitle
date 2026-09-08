namespace RealtimeSubtitle.Core.Audio;

/// <summary>
/// 值类型项目的线程安全固定容量环形缓冲区。
/// 设计为单生产者（捕获线程）和单消费者（管道任务）。
/// </summary>
public sealed class RingBuffer<T>
{
    private readonly T[] _buffer;
    private readonly object _sync = new();
    private int _head;
    private int _count;

    public RingBuffer(int capacity)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _buffer = new T[capacity];
    }

    public int Capacity => _buffer.Length;

    public int Count
    {
        get { lock (_sync) return _count; }
    }

    public int FreeSpace
    {
        get { lock (_sync) return _buffer.Length - _count; }
    }

    /// <summary>追加 <paramref name="items"/>，如果空间不足则丢弃整个批次。</summary>
    /// <returns>实际写入的项目数量（如果没有足够空间则返回 0）。</returns>
    public int TryWrite(ReadOnlySpan<T> items)
    {
        lock (_sync)
        {
            int count = items.Length;
            if (count > _buffer.Length - _count)
            {
                return 0; // 丢弃最新批次，以保持环形缓冲区不会阻塞音频引擎。
            }

            int tail = (_head + _count) % _buffer.Length;
            for (int i = 0; i < count; i++)
            {
                _buffer[(tail + i) % _buffer.Length] = items[i];
            }

            _count += count;
            return count;
        }
    }

    /// <summary>读取最多 <paramref name="destination"/>.Length 个项目到 span 中（连续）。</summary>
    /// <returns>读取的项目数量。</returns>
    public int TryRead(Span<T> destination)
    {
        lock (_sync)
        {
            int read = Math.Min(destination.Length, _count);
            for (int i = 0; i < read; i++)
            {
                destination[i] = _buffer[(_head + i) % _buffer.Length];
            }

            _head = (_head + read) % _buffer.Length;
            _count -= read;
            return read;
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            _head = 0;
            _count = 0;
            Array.Clear(_buffer, 0, _buffer.Length);
        }
    }
}