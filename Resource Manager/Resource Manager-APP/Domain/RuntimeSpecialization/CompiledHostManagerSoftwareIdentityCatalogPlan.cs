using System.Collections.Immutable;

namespace ResourceManager.App.Domain.RuntimeSpecialization;

public sealed record CompiledHostManagerSoftwareIdentityCatalogCapacityPlan(
    int MaximumEntryCount,
    int MaximumAliasCount,
    int MaximumRootCount,
    int MaximumCatalogKeyByteCount,
    int MaximumQueryFactCount,
    int MaximumQuerySignalCount,
    int MaximumQueryKeyByteCount,
    int EntryIndexCapacity,
    int AliasIndexCapacity,
    int IdentityIndexCapacity,
    int RootIndexCapacity)
{
    public bool IsPublished => MaximumEntryCount > 0
        && MaximumAliasCount > 0
        && MaximumRootCount >= 0
        && MaximumCatalogKeyByteCount > 0
        && MaximumQueryFactCount > 0
        && MaximumQuerySignalCount is > 0 and <= 6
        && MaximumQueryKeyByteCount > 0
        && IsValidIndexCapacity(EntryIndexCapacity, MaximumEntryCount)
        && IsValidIndexCapacity(AliasIndexCapacity, MaximumAliasCount)
        && IsValidIndexCapacity(IdentityIndexCapacity, MaximumEntryCount)
        && IsValidIndexCapacity(RootIndexCapacity, MaximumRootCount);

    private static bool IsValidIndexCapacity(int capacity, int count)
        => capacity > 0 && capacity >= count && (capacity & (capacity - 1)) == 0;
}

public sealed record CompiledHostManagerSoftwareIdentityCatalogBuildPlan(
    uint AbiVersion,
    string NativeModule,
    string NativeBinaryFileName,
    string NativeBinarySha256,
    CompiledHostManagerSoftwareIdentityCatalogCapacityPlan CapacityLimits)
{
    public bool IsPublished => AbiVersion == 0x0005_0000U
        && string.Equals(NativeModule, "software_identity_catalog", StringComparison.Ordinal)
        && !string.IsNullOrWhiteSpace(NativeBinaryFileName)
        && NativeBinarySha256.Length == 64
        && NativeBinarySha256.All(static value => char.IsAsciiHexDigit(value))
        && CapacityLimits.IsPublished;

    public static CompiledHostManagerSoftwareIdentityCatalogBuildPlan Unpublished { get; } = new(
        0,
        string.Empty,
        string.Empty,
        string.Empty,
        new CompiledHostManagerSoftwareIdentityCatalogCapacityPlan(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0));
}

public sealed record CompiledHostManagerSoftwareIdentityCatalogRecreatePlan(
    CompiledHostManagerSoftwareIdentityCatalogCapacityPlan Capacity,
    ImmutableArray<string> ProhibitedExecutableAliases,
    ImmutableArray<string> LauncherTokens,
    ImmutableArray<string> ManagedChildSegments)
{
    public bool IsPublished => Capacity.IsPublished
        && !ProhibitedExecutableAliases.IsDefault
        && !LauncherTokens.IsDefault
        && !ManagedChildSegments.IsDefault
        && ProhibitedExecutableAliases.All(static value => !string.IsNullOrWhiteSpace(value))
        && LauncherTokens.All(static value => !string.IsNullOrWhiteSpace(value))
        && ManagedChildSegments.All(static value => !string.IsNullOrWhiteSpace(value))
        && ProhibitedExecutableAliases.Length + LauncherTokens.Length + ManagedChildSegments.Length
            <= Capacity.MaximumAliasCount;

    public static CompiledHostManagerSoftwareIdentityCatalogRecreatePlan Unpublished { get; } = new(
        new CompiledHostManagerSoftwareIdentityCatalogCapacityPlan(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0),
        [],
        [],
        []);
}

public sealed record CompiledHostManagerSoftwareIdentityCatalogHotPublishPlan(
    ulong ConfigurationGeneration,
    long ResidentByteBudget,
    int MinimumContainsKeyLength,
    int ExactTextScore,
    int ContainsTextScore,
    int IdentityMinimumScore,
    int StrongEvidenceMinimumScore,
    int RootHitBonus,
    int QueryLauncherMatchBonus,
    int QueryLauncherNonmatchPenalty,
    int RootLauncherMatchBonus,
    int RootLauncherNonmatchPenalty,
    int RootRejectScore,
    ImmutableArray<int> SignalWeights,
    ImmutableArray<int> RootSignalWeights,
    uint StrongSignalMask,
    uint RootSignalMask,
    bool LauncherNonEntryRequiresRoot)
{
    public bool IsPublished => ConfigurationGeneration > 0
        && ResidentByteBudget > 0
        && MinimumContainsKeyLength > 0
        && ExactTextScore > 0
        && ContainsTextScore > 0
        && IdentityMinimumScore >= 0
        && StrongEvidenceMinimumScore >= 0
        && RootHitBonus >= 0
        && QueryLauncherMatchBonus >= 0
        && QueryLauncherNonmatchPenalty >= 0
        && RootLauncherMatchBonus >= 0
        && RootLauncherNonmatchPenalty >= 0
        && SignalWeights.Length == 6
        && RootSignalWeights.Length == 6
        && SignalWeights.All(static value => value >= 0)
        && RootSignalWeights.All(static value => value >= 0)
        && (StrongSignalMask & ~0x3FU) == 0
        && (RootSignalMask & ~0x3FU) == 0;

    public static CompiledHostManagerSoftwareIdentityCatalogHotPublishPlan Unpublished { get; } = new(
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        [],
        [],
        0,
        0,
        false);
}

public sealed record CompiledHostManagerSoftwareIdentityCatalogPlan(
    CompiledHostManagerSoftwareIdentityCatalogBuildPlan Build,
    CompiledHostManagerSoftwareIdentityCatalogRecreatePlan Recreate,
    CompiledHostManagerSoftwareIdentityCatalogHotPublishPlan HotPublish,
    ulong ConfigurationGeneration,
    string ConfigurationSha256)
{
    public bool IsPublished => Build.IsPublished
        && Recreate.IsPublished
        && HotPublish.IsPublished
        && HotPublish.ConfigurationGeneration == ConfigurationGeneration
        && ConfigurationGeneration > 0
        && ConfigurationSha256.Length == 64
        && ConfigurationSha256.All(static value => char.IsAsciiHexDigit(value));

    public static CompiledHostManagerSoftwareIdentityCatalogPlan Unpublished { get; } = new(
        CompiledHostManagerSoftwareIdentityCatalogBuildPlan.Unpublished,
        CompiledHostManagerSoftwareIdentityCatalogRecreatePlan.Unpublished,
        CompiledHostManagerSoftwareIdentityCatalogHotPublishPlan.Unpublished,
        0,
        string.Empty);
}
