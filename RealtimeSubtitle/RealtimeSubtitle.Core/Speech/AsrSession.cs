namespace RealtimeSubtitle.Core.Speech;

public enum AsrSessionState
{
    Idle,
    Listening,
    Recognizing,
    Completed,
    Failed,
}

/// <summary>
/// 认识器无关的会话状态（设计文档 §6）：跟踪当前短语、部分文本、最终历史和会话状态，
/// 因此无论后端如何，UI 和翻译源都看起来一样。
/// 实例由调用 <see cref="OnPartial"/> / <see cref="OnFinal"/> / <see cref="OnError"/> / <see cref="Reset"/> 驱动。
/// </summary>
public sealed class AsrSession
{
    private const int MaxHistory = 50;
    private readonly List<AsrFinal> _history = new();

    public AsrSessionState State { get; private set; } = AsrSessionState.Idle;

    /// <summary>当前短语的最新部分文本（空闲/监听时为空）。</summary>
    public string CurrentText { get; private set; } = string.Empty;

    public IReadOnlyList<AsrFinal> History => _history;

    public event Action<AsrSessionState>? StateChanged;

    public void BeginListening()
    {
        if (State is AsrSessionState.Listening or AsrSessionState.Recognizing) return;
        State = AsrSessionState.Listening;
        CurrentText = string.Empty;
        StateChanged?.Invoke(State);
    }

    public void StopListening()
    {
        State = AsrSessionState.Idle;
        CurrentText = string.Empty;
        StateChanged?.Invoke(State);
    }

    public void OnPartial(AsrPartial partial)
    {
        if (State is not (AsrSessionState.Listening or AsrSessionState.Recognizing or AsrSessionState.Completed))
        {
            State = AsrSessionState.Listening;
            StateChanged?.Invoke(State);
        }

        if (State is not AsrSessionState.Recognizing)
        {
            State = AsrSessionState.Recognizing;
            StateChanged?.Invoke(State);
        }

        CurrentText = partial.Text;
    }

    public void OnFinal(AsrFinal final)
    {
        _history.Add(final);
        if (_history.Count > MaxHistory) _history.RemoveAt(0);

        CurrentText = string.Empty;
        State = AsrSessionState.Completed;
        StateChanged?.Invoke(State);

        // 完成的短语意味着会话已准备好进行下一个。
        State = AsrSessionState.Listening;
        StateChanged?.Invoke(State);
    }

    public void OnError(Exception error)
    {
        State = AsrSessionState.Failed;
        StateChanged?.Invoke(State);
    }

    public void Reset()
    {
        _history.Clear();
        CurrentText = string.Empty;
        State = AsrSessionState.Idle;
        StateChanged?.Invoke(State);
    }
}