using System.Collections.Immutable;

namespace ResourceManager.App.Domain.RuntimeSpecialization;

public sealed record CompiledHostManagerMetricSnapshotCapacityPlan(
    uint MaximumSourceCount,
    uint MaximumMetricCount,
    uint MaximumRuleCount,
    uint MaximumRequestedCount,
    uint MaximumObservationCount,
    uint MaximumGpuAdapterCount,
    uint MaximumPersistenceSourceCount,
    uint MaximumPersistenceRuleCount,
    uint MaximumPersistenceGpuCount,
    uint SourceIndexCapacity,
    uint MetricIndexCapacity,
    uint RuleIndexCapacity,
    uint GpuIndexCapacity,
    uint GpuLuidIndexCapacity,
    uint GpuKeyIndexCapacity,
    uint MaximumPlanMetricCount,
    uint MaximumSourceModeCount,
    uint MaximumSourcePlanCount,
    uint MaximumMetricPlanCount,
    ulong ResidentByteBudget)
{
    public bool IsPublished => MaximumSourceCount > 0
        && MaximumMetricCount > 0
        && MaximumRuleCount > 0
        && MaximumRequestedCount > 0
        && MaximumObservationCount > 0
        && MaximumGpuAdapterCount > 0
        && MaximumPersistenceSourceCount >= MaximumSourceCount
        && MaximumPersistenceRuleCount >= MaximumRuleCount
        && MaximumPersistenceGpuCount >= MaximumGpuAdapterCount
        && IsValidIndexCapacity(SourceIndexCapacity, MaximumSourceCount)
        && IsValidIndexCapacity(MetricIndexCapacity, MaximumMetricCount)
        && IsValidIndexCapacity(RuleIndexCapacity, MaximumRuleCount)
        && IsValidIndexCapacity(GpuIndexCapacity, MaximumGpuAdapterCount)
        && IsValidIndexCapacity(GpuLuidIndexCapacity, MaximumGpuAdapterCount)
        && IsValidIndexCapacity(GpuKeyIndexCapacity, MaximumGpuAdapterCount)
        && MaximumRequestedCount <= MaximumRuleCount
        && MaximumObservationCount >= MaximumRequestedCount
        && MaximumPlanMetricCount >= MaximumRequestedCount
        && MaximumSourceModeCount >= MaximumSourceCount
        && MaximumSourcePlanCount == MaximumSourceCount
        && MaximumMetricPlanCount <= MaximumRuleCount
        && MaximumMetricPlanCount >= MaximumRequestedCount
        && ResidentByteBudget > 0;

    public bool FitsWithin(CompiledHostManagerMetricSnapshotCapacityPlan maximum)
        => MaximumSourceCount <= maximum.MaximumSourceCount
            && MaximumMetricCount <= maximum.MaximumMetricCount
            && MaximumRuleCount <= maximum.MaximumRuleCount
            && MaximumRequestedCount <= maximum.MaximumRequestedCount
            && MaximumObservationCount <= maximum.MaximumObservationCount
            && MaximumGpuAdapterCount <= maximum.MaximumGpuAdapterCount
            && MaximumPersistenceSourceCount <= maximum.MaximumPersistenceSourceCount
            && MaximumPersistenceRuleCount <= maximum.MaximumPersistenceRuleCount
            && MaximumPersistenceGpuCount <= maximum.MaximumPersistenceGpuCount
            && SourceIndexCapacity <= maximum.SourceIndexCapacity
            && MetricIndexCapacity <= maximum.MetricIndexCapacity
            && RuleIndexCapacity <= maximum.RuleIndexCapacity
            && GpuIndexCapacity <= maximum.GpuIndexCapacity
            && GpuLuidIndexCapacity <= maximum.GpuLuidIndexCapacity
            && GpuKeyIndexCapacity <= maximum.GpuKeyIndexCapacity
            && MaximumPlanMetricCount <= maximum.MaximumPlanMetricCount
            && MaximumSourceModeCount <= maximum.MaximumSourceModeCount
            && MaximumSourcePlanCount <= maximum.MaximumSourcePlanCount
            && MaximumMetricPlanCount <= maximum.MaximumMetricPlanCount
            && ResidentByteBudget <= maximum.ResidentByteBudget;

    public static CompiledHostManagerMetricSnapshotCapacityPlan Unpublished { get; } =
        new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

    private static bool IsValidIndexCapacity(uint capacity, uint itemCount)
        => capacity >= itemCount
            && capacity > 0
            && (capacity & (capacity - 1)) == 0;
}

