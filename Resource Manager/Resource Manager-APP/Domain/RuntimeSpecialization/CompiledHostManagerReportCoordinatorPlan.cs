using System.Collections.Immutable;

namespace ResourceManager.App.Domain.RuntimeSpecialization;

public sealed record CompiledHostManagerReportCoordinatorCapacityPlan(
    int MaximumSourceCount,
    int MaximumRuleCount,
    int MaximumObservationCount,
    int MaximumReportCount,
    int MaximumTrustCount,
    int MaximumBucketCount,
    int MaximumPersistenceOperationCount,
    int MaximumReportOutputCount,
    int MaximumRollingObservationCount,
    int SourceIndexCapacity,
    int RuleIndexCapacity,
    int ObservationIndexCapacity,
    int ReportIndexCapacity,
    int TrustIndexCapacity,
    int BucketIndexCapacity,
    int PlannedPersistenceIndexCapacity)
{
    public bool IsPublished
    {
        get
        {
            var maximumDirtyCount = 1L
                + MaximumSourceCount
                + MaximumObservationCount
                + MaximumBucketCount
                + MaximumReportCount
                + MaximumTrustCount;
            return MaximumSourceCount > 0
                && MaximumRuleCount > 0
                && MaximumObservationCount > 0
                && MaximumReportCount > 0
                && MaximumTrustCount > 0
                && MaximumBucketCount > 0
                && MaximumPersistenceOperationCount >= maximumDirtyCount
                && MaximumReportOutputCount is > 0
                && MaximumReportOutputCount <= MaximumReportCount
                && MaximumRollingObservationCount is > 0
                && MaximumRollingObservationCount <= MaximumObservationCount
                && IsValidIndexCapacity(SourceIndexCapacity, MaximumSourceCount)
                && IsValidIndexCapacity(RuleIndexCapacity, MaximumRuleCount)
                && IsValidIndexCapacity(ObservationIndexCapacity, MaximumObservationCount)
                && IsValidIndexCapacity(ReportIndexCapacity, MaximumReportCount)
                && IsValidIndexCapacity(TrustIndexCapacity, MaximumTrustCount)
                && IsValidIndexCapacity(BucketIndexCapacity, MaximumBucketCount)
                && IsValidIndexCapacity(
                    PlannedPersistenceIndexCapacity,
                    MaximumPersistenceOperationCount);
        }
    }

    private static bool IsValidIndexCapacity(int capacity, int count)
        => capacity >= (long)count * 2
            && capacity > 0
            && (capacity & (capacity - 1)) == 0;
}

public sealed record CompiledHostManagerReportCoordinatorBuildPlan(
    uint AbiVersion,
    string NativeModule,
    string NativeBinaryFileName,
    string NativeBinarySha256,
    CompiledHostManagerReportCoordinatorCapacityPlan CapacityLimits)
{
    private static CompiledHostManagerReportCoordinatorCapacityPlan EmptyCapacity { get; } =
        new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

    public bool IsPublished => AbiVersion == 0x0006_0000U
        && string.Equals(NativeModule, "report_coordinator", StringComparison.Ordinal)
        && !string.IsNullOrWhiteSpace(NativeBinaryFileName)
        && NativeBinarySha256.Length == 64
        && NativeBinarySha256.All(static value => char.IsAsciiHexDigit(value))
        && CapacityLimits.IsPublished;

    public static CompiledHostManagerReportCoordinatorBuildPlan Unpublished { get; } = new(
        0,
        string.Empty,
        string.Empty,
        string.Empty,
        EmptyCapacity);
}

