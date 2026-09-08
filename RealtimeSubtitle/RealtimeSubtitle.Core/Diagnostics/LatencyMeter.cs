namespace RealtimeSubtitle.Core.Diagnostics;

public readonly record struct LatencySummary(int Count, TimeSpan Min, TimeSpan P50, TimeSpan P95, TimeSpan Max);

/// <summary>
/// 滑动窗口延迟记录器，每个命名指标具有 p50/p95 汇总。
/// 用于 v3.0 计划（§10）中定义的管道预算测量。
/// </summary>
public sealed class LatencyMeter
{
    private sealed class Metric
    {
        private readonly List<(DateTimeOffset Ts, double Ms)> _samples = new();
        private readonly TimeSpan _window;

        public Metric(TimeSpan window) => _window = window;

        public void Add(DateTimeOffset now, double ms)
        {
            Prune(now);
            _samples.Add((now, ms));
        }

        public LatencySummary Get(DateTimeOffset now)
        {
            Prune(now);
            if (_samples.Count == 0) return new LatencySummary(0, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero);

            double[] values = _samples.Select(s => s.Ms).OrderBy(v => v).ToArray();
            return new LatencySummary(
                values.Length,
                TimeSpan.FromMilliseconds(values[0]),
                TimeSpan.FromMilliseconds(Percentile(values, 0.50)),
                TimeSpan.FromMilliseconds(Percentile(values, 0.95)),
                TimeSpan.FromMilliseconds(values[^1]));
        }

        private void Prune(DateTimeOffset now)
        {
            int drop = 0;
            while (drop < _samples.Count && now - _samples[drop].Ts > _window) drop++;
            if (drop > 0) _samples.RemoveRange(0, drop);
        }

        private static double Percentile(double[] sorted, double p)
        {
            double rank = (sorted.Length - 1) * p;
            int lo = (int)Math.Floor(rank);
            int hi = (int)Math.Ceiling(rank);
            if (lo == hi) return sorted[lo];
            double frac = rank - lo;
            return sorted[lo] * (1 - frac) + sorted[hi] * frac;
        }
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, Metric> _metrics = new();
    private TimeSpan _window;

    public LatencyMeter(TimeSpan? window = null)
    {
        _window = window ?? TimeSpan.FromSeconds(10);
    }

    public void Record(string metric, TimeSpan latency) => RecordAt(metric, latency, DateTimeOffset.UtcNow);

    public void RecordAt(string metric, TimeSpan latency, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (!_metrics.TryGetValue(metric, out var m)) _metrics[metric] = m = new Metric(_window);
            m.Add(now, latency.TotalMilliseconds);
        }
    }

    public LatencySummary GetSummary(string metric)
    {
        lock (_gate)
        {
            return _metrics.TryGetValue(metric, out var m) ? m.Get(DateTimeOffset.UtcNow) : default;
        }
    }

    public IEnumerable<string> MetricNames
    {
        get { lock (_gate) return _metrics.Keys.ToArray(); }
    }
}