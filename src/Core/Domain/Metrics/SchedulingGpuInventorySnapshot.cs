namespace ResourceManager.App.Domain.Metrics;

public enum SamplingObservationStatus : byte
{
    Invalid = 0,
    Current = 1,
    RetainedLastGood = 2,
    Unavailable = 3,
    NotRequested = 4,
    Unsupported = 5,
    Partial = 6,
    Warming = 7
}

[Flags]
public enum SchedulingGpuCapabilityMask : ulong
{
    None = 0,
    Usage = 1UL << 0,
    DedicatedMemory = 1UL << 1
}

[Flags]
public enum SchedulingGpuMetricMask : ulong
{
    None = 0,
    Usage = 1UL << 0,
    UsedDedicatedMemory = 1UL << 1,
    TotalDedicatedMemory = 1UL << 2
}

public sealed record SchedulingGpuAdapterObservation(
    int Index,
    ulong AdapterKey,
    SchedulingGpuCapabilityMask CapabilityMask,
    SchedulingGpuMetricMask ValidMetricMask,
    SamplingObservationStatus UsageStatus,
    SamplingObservationStatus CapacityStatus,
    double UsagePercent,
    ulong UsedDedicatedMemoryBytes,
    ulong TotalDedicatedMemoryBytes,
    ulong Generation,
    long ObservedAtUtcTicks);

public sealed record SchedulingGpuInventorySnapshot(
    SamplingObservationStatus Status,
    ulong Generation,
    long ObservedAtUtcTicks,
    uint ObservedCount,
    uint SkippedCount,
    uint OverflowCount,
    ulong TopologyFingerprint,
    IReadOnlyList<SchedulingGpuAdapterObservation> Adapters)
{
    public bool IsCurrentComplete()
    {
        if (Status != SamplingObservationStatus.Current
            || Generation == 0
            || ObservedAtUtcTicks <= 0
            || SkippedCount != 0
            || OverflowCount != 0
            || ObservedCount != Adapters.Count)
        {
            return false;
        }

        var indexes = new HashSet<int>();
        var keys = new HashSet<ulong>();
        foreach (var adapter in Adapters)
        {
            if (adapter.Index < 0
                || adapter.AdapterKey == 0
                || adapter.Generation != Generation
                || adapter.ObservedAtUtcTicks != ObservedAtUtcTicks
                || !indexes.Add(adapter.Index)
                || !keys.Add(adapter.AdapterKey)
                || !IsAdapterObservationValid(adapter))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsAdapterObservationValid(SchedulingGpuAdapterObservation adapter)
    {
        var usageCurrent = adapter.UsageStatus == SamplingObservationStatus.Current;
        var usageValid = adapter.ValidMetricMask.HasFlag(SchedulingGpuMetricMask.Usage);
        if (usageCurrent != usageValid
            || usageValid && (!double.IsFinite(adapter.UsagePercent)
                || adapter.UsagePercent < 0
                || adapter.UsagePercent > 100))
        {
            return false;
        }

        var capacityCurrent = adapter.CapacityStatus == SamplingObservationStatus.Current;
        var requiredCapacityMask = SchedulingGpuMetricMask.UsedDedicatedMemory
            | SchedulingGpuMetricMask.TotalDedicatedMemory;
        var capacityValid = (adapter.ValidMetricMask & requiredCapacityMask) == requiredCapacityMask;
        if (capacityCurrent != capacityValid
            || capacityValid && adapter.UsedDedicatedMemoryBytes > adapter.TotalDedicatedMemoryBytes)
        {
            return false;
        }

        if (capacityCurrent
            && !adapter.CapabilityMask.HasFlag(SchedulingGpuCapabilityMask.DedicatedMemory))
        {
            return false;
        }

        if (adapter.CapacityStatus == SamplingObservationStatus.Unsupported
            && adapter.CapabilityMask.HasFlag(SchedulingGpuCapabilityMask.DedicatedMemory))
        {
            return false;
        }

        return true;
    }
}
