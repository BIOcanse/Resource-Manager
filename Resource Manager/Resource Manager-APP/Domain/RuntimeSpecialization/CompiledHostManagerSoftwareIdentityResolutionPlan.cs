using System.Collections.Immutable;

namespace ResourceManager.App.Domain.RuntimeSpecialization;

public sealed record CompiledHostManagerSoftwareIdentityResolutionCapacityPlan(
    int MaximumPolicyCount,
    int MaximumObservationCount,
    int PolicyIndexCapacity)
{
    public bool IsPublished => MaximumPolicyCount is > 0 and <= 64
        && MaximumObservationCount > 0
        && PolicyIndexCapacity >= MaximumPolicyCount
        && (PolicyIndexCapacity & (PolicyIndexCapacity - 1)) == 0;
}

public sealed record CompiledHostManagerSoftwareIdentitySourcePolicyPlan(
    uint SourceId,
    uint Priority,
    bool StopOnUnavailable)
{
    public bool IsPublished => SourceId is >= 1 and <= 64 && Priority > 0;
}

public sealed record CompiledHostManagerSoftwareIdentityResolutionBuildPlan(
    uint AbiVersion,
    string NativeModule,
    string NativeBinaryFileName,
    string NativeBinarySha256,
    CompiledHostManagerSoftwareIdentityResolutionCapacityPlan CapacityLimits)
{
    public bool IsPublished => AbiVersion == 0x0001_0000U
        && string.Equals(NativeModule, "software_identity_resolution", StringComparison.Ordinal)
        && !string.IsNullOrWhiteSpace(NativeBinaryFileName)
        && NativeBinarySha256.Length == 64
        && NativeBinarySha256.All(static value => char.IsAsciiHexDigit(value))
        && CapacityLimits.IsPublished;

    public static CompiledHostManagerSoftwareIdentityResolutionBuildPlan Unpublished { get; } = new(
        0,
        string.Empty,
        string.Empty,
        string.Empty,
        new CompiledHostManagerSoftwareIdentityResolutionCapacityPlan(0, 0, 0));
}

public sealed record CompiledHostManagerSoftwareIdentityResolutionRecreatePlan(
    CompiledHostManagerSoftwareIdentityResolutionCapacityPlan Capacity,
    ImmutableArray<uint> SourceIds)
{
    public bool IsPublished => Capacity.IsPublished
        && !SourceIds.IsDefaultOrEmpty
        && SourceIds.Length <= Capacity.MaximumPolicyCount
        && SourceIds.SequenceEqual(SourceIds.Order())
        && SourceIds.Distinct().Count() == SourceIds.Length
        && SourceIds.All(static value => value is >= 1 and <= 64);

    public static CompiledHostManagerSoftwareIdentityResolutionRecreatePlan Unpublished { get; } = new(
        new CompiledHostManagerSoftwareIdentityResolutionCapacityPlan(0, 0, 0),
        []);
}

public sealed record CompiledHostManagerSoftwareIdentityResolutionHotPublishPlan(
    ulong ConfigurationGeneration,
    int MaximumFutureSkewMilliseconds,
    long ResidentByteBudget,
    ImmutableArray<CompiledHostManagerSoftwareIdentitySourcePolicyPlan> SourcePolicies)
{
    public bool IsPublished => ConfigurationGeneration > 0
        && MaximumFutureSkewMilliseconds >= 0
        && ResidentByteBudget > 0
        && !SourcePolicies.IsDefaultOrEmpty
        && SourcePolicies.All(static value => value.IsPublished)
        && SourcePolicies.Select(static value => value.SourceId).Distinct().Count() == SourcePolicies.Length
        && SourcePolicies.Select(static value => value.Priority).Distinct().Count() == SourcePolicies.Length
        && SourcePolicies.SequenceEqual(SourcePolicies.OrderBy(static value => value.Priority));

    public static CompiledHostManagerSoftwareIdentityResolutionHotPublishPlan Unpublished { get; } = new(
        0,
        0,
        0,
        []);
}

public sealed record CompiledHostManagerSoftwareIdentityResolutionPlan(
    CompiledHostManagerSoftwareIdentityResolutionBuildPlan Build,
    CompiledHostManagerSoftwareIdentityResolutionRecreatePlan Recreate,
    CompiledHostManagerSoftwareIdentityResolutionHotPublishPlan HotPublish,
    ulong ConfigurationGeneration,
    string ConfigurationSha256)
{
    public bool IsPublished => Build.IsPublished
        && Recreate.IsPublished
        && HotPublish.IsPublished
        && HotPublish.ConfigurationGeneration == ConfigurationGeneration
        && Recreate.SourceIds.SequenceEqual(
            HotPublish.SourcePolicies
                .Select(static value => value.SourceId)
                .Order())
        && ConfigurationGeneration > 0
        && ConfigurationSha256.Length == 64
        && ConfigurationSha256.All(static value => char.IsAsciiHexDigit(value));

    public static CompiledHostManagerSoftwareIdentityResolutionPlan Unpublished { get; } = new(
        CompiledHostManagerSoftwareIdentityResolutionBuildPlan.Unpublished,
        CompiledHostManagerSoftwareIdentityResolutionRecreatePlan.Unpublished,
        CompiledHostManagerSoftwareIdentityResolutionHotPublishPlan.Unpublished,
        0,
        string.Empty);
}
