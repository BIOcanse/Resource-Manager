using System.Collections.Immutable;

using ResourceManager.App.Domain.RuntimeSpecialization.FreedomPoints;

namespace ResourceManager.App.Domain.RuntimeSpecialization;

public sealed record CompiledHostManagerSmartCoordinatorBuildPlan(
    uint AbiVersion,
    string NativeModule,
    string NativeBinaryFileName,
    string NativeBinarySha256)
{
    public bool IsPublished => AbiVersion > 0
        && string.Equals(NativeModule, "smart_coordinator", StringComparison.Ordinal)
        && !string.IsNullOrWhiteSpace(NativeBinaryFileName)
        && NativeBinarySha256.Length == 64
        && NativeBinarySha256.All(static value => char.IsAsciiHexDigit(value));

    public static CompiledHostManagerSmartCoordinatorBuildPlan Unpublished { get; } = new(
        0,
        string.Empty,
        string.Empty,
        string.Empty);
}

public sealed record CompiledHostManagerSmartCoordinatorRecreatePlan(
    int MaximumProcesses,
    int MaximumSoftwareGroups,
    int MaximumGpuStates,
    int MaximumInputRows,
    int MaximumActions,
    int MaximumReservations,
    int MaximumAtomicGroups)
{
    public bool IsPublished => MaximumProcesses > 0
        && MaximumSoftwareGroups > 0
        && MaximumGpuStates > 0
        && MaximumInputRows > 0
        && MaximumGpuStates >= MaximumInputRows
        && MaximumActions >= MaximumProcesses + MaximumSoftwareGroups
        && MaximumReservations >= MaximumProcesses + (2 * MaximumSoftwareGroups)
        && MaximumAtomicGroups >= MaximumProcesses;

    public static CompiledHostManagerSmartCoordinatorRecreatePlan Unpublished { get; } = new(
        0, 0, 0, 0, 0, 0, 0);
}

public sealed record CompiledHostManagerSmartCoordinatorAdapterPolicyPlan(
    ImmutableArray<double> StateMultipliers,
    double ExtremeMinimumScore,
    double NormalMinimumScore,
    double OptimizeMinimumScore)
{
    public bool IsPublished => StateMultipliers.Length == CompiledHostManagerSmartCoordinatorPlan.RuntimeStateCount;

    public static CompiledHostManagerSmartCoordinatorAdapterPolicyPlan Unpublished { get; } = new(
        [], 0, 0, 0);
}

public sealed record CompiledHostManagerBaseScoreTierPlan(
    double HighMinimumBaseScore,
    double MiddleMinimumBaseScore)
{
    public bool IsPublished => double.IsFinite(HighMinimumBaseScore)
        && double.IsFinite(MiddleMinimumBaseScore)
        && MiddleMinimumBaseScore > 0
        && MiddleMinimumBaseScore < HighMinimumBaseScore
        && HighMinimumBaseScore <= 100;

    public static CompiledHostManagerBaseScoreTierPlan Unpublished { get; } = new(0, 0);
}

public static class HostManagerMemoryModePolicySourceKinds
{
    public const string ProductBaseline = "product-baseline";
}

public static class HostManagerMemoryPriorityForeignDispositions
{
    public const string Reject = "reject";
}

public sealed record CompiledHostManagerMemoryModePolicyPlan(
    bool Enabled,
    string SourceKind,
    uint RatioUnitsMaximum,
    uint StrongBeginFreeRatioUnits,
    uint NormalMinimumFreeRatioUnits,
    uint UnrestrictedMinimumFreeRatioUnits,
    bool AllowUnrestricted,
    uint OptimizeMemoryPriority,
    uint PagedFrozenMemoryPriority,
    string ForeignMemoryPriorityDisposition,
    uint OwnedStateVerificationIntervalCycles,
    ulong ConfigurationGeneration,
    string ConfigurationSha256)
{
    public bool IsPublished => ConfigurationGeneration > 0
        && IsCanonicalSha256(ConfigurationSha256)
        && OptimizeMemoryPriority is >= 1 and <= 5
        && PagedFrozenMemoryPriority is >= 1 and <= 5
        && PagedFrozenMemoryPriority <= OptimizeMemoryPriority
        && (Enabled
            ? string.Equals(
                    SourceKind,
                    HostManagerMemoryModePolicySourceKinds.ProductBaseline,
                    StringComparison.Ordinal)
                && RatioUnitsMaximum == 10_000
                && StrongBeginFreeRatioUnits > 0
                && StrongBeginFreeRatioUnits < NormalMinimumFreeRatioUnits
                && NormalMinimumFreeRatioUnits < UnrestrictedMinimumFreeRatioUnits
                && UnrestrictedMinimumFreeRatioUnits <= RatioUnitsMaximum
                && string.Equals(
                    ForeignMemoryPriorityDisposition,
                    HostManagerMemoryPriorityForeignDispositions.Reject,
                    StringComparison.Ordinal)
                && OwnedStateVerificationIntervalCycles == 1
            : SourceKind.Length == 0
                && RatioUnitsMaximum == 0
                && StrongBeginFreeRatioUnits == 0
                && NormalMinimumFreeRatioUnits == 0
                && UnrestrictedMinimumFreeRatioUnits == 0
                && !AllowUnrestricted
                && ForeignMemoryPriorityDisposition.Length == 0
                && OwnedStateVerificationIntervalCycles == 0);

    public static CompiledHostManagerMemoryModePolicyPlan Unpublished { get; } =
        new(false, string.Empty, 0, 0, 0, 0, false, 5, 5, string.Empty, 0, 0, string.Empty);

    private static bool IsCanonicalSha256(string value)
        => value is not null
            && value.Length == 64
            && value.All(static character =>
                character is >= '0' and <= '9' or >= 'A' and <= 'F');
}

