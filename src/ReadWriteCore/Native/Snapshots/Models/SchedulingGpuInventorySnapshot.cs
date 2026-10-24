namespace ResourceManager.App.Domain.Metrics;

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

        for (var index = 0; index < Adapters.Count; index++)
        {
            var adapter = Adapters[index];
            if (adapter.Index < 0
                || adapter.AdapterKey == 0
                || adapter.Generation != Generation
                || adapter.ObservedAtUtcTicks != ObservedAtUtcTicks
                || !IsAdapterObservationValid(adapter))
            {
                return false;
            }
            // The actual inventory is bounded by the published GPU profile
            // (16 adapters by default). Checking its short prefix needs no sets.
            for (var prior = 0; prior < index; prior++)
                if (Adapters[prior].Index == adapter.Index || Adapters[prior].AdapterKey == adapter.AdapterKey)
                    return false;
        }

        return true;
    }

    private static bool IsAdapterObservationValid(SchedulingGpuAdapterObservation adapter)
    {
        var usageCurrent = adapter.UsageStatus == SamplingObservationStatus.Current;
        var usageValid = (adapter.ValidMetricMask & SchedulingGpuMetricMask.Usage) != 0;
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
            && (adapter.CapabilityMask & SchedulingGpuCapabilityMask.DedicatedMemory) == 0)
        {
            return false;
        }

        if (adapter.CapacityStatus == SamplingObservationStatus.Unsupported
            && (adapter.CapabilityMask & SchedulingGpuCapabilityMask.DedicatedMemory) != 0)
        {
            return false;
        }

        return true;
    }
}
