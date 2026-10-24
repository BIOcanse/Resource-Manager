using System.Collections.Immutable;
using ResourceManager.App.Infrastructure.NativeCore;

namespace ResourceManager.App.Infrastructure.Optimization;

internal sealed record HostManagerDesiredMemoryMode(
    ulong SoftwareKey,
    ulong RoundNumber,
    double CpuScore,
    uint Rank,
    NativeMemoryMode Mode,
    NativeMemoryGradeSet BaseScoreAllowedGrades,
    double BaseScore = 0,
    bool BaseScoreClamped = false);

internal sealed record HostManagerMemoryModeDesiredSnapshot(
    ulong ConfigurationGeneration,
    ulong RoundNumber,
    bool UnrestrictedAllowed,
    uint OptimizeCount,
    uint StrongestCount,
    ImmutableArray<HostManagerDesiredMemoryMode> Software);