public sealed record CompiledHostManagerRealtimeActionBudgetPlan(string Mode, int Count, int Percent)
{
    public bool IsPublished => Mode is "fixed" or "percent"
        && Count is >= 1 and <= 8192
        && Percent is >= 1 and <= 100;
}

public sealed record CompiledHostManagerSmartCoordinatorHotPublishPlan(
    ulong FeatureFlags,
    int NormalIntervalMilliseconds,
    int EventIntervalMilliseconds,
    int EventBoostRounds,
    [property: global::FreedomPoint.FreedomPointBinding(BackendFreedomPointPositions.GameStartGraceMs)]
    int GameStartGraceMilliseconds,
    [property: global::FreedomPoint.FreedomPointBinding(BackendFreedomPointPositions.RequiredConsecutiveDecisions)]
    int RequiredConsecutiveDecisions,
    [property: global::FreedomPoint.FreedomPointBinding(BackendFreedomPointPositions.FailureRetryMs)]
    int FailureRetryMilliseconds,
    int ReservationTimeoutMilliseconds,
    CompiledHostManagerRealtimeActionBudgetPlan RealtimeActionBudget,
    [property: global::FreedomPoint.FreedomPointBinding(BackendFreedomPointPositions.SoftwareWelfare)]
    double WelfareBaseScore,
    [property: global::FreedomPoint.FreedomPointBinding(BackendFreedomPointPositions.StateMultipliers)]
    ImmutableArray<double> ProcessStateMultipliers,
    [property: global::FreedomPoint.FreedomPointBinding(BackendFreedomPointPositions.BaseScoreTiers)]
    CompiledHostManagerBaseScoreTierPlan BaseScoreTiers,
    double A1MinimumCpuScore,
    double DefaultMinimumCpuScoreScale,
    double Level1MaximumCpuScoreScale,
    double Level2MaximumCpuScoreScale,
    double Level3MaximumCpuScoreScale,
    double LowTierLevel4MaximumCpuScoreScale,
    CompiledHostManagerSmartCoordinatorAdapterPolicyPlan CpuAdapterPolicy,
    CompiledHostManagerSmartCoordinatorAdapterPolicyPlan GpuAdapterPolicy,
    CompiledHostManagerMemoryModePolicyPlan MemoryModePolicy)
{
    public bool IsPublished => FeatureFlags > 0
        && NormalIntervalMilliseconds > 0
        && EventIntervalMilliseconds > 0
        && EventBoostRounds > 0
        && GameStartGraceMilliseconds > 0
        && RequiredConsecutiveDecisions > 0
        && FailureRetryMilliseconds > 0
        && ReservationTimeoutMilliseconds > 0
        && RealtimeActionBudget.IsPublished
        && double.IsFinite(WelfareBaseScore)
        && WelfareBaseScore is >= 0 and <= 100
        && ProcessStateMultipliers.Length == CompiledHostManagerSmartCoordinatorPlan.RuntimeStateCount
        && BaseScoreTiers.IsPublished
        && CpuAdapterPolicy.IsPublished
        && GpuAdapterPolicy.IsPublished
        && MemoryModePolicy.IsPublished;

    public static CompiledHostManagerSmartCoordinatorHotPublishPlan Unpublished { get; } = new(
        0, 0, 0, 0, 0, 0, 0, 0, new("fixed", 0, 0), 0, [],
        CompiledHostManagerBaseScoreTierPlan.Unpublished,
        0, 0, 0, 0, 0, 0,
        CompiledHostManagerSmartCoordinatorAdapterPolicyPlan.Unpublished,
        CompiledHostManagerSmartCoordinatorAdapterPolicyPlan.Unpublished,
        CompiledHostManagerMemoryModePolicyPlan.Unpublished);
}

public sealed record CompiledHostManagerSmartCoordinatorPlan(
    CompiledHostManagerSmartCoordinatorBuildPlan Build,
    CompiledHostManagerSmartCoordinatorRecreatePlan Recreate,
    CompiledHostManagerSmartCoordinatorHotPublishPlan HotPublish,
    ulong ConfigurationGeneration,
    string ConfigurationSha256)
{
    public CompiledDataHistoryPlan DataHistory { get; init; } = CompiledDataHistoryPlan.Empty;

    public const int RuntimeStateCount = 7;

    public bool IsPublished => Build.IsPublished
        && Recreate.IsPublished
        && HotPublish.IsPublished
        && ConfigurationGeneration > 0
        && ConfigurationSha256.Length == 64
        && ConfigurationSha256.All(static value => char.IsAsciiHexDigit(value));

    public static CompiledHostManagerSmartCoordinatorPlan Unpublished { get; } = new(
        CompiledHostManagerSmartCoordinatorBuildPlan.Unpublished,
        CompiledHostManagerSmartCoordinatorRecreatePlan.Unpublished,
        CompiledHostManagerSmartCoordinatorHotPublishPlan.Unpublished,
        0,
        string.Empty);
}
