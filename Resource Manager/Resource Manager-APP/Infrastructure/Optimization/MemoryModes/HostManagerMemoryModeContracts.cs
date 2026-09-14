using System.Collections.Immutable;
using ResourceManager.App.Infrastructure.NativeCore;

namespace ResourceManager.App.Infrastructure.Optimization;

internal sealed record HostManagerDesiredMemoryMode(
    ulong SoftwareKey,
    ulong SchedulingGeneration,
    ulong SnapshotGeneration,
    double CpuScore,
    uint Rank,
    NativeMemoryMode Mode,
    NativeMemoryGradeSet BaseScoreAllowedGrades,
    double BaseScore = 0,
    bool BaseScoreClamped = false);

internal readonly record struct HostManagerMemorySourceStamp(
    ulong WorkspaceIdentity,
    ulong CommittedGeneration)
{
    internal bool IsValid => WorkspaceIdentity != 0 && CommittedGeneration != 0;
}

internal sealed record HostManagerMemoryModeDesiredSnapshot(
    ulong ConfigurationGeneration,
    ulong SchedulingGeneration,
    HostManagerMemorySourceStamp MemorySource,
    ulong SnapshotGeneration,
    bool UnrestrictedAllowed,
    uint OptimizeCount,
    uint StrongestCount,
    ImmutableArray<HostManagerDesiredMemoryMode> Software);
