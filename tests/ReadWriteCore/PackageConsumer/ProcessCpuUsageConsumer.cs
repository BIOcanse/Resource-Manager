using ResourceManager.App.Infrastructure.ResourceBreakdown;
using ResourceManager.ReadWrite.ProcessFields;

internal static class ProcessCpuUsageConsumer
{
    internal static void Run()
    {
        var definition = ProcessMetricFieldRegistry.Find("process_all_cpu_usage_actual")
            ?? throw new InvalidOperationException("Independent canonical CPU usage definition missing.");
        Require(definition.Code == ProcessMetricFieldCode.CpuUsage && definition.Unit == "%"
            && definition.ValueKind == "float64" && definition.SourceId == "windows.process.cpu-interval"
            && definition.ReadMask == ProcessMetricReadMask.CpuTime, "explicit CPU interval binding");
        Require(typeof(ProcessCpuDeltaTracker).Assembly == typeof(ProcessMetricFieldRegistry).Assembly
            && typeof(ProcessInstanceKey).Assembly == typeof(ProcessMetricFieldRegistry).Assembly,
            "tracker and creation identity owned by independent read/write assembly");
        var tracker = new ProcessCpuDeltaTracker(); var at = DateTimeOffset.UnixEpoch;
        var original = new ProcessInstanceKey(42, 100); var reused = new ProcessInstanceKey(42, 200);
        var first = tracker.UpdateInterval(true, at, Batch(original, 10), 4);
        Require(first.Percentages.Count == 0 && first.Interval is null, "first sample has no inferred percent");
        var second = tracker.UpdateInterval(true, at.AddMilliseconds(2500), Batch(original, 15), 4);
        Require(second.CapturedAt == at.AddMilliseconds(2500) && second.Interval == TimeSpan.FromMilliseconds(2500)
            && second.Percentages[original] == 50, "recorded interval and explicit four-processor denominator");
        Require(ProcessMetricFieldProjection.CreateCpuUsageValue(definition, second.Percentages[original]) is
            { NumericValue: 50, IntegerValue: null }, "independent canonical projection");
        var zero = tracker.UpdateInterval(true, at.AddSeconds(5), Batch(original, 15), 4);
        Require(zero.Percentages[original] == 0
            && ProcessMetricFieldProjection.CreateCpuUsageValue(definition, zero.Percentages[original]).NumericValue == 0,
            "valid zero stays zero");
        Require(tracker.Update(true, at.AddSeconds(6), Batch(reused, 1), 4).Count == 0,
            "reused PID cannot consume old creation baseline");
        Require(tracker.Update(true, at.AddSeconds(7), Batch(reused, 3), 4)[reused] == 50,
            "reused process second sample");
        var backwards = tracker.UpdateInterval(true, at.AddSeconds(6), Batch(reused, 4), 4);
        Require(backwards.Interval is null && backwards.Percentages.Count == 0, "backward time has no fake one-ms interval");
        Require(tracker.Update(true, at.AddSeconds(8), Batch(reused, 8), 4)[reused] == 50,
            "backward-time capture rebaselines");
        Require(tracker.Update(true, at.AddSeconds(9), Batch(reused, 1), 4).Count == 0,
            "counter reset has no inferred zero");
        Require(tracker.Update(true, at.AddSeconds(10), Batch(reused, 3), 4)[reused] == 50,
            "counter reset rebaselines");
        Require(tracker.Update(false, at.AddSeconds(11), Batch(reused, 0), 4).Count == 0,
            "non-CPU call does not advance baseline");
        Require(tracker.Update(true, at.AddSeconds(12), Batch(reused, 7), 4)[reused] == 50,
            "same original baseline across non-CPU capture");
        Require(ProcessMetricFieldProjection.CreateCpuUsageValue(definition, null).NumericValue is null,
            "missing interval projects typed empty");
        var twoProcessors = new ProcessCpuDeltaTracker();
        twoProcessors.Update(true, at, Batch(original, 10), 2);
        Require(twoProcessors.Update(true, at.AddSeconds(2), Batch(original, 12), 2)[original] == 50,
            "caller-supplied processor denominator is used without topology substitution");
        Console.WriteLine("Process CPU usage package consumer passed: public tracker and exact creation identity, recorded intervals, zero, reset and explicit processor denominator; no hardware access.");
    }

    private static Dictionary<ProcessInstanceKey, TimeSpan> Batch(ProcessInstanceKey key, double seconds)
        => new() { [key] = TimeSpan.FromSeconds(seconds) };

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