public sealed record CompiledHostManagerReportCoordinatorRulePlan(
    HostManagerReportFactKind FactKind,
    ulong RuleHandle,
    ulong RuleGeneration,
    ulong SourceHandle,
    ulong CoverageScopeHandle,
    ulong FamilyHandle,
    ulong ReportTypeHandle,
    ulong ResourceKindHandle,
    ulong PayloadHandle,
    uint MetricSelector,
    uint Comparison,
    uint Priority,
    uint Severity,
    uint RequiredConsecutiveHits,
    uint RequiredConsecutiveMisses,
    long MinimumSampleDurationMilliseconds,
    double ActivationThreshold,
    double ClearThreshold,
    long StaleAfterMilliseconds,
    long RetentionMilliseconds,
    bool Rolling,
    ulong PredicateGroupHandle,
    uint PredicateIndex,
    uint PredicateCount)
{
    public bool IsPublished => Enum.IsDefined(FactKind)
        && RuleHandle > 0
        && RuleGeneration > 0
        && SourceHandle > 0
        && CoverageScopeHandle > 0
        && FamilyHandle > 0
        && ReportTypeHandle > 0
        && ResourceKindHandle > 0
        && PayloadHandle > 0
        && MetricSelector is >= 1 and <= 13
        && Comparison is >= 1 and <= 7
        && Priority > 0
        && Severity > 0
        && RequiredConsecutiveHits > 0
        && RequiredConsecutiveMisses > 0
        && MinimumSampleDurationMilliseconds > 0
        && double.IsFinite(ActivationThreshold)
        && double.IsFinite(ClearThreshold)
        && StaleAfterMilliseconds > 0
        && RetentionMilliseconds >= StaleAfterMilliseconds
        && PredicateGroupHandle > 0
        && PredicateCount > 0
        && PredicateIndex < PredicateCount;
}

public sealed record CompiledHostManagerReportCoordinatorRecreatePlan(
    CompiledHostManagerReportCoordinatorCapacityPlan Capacity,
    ImmutableArray<CompiledHostManagerReportCoordinatorRulePlan> Rules)
{
    public bool IsPublished => Capacity.IsPublished
        && !Rules.IsDefaultOrEmpty
        && Rules.Length <= Capacity.MaximumRuleCount
        && Rules.All(static rule => rule.IsPublished);

    public static CompiledHostManagerReportCoordinatorRecreatePlan Unpublished { get; } = new(
        new CompiledHostManagerReportCoordinatorCapacityPlan(
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0),
        []);
}

public sealed record CompiledHostManagerReportCoordinatorHotPublishPlan(
    ulong ConfigurationGeneration,
    long BucketWidthMilliseconds,
    long Window24HoursMilliseconds,
    long Window7DaysMilliseconds,
    long MaximumFutureSkewMilliseconds,
    long DefaultStaleAfterMilliseconds,
    long DefaultRetentionMilliseconds,
    long MetadataCheckpointIntervalMilliseconds,
    long ResidentByteBudget)
{
    public bool IsPublished => ConfigurationGeneration > 0
        && BucketWidthMilliseconds > 0
        && Window24HoursMilliseconds > 0
        && Window7DaysMilliseconds > Window24HoursMilliseconds
        && Window24HoursMilliseconds % BucketWidthMilliseconds == 0
        && Window7DaysMilliseconds % BucketWidthMilliseconds == 0
        && MaximumFutureSkewMilliseconds >= 0
        && DefaultStaleAfterMilliseconds > 0
        && DefaultRetentionMilliseconds >= DefaultStaleAfterMilliseconds
        && MetadataCheckpointIntervalMilliseconds > 0
        && ResidentByteBudget > 0;

    public static CompiledHostManagerReportCoordinatorHotPublishPlan Unpublished { get; } = new(
        0, 0, 0, 0, 0, 0, 0, 0, 0);
}

public sealed record CompiledHostManagerReportCoordinatorPlan(
    CompiledHostManagerReportCoordinatorBuildPlan Build,
    CompiledHostManagerReportCoordinatorRecreatePlan Recreate,
    CompiledHostManagerReportCoordinatorHotPublishPlan HotPublish,
    ulong ConfigurationGeneration,
    string ConfigurationSha256)
{
    public bool IsPublished => Build.IsPublished
        && Recreate.IsPublished
        && HotPublish.IsPublished
        && ConfigurationGeneration > 0
        && HotPublish.ConfigurationGeneration == ConfigurationGeneration
        && ConfigurationSha256.Length == 64
        && ConfigurationSha256.All(static value => char.IsAsciiHexDigit(value));

    public static CompiledHostManagerReportCoordinatorPlan Unpublished { get; } = new(
        CompiledHostManagerReportCoordinatorBuildPlan.Unpublished,
        CompiledHostManagerReportCoordinatorRecreatePlan.Unpublished,
        CompiledHostManagerReportCoordinatorHotPublishPlan.Unpublished,
        0,
        string.Empty);
}
