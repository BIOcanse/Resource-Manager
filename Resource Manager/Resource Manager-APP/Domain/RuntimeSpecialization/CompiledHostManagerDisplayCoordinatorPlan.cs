namespace ResourceManager.App.Domain.RuntimeSpecialization;

public sealed record CompiledHostManagerDisplayCoordinatorCapacityPlan(
    uint MaximumSourceCount,
    uint MaximumObservationCount,
    uint MaximumNodeCount,
    uint MaximumEdgeCount,
    uint MaximumCapabilityCount,
    uint MaximumDiffEntryCount,
    uint MaximumTextBindingCount,
    uint MaximumTextByteCount,
    uint MaximumUnresolvedCount,
    uint MaximumSourceBatchCount,
    uint IdentityIndexCapacity,
    uint TextIndexCapacity,
    ulong ResidentByteBudget)
{
    public bool IsPublished => MaximumSourceCount > 0
        && MaximumObservationCount > 0
        && MaximumNodeCount > 0
        && MaximumEdgeCount > 0
        && MaximumCapabilityCount > 0
        && MaximumDiffEntryCount > 0
        && MaximumTextBindingCount > 0
        && MaximumTextByteCount > 0
        && MaximumUnresolvedCount > 0
        && MaximumSourceBatchCount > 0
        && IdentityIndexCapacity > 0
        && TextIndexCapacity > 0
        && ResidentByteBudget > 0;

    public bool FitsWithin(CompiledHostManagerDisplayCoordinatorCapacityPlan maximum)
        => MaximumSourceCount <= maximum.MaximumSourceCount
            && MaximumObservationCount <= maximum.MaximumObservationCount
            && MaximumNodeCount <= maximum.MaximumNodeCount
            && MaximumEdgeCount <= maximum.MaximumEdgeCount
            && MaximumCapabilityCount <= maximum.MaximumCapabilityCount
            && MaximumDiffEntryCount <= maximum.MaximumDiffEntryCount
            && MaximumTextBindingCount <= maximum.MaximumTextBindingCount
            && MaximumTextByteCount <= maximum.MaximumTextByteCount
            && MaximumUnresolvedCount <= maximum.MaximumUnresolvedCount
            && MaximumSourceBatchCount <= maximum.MaximumSourceBatchCount
            && IdentityIndexCapacity <= maximum.IdentityIndexCapacity
            && TextIndexCapacity <= maximum.TextIndexCapacity
            && ResidentByteBudget <= maximum.ResidentByteBudget;

    public static CompiledHostManagerDisplayCoordinatorCapacityPlan Unpublished { get; } =
        new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
}

public sealed record CompiledHostManagerDisplayCoordinatorBuildPlan(
    uint AbiVersion,
    string NativeModule,
    CompiledHostManagerDisplayCoordinatorCapacityPlan CapacityLimits)
{
    public bool IsPublished => AbiVersion > 0
        && string.Equals(
            NativeModule,
            "display_coordinator",
            StringComparison.Ordinal)
        && CapacityLimits.IsPublished;

    public static CompiledHostManagerDisplayCoordinatorBuildPlan Unpublished { get; } =
        new(0, string.Empty, CompiledHostManagerDisplayCoordinatorCapacityPlan.Unpublished);
}

public sealed record CompiledHostManagerDisplayCoordinatorRecreatePlan(
    ulong ConfigurationGeneration,
    CompiledHostManagerDisplayCoordinatorCapacityPlan Capacity,
    string PersistenceRelativePath,
    ulong RequiredSourceMask,
    ulong OptionalSourceMask,
    ulong IdentitySourcePriorityOrder,
    ulong FriendlyNameSourcePriorityOrder,
    ulong CapabilitySourcePriorityOrder,
    ulong ProjectionSourcePriorityOrder,
    uint IdentityContractVersion,
    uint CapabilityContractVersion,
    uint MaximumFutureSkewMilliseconds)
{
    public bool IsPublished => ConfigurationGeneration > 0
        && Capacity.IsPublished
        && !string.IsNullOrWhiteSpace(PersistenceRelativePath)
        && RequiredSourceMask > 0
        && (RequiredSourceMask & OptionalSourceMask) == 0
        && IdentitySourcePriorityOrder > 0
        && FriendlyNameSourcePriorityOrder > 0
        && CapabilitySourcePriorityOrder > 0
        && ProjectionSourcePriorityOrder > 0
        && IdentityContractVersion > 0
        && CapabilityContractVersion > 0;

    public static CompiledHostManagerDisplayCoordinatorRecreatePlan Unpublished { get; } =
        new(
            0,
            CompiledHostManagerDisplayCoordinatorCapacityPlan.Unpublished,
            string.Empty,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0);
}

public sealed record CompiledHostManagerDisplayCoordinatorPlan(
    CompiledHostManagerDisplayCoordinatorBuildPlan Build,
    CompiledHostManagerDisplayCoordinatorRecreatePlan Recreate,
    string ConfigurationSha256)
{
    public bool IsPublished => Build.IsPublished
        && Recreate.IsPublished
        && Recreate.Capacity.FitsWithin(Build.CapacityLimits)
        && IsSha256(ConfigurationSha256);

    public static CompiledHostManagerDisplayCoordinatorPlan Unpublished { get; } =
        new(
            CompiledHostManagerDisplayCoordinatorBuildPlan.Unpublished,
            CompiledHostManagerDisplayCoordinatorRecreatePlan.Unpublished,
            string.Empty);

    private static bool IsSha256(string value)
        => value.Length == 64
            && value.All(static character => char.IsAsciiHexDigit(character));
}
