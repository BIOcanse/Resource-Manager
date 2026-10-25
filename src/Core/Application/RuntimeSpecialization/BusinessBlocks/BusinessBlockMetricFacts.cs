using System.Collections.ObjectModel;

namespace ResourceManager.App.Application.RuntimeSpecialization.BusinessBlocks;

public enum BusinessBlockAttributionCoverage
{
    None,
    SynchronousSegments,
    DedicatedThreads,
    DedicatedThreadsAndSynchronousSegments
}

public sealed class BusinessBlockMetricFacts
{
    public BusinessBlockMetricFacts(
        DateTimeOffset intervalStartedAt,
        DateTimeOffset sampledAt,
        TimeSpan totalCpuTime,
        long totalAllocatedBytes,
        TimeSpan intervalCpuTime,
        long intervalAllocatedBytes,
        bool allocationCoverageComplete,
        IReadOnlyList<BusinessBlockThreadMetricFacts> activeThreads,
        BusinessBlockAttributionCoverage coverage = BusinessBlockAttributionCoverage.None)
    {
        IntervalStartedAt = intervalStartedAt;
        SampledAt = sampledAt;
        TotalCpuTime = totalCpuTime;
        TotalAllocatedBytes = totalAllocatedBytes;
        IntervalCpuTime = intervalCpuTime;
        IntervalAllocatedBytes = intervalAllocatedBytes;
        AllocationCoverageComplete = allocationCoverageComplete;
        ActiveThreads = new ReadOnlyCollection<BusinessBlockThreadMetricFacts>(activeThreads.ToArray());
        Coverage = coverage;
    }

    public DateTimeOffset IntervalStartedAt { get; }

    public DateTimeOffset SampledAt { get; }

    public TimeSpan TotalCpuTime { get; }

    public long TotalAllocatedBytes { get; }

    public TimeSpan IntervalCpuTime { get; }

    public long IntervalAllocatedBytes { get; }

    /// <summary>
    /// False while a synchronous segment or registered thread still has allocations
    /// that cannot yet be read from its owning thread.
    /// </summary>
    public bool AllocationCoverageComplete { get; }

    public IReadOnlyList<BusinessBlockThreadMetricFacts> ActiveThreads { get; }

    /// <summary>Identifies which measurement paths have been used since the last reset.</summary>
    public BusinessBlockAttributionCoverage Coverage { get; }
}

public sealed record BusinessBlockThreadMetricFacts(
    uint NativeThreadId,
    string? Name,
    string SessionName,
    DateTimeOffset RegisteredAt,
    TimeSpan CpuTime);
