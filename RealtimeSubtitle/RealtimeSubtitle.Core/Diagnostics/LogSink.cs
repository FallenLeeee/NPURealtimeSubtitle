namespace RealtimeSubtitle.Core.Diagnostics;

public enum LogLevel
{
    Debug,
    Info,
    Warn,
    Error,
}

/// <summary>
/// 线程安全的内存环形缓冲区 + 可选的滚动文件日志。
/// UI/客户端订阅 <see cref="LineWritten"/> 而不是轮询。
/// </summary>
public sealed class LogSink
{
    private static readonly Lazy<LogSink> LazyDefault = new(() => new LogSink());
    public static LogSink Default => LazyDefault.Value;

    private readonly object _gate = new();
    private readonly List<string> _ring = new(512);
    private StreamWriter? _file;
    private long _lineCount;

    public LogSink()
    {
        MinLevel = LogLevel.Info;
    }

    public LogLevel MinLevel { get; set; }

    /// <summary>滚动日志文件的绝对路径；设置为 null 以禁用文件输出。</summary>
    public string? LogFile
    {
        get { lock (_gate) return _file?.BaseStream is FileStream fs ? fs.Name : null; }
        set
        {
            lock (_gate)
            {
                _file?.Dispose();
                _file = null;
                if (!string.IsNullOrEmpty(value))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(value)!);
                    _file = new StreamWriter(value, append: true) { AutoFlush = true };
                }
            }
        }
    }

    public event Action<string>? LineWritten;

    public void Log(LogLevel level, string message, params object?[] args)
    {
        if (level < MinLevel) return;

        string line = $"{DateTimeOffset.Now:O} [{level,-5}] {string.Format(message, args)}";
        lock (_gate)
        {
            _ring.Add(line);
            if (_ring.Count > 512) _ring.RemoveAt(0);
            _file?.WriteLine(line);
            _lineCount++;
        }

        LineWritten?.Invoke(line);
    }

    public void Debug(string message, params object?[] args) => Log(LogLevel.Debug, message, args);
    public void Info(string message, params object?[] args) => Log(LogLevel.Info, message, args);
    public void Warn(string message, params object?[] args) => Log(LogLevel.Warn, message, args);
    public void Error(string message, params object?[] args) => Log(LogLevel.Error, message, args);

    /// <summary>Snapshot of the most recent <paramref name="count"/> lines (oldest first).</summary>
    public IReadOnlyList<string> Recent(int count)
    {
        lock (_gate)
        {
            int take = Math.Min(count, _ring.Count);
            return _ring.GetRange(_ring.Count - take, take);
        }
    }

    public void Dispose()
    {
        lock (_gate) _file?.Dispose();
        _file = null;
    }
}