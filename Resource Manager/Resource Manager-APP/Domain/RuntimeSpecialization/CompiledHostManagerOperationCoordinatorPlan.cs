using System.Collections.Immutable;

namespace ResourceManager.App.Domain.RuntimeSpecialization;

public sealed record CompiledHostManagerOperationCoordinatorCapacityPlan(
    uint MaximumOperationCount,
    uint MaximumDomainCount,
    uint MaximumActionCount,
    uint OperationIndexCapacity,
    uint DomainIndexCapacity,
    uint MaximumReadCount,
    ulong MaximumPersistenceByteCount,
    ulong ResidentByteBudget,
    uint MaximumPayloadCount,
    ulong MaximumPayloadByteCount,
    uint MaximumEffectReceiptCount,
    ulong MaximumEnvelopeByteCount)
{
    public bool IsPublished => MaximumOperationCount > 0
        && MaximumDomainCount > 0
        && MaximumDomainCount <= MaximumOperationCount
        && MaximumActionCount > 0
        && MaximumActionCount <= MaximumOperationCount
        && OperationIndexCapacity >= MaximumOperationCount
        && DomainIndexCapacity >= MaximumDomainCount
        && MaximumReadCount == MaximumOperationCount
        && MaximumPersistenceByteCount > 0
        && ResidentByteBudget > 0
        && MaximumPayloadCount > 0
        && MaximumPayloadByteCount > 0
        && MaximumEffectReceiptCount > 0
        && MaximumEnvelopeByteCount > 0;

    public bool FitsWithin(CompiledHostManagerOperationCoordinatorCapacityPlan maximum)
        => MaximumOperationCount <= maximum.MaximumOperationCount
            && MaximumDomainCount <= maximum.MaximumDomainCount
            && MaximumActionCount <= maximum.MaximumActionCount
            && OperationIndexCapacity <= maximum.OperationIndexCapacity
            && DomainIndexCapacity <= maximum.DomainIndexCapacity
            && MaximumReadCount <= maximum.MaximumReadCount
            && MaximumPersistenceByteCount <= maximum.MaximumPersistenceByteCount
            && ResidentByteBudget <= maximum.ResidentByteBudget
            && MaximumPayloadCount <= maximum.MaximumPayloadCount
            && MaximumPayloadByteCount <= maximum.MaximumPayloadByteCount
            && MaximumEffectReceiptCount <= maximum.MaximumEffectReceiptCount
            && MaximumEnvelopeByteCount <= maximum.MaximumEnvelopeByteCount;

    public static CompiledHostManagerOperationCoordinatorCapacityPlan Unpublished { get; } =
        new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
}

public sealed record CompiledHostManagerOperationCoordinatorBuildPlan(
    uint AbiVersion,
    string NativeModule,
    CompiledHostManagerOperationCoordinatorCapacityPlan CapacityLimits)
{
    public bool IsPublished => AbiVersion == 0x0001_0000U
        && string.Equals(
            NativeModule,
            "operation_coordinator",
            StringComparison.Ordinal)
        && CapacityLimits.IsPublished;

    public static CompiledHostManagerOperationCoordinatorBuildPlan Unpublished { get; } =
        new(
            0,
            string.Empty,
            CompiledHostManagerOperationCoordinatorCapacityPlan.Unpublished);
}

public sealed record CompiledHostManagerOperationCoordinatorRecreatePlan(
    CompiledHostManagerOperationCoordinatorCapacityPlan Capacity,
    string CanonicalEnvelopeRelativePath)
{
    public bool IsPublished => Capacity.IsPublished
        && !string.IsNullOrWhiteSpace(CanonicalEnvelopeRelativePath);

    public static CompiledHostManagerOperationCoordinatorRecreatePlan Unpublished { get; } =
        new(
            CompiledHostManagerOperationCoordinatorCapacityPlan.Unpublished,
            string.Empty);
}

public sealed record CompiledHostManagerOperationKindPlan(
    string Name,
    long Priority,
    uint MaximumAttempts,
    uint RetryDelayMilliseconds,
    ulong ExecutionTimeoutMilliseconds,
    ulong CancelGraceMilliseconds,
    ulong TerminalRetentionMilliseconds)
{
    public bool IsPublished => !string.IsNullOrWhiteSpace(Name)
        && MaximumAttempts > 0
        && ExecutionTimeoutMilliseconds > 0
        && CancelGraceMilliseconds > 0
        && TerminalRetentionMilliseconds > 0;
}

public sealed record CompiledHostManagerOperationCoordinatorHotPublishPlan(
    ulong ConfigurationGeneration,
    uint MaximumGlobalRunningCount,
    uint MaximumRecentTerminalCount,
    uint MaximumStartActionsPerPlan,
    uint MaximumCancelActionsPerPlan,
    uint MaximumRecoverActionsPerPlan,
    ulong MaximumFutureSkewMilliseconds,
    ImmutableArray<CompiledHostManagerOperationKindPlan> Kinds)
{
    public bool IsPublished => ConfigurationGeneration > 0
        && MaximumGlobalRunningCount > 0
        && MaximumStartActionsPerPlan > 0
        && MaximumCancelActionsPerPlan > 0
        && MaximumRecoverActionsPerPlan > 0
        && !Kinds.IsDefaultOrEmpty
        && Kinds.All(static kind => kind.IsPublished)
        && Kinds.Select(static kind => kind.Name)
            .Distinct(StringComparer.Ordinal)
            .Count() == Kinds.Length;

    public static CompiledHostManagerOperationCoordinatorHotPublishPlan Unpublished { get; } =
        new(0, 0, 0, 0, 0, 0, 0, []);
}

public sealed record CompiledHostManagerOperationCoordinatorPlan(
    CompiledHostManagerOperationCoordinatorBuildPlan Build,
    CompiledHostManagerOperationCoordinatorRecreatePlan Recreate,
    CompiledHostManagerOperationCoordinatorHotPublishPlan HotPublish,
    string ConfigurationSha256)
{
    public bool IsPublished => Build.IsPublished
        && Recreate.IsPublished
        && HotPublish.IsPublished
        && Recreate.Capacity.FitsWithin(Build.CapacityLimits)
        && HotPublish.MaximumGlobalRunningCount <= Recreate.Capacity.MaximumOperationCount
        && HotPublish.MaximumRecentTerminalCount <= Recreate.Capacity.MaximumOperationCount
        && HotPublish.MaximumStartActionsPerPlan <= Recreate.Capacity.MaximumActionCount
        && HotPublish.MaximumCancelActionsPerPlan <= Recreate.Capacity.MaximumActionCount
        && HotPublish.MaximumRecoverActionsPerPlan <= Recreate.Capacity.MaximumActionCount
        && IsSha256(ConfigurationSha256);

    public static CompiledHostManagerOperationCoordinatorPlan Unpublished { get; } =
        new(
            CompiledHostManagerOperationCoordinatorBuildPlan.Unpublished,
            CompiledHostManagerOperationCoordinatorRecreatePlan.Unpublished,
            CompiledHostManagerOperationCoordinatorHotPublishPlan.Unpublished,
            string.Empty);

    private static bool IsSha256(string value)
        => value.Length == 64
            && value.All(static character => char.IsAsciiHexDigit(character));
}
