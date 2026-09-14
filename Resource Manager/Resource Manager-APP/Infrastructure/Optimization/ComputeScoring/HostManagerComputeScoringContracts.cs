using System.Collections.Immutable;
using System.Numerics;
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
    uint EligibleProcessCount,
    ulong PublicationFingerprint = 0)
{
    internal bool IsValid => IsRatio(CpuFreeRatio)
        && IsRatio(GpuFreeRatio)
        && IsRatio(VramFreeRatio)
        && IsRatio(MemoryFreeRatio);

    internal static HostManagerWelfareCapacityInput Unavailable(uint eligibleProcessCount)
        => new(0, 0, 0, 0, eligibleProcessCount, 0);

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
    double SoftwareBaseMean,
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
    ulong SchedulingGeneration,
    ulong TargetKey,
    ulong SoftwareKey,
    int ProcessId,
    ulong ProcessStartKey,
    ulong AdapterKey,
    double Score,
    uint MemberCount,
    NativeComputeScoringRuntimeState RuntimeState);

internal readonly record struct HostManagerComputeScoreSourceIdentity(
    ulong InventoryGeneration,
    long InventoryObservedAtUtcTicks,
    ulong AttributionGeneration,
    long AttributionObservedAtUtcTicks,
    ulong RuntimeStateGeneration,
    long RuntimeStateObservedAtUtcTicks,
    ulong MetricGeneration,
    long MetricObservedAtUtcTicks,
    ulong TopologyGeneration,
    ulong TopologyFingerprint,
    ulong SourceFingerprint)
{
    internal bool IsWellFormed(bool gpuDomain)
        => InventoryGeneration > 0
            && InventoryObservedAtUtcTicks > 0
            && AttributionGeneration > 0
            && AttributionObservedAtUtcTicks > 0
            && RuntimeStateGeneration > 0
            && RuntimeStateObservedAtUtcTicks > 0
            && MetricGeneration > 0
            && MetricObservedAtUtcTicks > 0
            && SourceFingerprint > 0
            && gpuDomain == (TopologyGeneration > 0 && TopologyFingerprint > 0);
}

internal sealed record HostManagerComputeScoreDomainSnapshot(
    ulong SchedulingGeneration,
    HostManagerComputeScoreSourceIdentity SourceIdentity,
    ImmutableArray<HostManagerComputeScore> Scores)
{
    internal HostManagerComputeScoreDomainSnapshot(
        ulong SchedulingGeneration,
        ulong SourceGeneration,
        ulong TopologyGeneration,
        ulong TopologyFingerprint,
        ImmutableArray<HostManagerComputeScore> Scores)
        : this(
            SchedulingGeneration,
            new HostManagerComputeScoreSourceIdentity(
                SourceGeneration,
                1,
                SourceGeneration,
                1,
                SourceGeneration,
                1,
                SourceGeneration,
                1,
                TopologyGeneration,
                TopologyFingerprint,
                CreateFixtureFingerprint(
                    SourceGeneration,
                    TopologyGeneration,
                    TopologyFingerprint)),
            Scores)
    {
    }

    internal ulong SourceGeneration => SourceIdentity.MetricGeneration;

    internal ulong TopologyGeneration => SourceIdentity.TopologyGeneration;

    internal ulong TopologyFingerprint => SourceIdentity.TopologyFingerprint;

    internal ulong SourceFingerprint => SourceIdentity.SourceFingerprint;

    private static ulong CreateFixtureFingerprint(
        ulong sourceGeneration,
        ulong topologyGeneration,
        ulong topologyFingerprint)
    {
        var value = sourceGeneration
            ^ BitOperations.RotateLeft(topologyGeneration, 17)
            ^ BitOperations.RotateLeft(topologyFingerprint, 41);
        return value == 0 ? 1 : value;
    }
}

internal sealed record HostManagerComputeScoringCycleResult(
    ulong SchedulingGeneration,
    HostManagerComputeScoreDomainSnapshot? Cpu,
    HostManagerComputeScoreDomainSnapshot? Gpu)
{
    internal HostManagerWelfareScoreSnapshot Welfare { get; init; }
    internal HostManagerComputeScoreDomainSnapshot? Memory { get; init; }
}

internal sealed record HostManagerComputeScoringShadowSnapshot(
    ulong LastAttemptGeneration,
    HostManagerComputeScoreDomainSnapshot? Cpu,
    HostManagerComputeScoreDomainSnapshot? Gpu,
    string? LastFailure)
{
    internal static HostManagerComputeScoringShadowSnapshot Empty { get; } = new(
        0,
        null,
        null,
        null);

    internal HostManagerComputeScoringShadowSnapshot Merge(
        HostManagerComputeScoringCycleResult result)
        => new(
            result.SchedulingGeneration,
            result.Cpu,
            result.Gpu,
            null);

    internal HostManagerComputeScoringShadowSnapshot Fail(
        ulong attemptGeneration,
        string failure)
        => this with
        {
            LastAttemptGeneration = attemptGeneration,
            LastFailure = failure
        };
}
