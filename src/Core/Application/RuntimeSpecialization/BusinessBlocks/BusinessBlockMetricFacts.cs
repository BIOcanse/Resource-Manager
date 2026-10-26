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
        TimeSpan totalCpuTime,
        long totalAllocatedBytes,
        bool allocationCoverageComplete,
        IReadOnlyList<BusinessBlockThreadMetricFacts> activeThreads,
        BusinessBlockAttributionCoverage coverage = BusinessBlockAttributionCoverage.None)
    {
        TotalCpuTime = totalCpuTime;
        TotalAllocatedBytes = totalAllocatedBytes;
        AllocationCoverageComplete = allocationCoverageComplete;
        ActiveThreads = new ReadOnlyCollection<BusinessBlockThreadMetricFacts>(activeThreads.ToArray());
        Coverage = coverage;
    }

    /// <summary>Cumulative observed CPU from completed synchronous scopes and live registered threads as of the containing read.</summary>
    public TimeSpan TotalCpuTime { get; }

    /// <summary>Cumulative observed managed allocations; active scopes and registrations are excluded until they end.</summary>
    public long TotalAllocatedBytes { get; }

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