public sealed record CompiledHostManagerMetricSnapshotBuildPlan(
    uint AbiVersion,
    string NativeModule,
    CompiledHostManagerMetricSnapshotCapacityPlan CapacityLimits)
{
    public bool IsPublished => AbiVersion == 0x0003_0000U
        && string.Equals(NativeModule, "metric_snapshot", StringComparison.Ordinal)
        && CapacityLimits.IsPublished;

    public static CompiledHostManagerMetricSnapshotBuildPlan Unpublished { get; } =
        new(0, string.Empty, CompiledHostManagerMetricSnapshotCapacityPlan.Unpublished);
}

public sealed record CompiledHostManagerMetricSnapshotRecreatePlan(
    CompiledHostManagerMetricSnapshotCapacityPlan Capacity,
    string PersistenceRelativePath,
    uint CatalogContractVersion,
    uint ValueContractVersion,
    uint ObservationContractVersion,
    uint InventoryContractVersion,
    uint PersistenceContractVersion,
    uint CpuCounterContractVersion)
{
    private const uint ContractVersionV1 = 0x0001_0000U;
    private const uint PersistenceContractVersionV2 = 0x0002_0000U;

    public bool IsPublished => Capacity.IsPublished
        && !string.IsNullOrWhiteSpace(PersistenceRelativePath)
        && CatalogContractVersion == ContractVersionV1
        && ValueContractVersion == ContractVersionV1
        && ObservationContractVersion == ContractVersionV1
        && InventoryContractVersion == ContractVersionV1
        && PersistenceContractVersion == PersistenceContractVersionV2
        && CpuCounterContractVersion == ContractVersionV1;

    public static CompiledHostManagerMetricSnapshotRecreatePlan Unpublished { get; } =
        new(CompiledHostManagerMetricSnapshotCapacityPlan.Unpublished, string.Empty, 0, 0, 0, 0, 0, 0);
}

public sealed record CompiledHostManagerMetricSnapshotSourcePolicy(
    string SourceId,
    ulong SourceHandle,
    uint SourceRole,
    uint Priority,
    uint RetentionPolicy,
    bool Required,
    ulong CapabilityMask,
    ulong SemanticFingerprint)
{
    public bool IsPublished => !string.IsNullOrWhiteSpace(SourceId)
        && SourceHandle > 0
        && SourceRole is 1 or 2
        && Priority > 0
        && RetentionPolicy is 1 or 2
        && CapabilityMask > 0
        && SemanticFingerprint > 0;
}

public sealed record CompiledHostManagerMetricSnapshotRuleTemplate(
    string TemplateId,
    string MetricIdTemplate,
    string SourceId,
    ulong SourceHandle,
    uint SourcePriority,
    uint ScopeExpansion,
    uint MetricKind,
    uint ScopeKind,
    uint ValueKind,
    uint RetentionPolicy,
    ulong CapabilityMask,
    uint MetricFlags,
    ulong MinimumValueBits,
    ulong MaximumValueBits,
    uint GpuVendorMask,
    uint ApplicabilityMask,
    uint MaximumInstanceCount,
    ulong SemanticFingerprint)
{
    public bool IsPublished => !string.IsNullOrWhiteSpace(TemplateId)
        && !string.IsNullOrWhiteSpace(MetricIdTemplate)
        && !string.IsNullOrWhiteSpace(SourceId)
        && SourceHandle > 0
        && SourcePriority > 0
        && ScopeExpansion is >= 1 and <= 7
        && MetricKind is >= 1 and <= 13
        && ScopeKind is >= 1 and <= 4
        && ValueKind is >= 1 and <= 3
        && RetentionPolicy is 1 or 2
        && CapabilityMask > 0
        && (MetricFlags & ~0x0FU) == 0
        && GpuVendorMask <= 0x0FU
        && ApplicabilityMask <= 0x01U
        && MaximumInstanceCount is > 0 and <= int.MaxValue
        && SemanticFingerprint > 0
        && (MetricFlags & 0x0CU) != 0x0CU
        && (ScopeExpansion == 4
            ? GpuVendorMask != 0
            : GpuVendorMask == 0 && ApplicabilityMask == 0)
        && (ScopeExpansion is 1 or 2 or 3 or 7
            ? MaximumInstanceCount == 1
            : true);
}

