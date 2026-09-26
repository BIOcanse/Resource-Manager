using System.Globalization;
using ResourceManager.App.Application.CpuTopology;
using ResourceManager.App.Domain.CpuTopology;
using ResourceManager.App.Domain.ResourceBreakdown;

namespace Resource_Manager_APP.Tests;

internal static class CpuCoreResidencyTestValues
{
    // These legacy scenarios explicitly have one physical core. This is not a multi-core inference.
    internal static CpuCoreResidencySnapshot? OneCore(SchedulingProcessFactSnapshot source)
    {
        if (!source.IsCpuCurrentComplete()
            || !source.TryGetCurrentDataset(SchedulingProcessMetricMask.CpuUsage, out var observation)) return null;
        return Create(checked((long)observation.SourceGeneration),
            new DateTimeOffset(observation.ObservedAtUtcTicks, TimeSpan.Zero),
            source.Processes.Where(process => process.ValidMetricMask.HasFlag(SchedulingProcessMetricMask.CpuUsage))
                .Select(process => Process(process.ProcessId, process.ProcessStartKey, ("core:0", process.CpuUsagePercent))).ToArray());
    }

    internal static CpuCoreResidencySnapshot Create(long sessionGeneration, DateTimeOffset at,
        params CpuProcessCoreResidency[] processes)
        => new(at, TimeSpan.FromSeconds(5), sessionGeneration, at.AddSeconds(-5), at, processes);

    internal static CpuProcessCoreResidency Process(int pid, ulong startKey, params (string CoreId, double Use)[] uses)
        => new($"{pid}/{startKey}", pid, startKey.ToString(CultureInfo.InvariantCulture), $"test-{pid}",
            0, 0, 0, null, null, null, [],
            uses.Select(core => new CpuPhysicalCoreResidency(core.CoreId, "ccd:0", 0, 0, 0, core.Use)).ToArray(), [], []);
}

internal sealed class RecordingCpuCoreResidencyReader(Func<CpuCoreResidencySnapshot?> read) : ICpuCoreResidencyReader
{
    internal int ReadCount { get; private set; }
    internal List<(string Id, TimeSpan Interval)> Subscriptions { get; } = [];
    internal int ReleasedSubscriptions { get; private set; }

    public CpuCoreResidencySnapshot? Read()
    {
        ReadCount++;
        return read();
    }

    public IDisposable AcquireSubscription(string subscriptionId, TimeSpan interval)
    {
        Subscriptions.Add((subscriptionId, interval));
        return new Subscription(this);
    }

    public IAsyncEnumerable<CpuCoreResidencySnapshot?> SubscribeAsync(string subscriptionId, TimeSpan interval, CancellationToken cancellationToken)
        => throw new NotSupportedException("The coordinator only reads the current value and owns a persistent subscription.");

    private sealed class Subscription(RecordingCpuCoreResidencyReader owner) : IDisposable
    {
        public void Dispose() => owner.ReleasedSubscriptions++;
    }
}
