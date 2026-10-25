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

    /// <summary>Start of the latest sample interval, or the reset baseline if no sample has occurred since reset.</summary>
    public DateTimeOffset IntervalStartedAt { get; }

    /// <summary>
    /// Time of the most recent explicit Sample call, or the last Reset baseline if
    /// no sample has occurred since reset. Read does not advance this timestamp.
    /// The enclosing business-block snapshot's At records the current read time.
    /// </summary>
    public DateTimeOffset SampledAt { get; }

    /// <summary>Cumulative observed CPU from completed synchronous scopes and live registered threads as of the containing read.</summary>
    public TimeSpan TotalCpuTime { get; }

    /// <summary>Cumulative observed managed allocations; active scopes and registrations are excluded until they end.</summary>
    public long TotalAllocatedBytes { get; }

    /// <summary>CPU interval value from the most recent explicit sample.</summary>
    public TimeSpan IntervalCpuTime { get; }

    /// <summary>Managed-allocation interval value from the most recent explicit sample.</summary>
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
