using System.Collections.Immutable;
using ResourceManager.App.Infrastructure.NativeCore;

namespace ResourceManager.App.Infrastructure.Optimization;

internal readonly record struct HostManagerComputeProcessIdentity(
    int ProcessId,
    ulong ProcessStartKey);

internal readonly record struct HostManagerComputeRuntimeFact(
    NativeComputeScoringRuntimeState RuntimeState,
    double CpuPolicyMultiplier,
    double GpuPolicyMultiplier);

internal readonly record struct HostManagerWelfareCapacityInput(
    double CpuFreeRatio,
    double GpuFreeRatio,
    double VramFreeRatio,
    double MemoryFreeRatio,
    uint EligibleProcessCount)
{
    internal bool IsValid => IsRatio(CpuFreeRatio)
        && IsRatio(GpuFreeRatio)
        && IsRatio(VramFreeRatio)
        && IsRatio(MemoryFreeRatio);

    internal static HostManagerWelfareCapacityInput Unavailable(uint eligibleProcessCount)
        => new(0, 0, 0, 0, eligibleProcessCount);

    private static bool IsRatio(double value)
        => double.IsFinite(value) && value is >= 0 and <= 1;
}

internal readonly record struct HostManagerWelfareScoreSnapshot(
    double CpuFreeRatio,
    double GpuFreeRatio,
    double VramFreeRatio,
    double MemoryFreeRatio,
    double Multiplier,
    double SystemPressure,
    double WelfareBaseScore,
    double Budget,
    double Share,
    uint EligibleProcessCount)
{
    internal double CpuMultiplier { get; init; }
    internal double MemoryMultiplier { get; init; }
    internal double CpuBonus { get; init; }
    internal double MemoryBonus { get; init; }
}

internal sealed record HostManagerComputeScore(
    NativeComputeScoringOutputKind Kind,
    ulong RoundNumber,
    ulong TargetKey,
    ulong SoftwareKey,
    int ProcessId,
    ulong ProcessStartKey,
    ulong AdapterKey,
    double Score,
    uint MemberCount,
    NativeComputeScoringRuntimeState RuntimeState);

internal sealed record HostManagerComputeScoreDomainSnapshot(
    ulong RoundNumber,
    ImmutableArray<HostManagerComputeScore> Scores);

internal sealed record HostManagerComputeScoringCycleResult(
    ulong RoundNumber,
    HostManagerComputeScoreDomainSnapshot? Cpu,
    HostManagerComputeScoreDomainSnapshot? Gpu)
{
    internal HostManagerWelfareScoreSnapshot Welfare { get; init; }
    internal HostManagerComputeScoreDomainSnapshot? Memory { get; init; }
    internal HostManagerCpuOccupancySource? CpuOccupancySource { get; init; }
}

/// <summary>Where the CPU domain's per-process occupancy came from this cycle.</summary>
internal enum HostManagerCpuOccupancySource
{
    /// <summary>Per-physical-core residency weighted by core capacity.</summary>
    CoreResidency,

    /// <summary>Each process's CPU usage as a share of the whole machine.</summary>
    ProcessCpuUsage
}
