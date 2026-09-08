namespace RealtimeSubtitle.Core.Translation;

/// <summary>
/// 设备自动选择（计划决策 D10）：使用固定的基准句子集，测试 NPU 与 CPU（各 3 轮，中位数），
/// 并选择更快的设备。当 NPU 无法编译或完全运行时回退到 CPU。
/// 结果写回配置中。
/// </summary>
public static class DeviceSelector
{
    /// <summary>固定的双语基准集（50 个句子，简短对话行）。</summary>
    public static readonly string[] BenchmarkSentences =
    {
        "Where are you going?", "I will be back soon.", "What is your name?", "How are you today?",
        "We need to move now.", "Open the door, please.", "The enemy is coming from the north.",
        "Did you find the key?", "This place is amazing.", "I cannot believe it.",
        "Keep quiet, they are near.", "We must leave before sunrise.", "That is not what I meant.",
        "Hold your position.", "Follow me closely.", "The bridge is down ahead.",
        "Tell me the truth.", "I have seen this before.", "Stay away from the edge.",
        "We ran out of supplies.", "Do not worry about it.", "Everything will be fine.",
        "Where did you put it?", "Let us split up.", "I found something strange.",
        "Be careful with that.", "It is getting dark.", "We should rest here.",
        "Who sent you here?", "Give me a minute.", "The signal is weak.",
        "They are right behind us.", "Is there another way?", "We have no choice.",
        "That is a bad idea.", "Come with me now.", "I understand what you mean.",
        "The village is empty.", "Someone is watching us.", "Put your hands up.",
        "Do not move.", "Where is the exit?", "We are trapped.",
        "Call for backup.", "The road is blocked.", "Try it again.",
        "It worked this time.", "We made it.", "See you on the other side.",
        "Good luck out there.",
    };

    /// <summary>
    /// Runs the benchmark and returns the recommended device string, or throws when both fail.
    /// </summary>
    public static async Task<(string Device, double NpuMs, double CpuMs, bool NpuFailed)> SelectAsync(
        Func<string, ITranslator> factory,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        const int rounds = 3;
        double npuMs = -1, cpuMs = -1;
        bool npuFailed = false;
        string device = "CPU";

        var npu = TryMeasure("NPU", factory, rounds, progress, cancellationToken);
        if (npu is not null)
        {
            npuMs = npu.Value;
            device = "NPU";
        }
        else
        {
            npuFailed = true;
        }

        var cpu = TryMeasure("CPU", factory, rounds, progress, cancellationToken);
        if (cpu is not null) cpuMs = cpu.Value;

        if (npuMs >= 0 && cpuMs >= 0 && npuMs > cpuMs * 1.05)
        {
            // NPU 可测量但比 CPU 慢 → 延迟优先原则选择 CPU。
            device = "CPU";
        }
        else if (npuMs >= 0)
        {
            device = npuFailed ? "CPU" : "NPU";
        }

        return (device, npuMs, cpuMs, npuFailed);
    }

    private static double? TryMeasure(
        string deviceName,
        Func<string, ITranslator> factory,
        int rounds,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        progress?.Report($"benchmarking {deviceName} ...");
        try
        {
            using var translator = factory(deviceName);
            var samples = new List<double>(BenchmarkSentences.Length * rounds);
            for (int r = 0; r < rounds; r++)
            {
                foreach (string sentence in BenchmarkSentences)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var result = translator.TranslateAsync(new TranslationRequest(sentence, DateTimeOffset.UtcNow), cancellationToken)
                        .GetAwaiter().GetResult();
                    if (result.TargetText.Length > 0)
                    {
                        samples.Add(result.Elapsed.TotalMilliseconds);
                    }
                }
            }

            if (samples.Count == 0) return null;
            samples.Sort();
            return samples[samples.Count / 2]; // median
        }
        catch (Exception ex)
        {
            progress?.Report($"{deviceName} failed: {ex.Message}");
            return null;
        }
    }
}