public sealed record CompiledHostManagerMetricSnapshotHotPublishPlan(
    ulong ConfigurationGeneration,
    ulong CatalogGenerationBase,
    uint CatalogManifestVersion,
    string CatalogManifestSha256,
    ulong MaximumFutureSkewMilliseconds,
    ulong ResidentByteBudget,
    ImmutableArray<CompiledHostManagerMetricSnapshotSourcePolicy> SourcePolicies,
    ImmutableArray<CompiledHostManagerMetricSnapshotRuleTemplate> RuleTemplates)
{
    public bool IsPublished => ConfigurationGeneration > 0
        && CatalogGenerationBase == (ConfigurationGeneration | 1UL)
        && (ConfigurationGeneration & uint.MaxValue) == 0
        && CatalogManifestVersion == 1
        && IsSha256(CatalogManifestSha256)
        && MaximumFutureSkewMilliseconds > 0
        && ResidentByteBudget > 0
        && !SourcePolicies.IsDefaultOrEmpty
        && !RuleTemplates.IsDefaultOrEmpty
        && SourcePolicies.All(static policy => policy.IsPublished)
        && RuleTemplates.All(static template => template.IsPublished)
        && SourcePolicies.SequenceEqual(
            SourcePolicies.OrderBy(static policy => policy.SourceHandle))
        && RuleTemplates.SequenceEqual(
            RuleTemplates.OrderBy(
                static template => template.TemplateId,
                StringComparer.Ordinal))
        && SourcePolicies.Select(static policy => policy.SourceId)
            .Distinct(StringComparer.Ordinal)
            .Count() == SourcePolicies.Length
        && SourcePolicies.Select(static policy => policy.SourceHandle)
            .Distinct()
            .Count() == SourcePolicies.Length
        && SourcePolicies.Count(static policy => policy.SourceRole == 2) == 1
        && RuleTemplates.Select(static template => template.TemplateId)
            .Distinct(StringComparer.Ordinal)
            .Count() == RuleTemplates.Length
        && RuleTemplates.All(template =>
            SourcePolicies.Any(policy =>
                string.Equals(
                    policy.SourceId,
                    template.SourceId,
                    StringComparison.Ordinal)
                && policy.SourceHandle == template.SourceHandle
                && policy.Priority == template.SourcePriority
                && policy.SourceRole == 1
                && (template.CapabilityMask & ~policy.CapabilityMask) == 0));

    public static CompiledHostManagerMetricSnapshotHotPublishPlan Unpublished { get; } =
        new(0, 0, 0, string.Empty, 0, 0, [], []);

    private static bool IsSha256(string value)
        => value.Length == 64
            && value.All(static character => char.IsAsciiHexDigit(character));
}

public sealed record CompiledHostManagerMetricSnapshotPlan(
    CompiledHostManagerMetricSnapshotBuildPlan Build,
    CompiledHostManagerMetricSnapshotRecreatePlan Recreate,
    CompiledHostManagerMetricSnapshotHotPublishPlan HotPublish,
    string RecreateSha256,
    string HotPublishSha256)
{
    public bool IsPublished => Build.IsPublished
        && Recreate.IsPublished
        && HotPublish.IsPublished
        && Recreate.Capacity.FitsWithin(Build.CapacityLimits)
        && HotPublish.SourcePolicies.Length <= Recreate.Capacity.MaximumSourceCount
        && HotPublish.RuleTemplates.Length <= Recreate.Capacity.MaximumRuleCount
        && HotPublish.ResidentByteBudget <= Recreate.Capacity.ResidentByteBudget
        && IsSha256(RecreateSha256)
        && IsSha256(HotPublishSha256);

    public static CompiledHostManagerMetricSnapshotPlan Unpublished { get; } =
        new(
            CompiledHostManagerMetricSnapshotBuildPlan.Unpublished,
            CompiledHostManagerMetricSnapshotRecreatePlan.Unpublished,
            CompiledHostManagerMetricSnapshotHotPublishPlan.Unpublished,
            string.Empty,
            string.Empty);

    private static bool IsSha256(string value)
        => value.Length == 64
            && value.All(static character => char.IsAsciiHexDigit(character));
}
