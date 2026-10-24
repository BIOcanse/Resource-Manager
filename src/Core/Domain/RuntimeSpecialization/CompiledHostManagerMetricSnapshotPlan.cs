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
    public bool IsPublished => AbiVersion == 0x000B_0000U
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
    private const uint ContractVersionV2 = 0x0002_0000U;
    private const uint ContractVersionV4 = 0x0004_0000U;
    private const uint ContractVersionV5 = 0x0005_0000U;
    private const uint ContractVersionV6 = 0x0006_0000U;
    private const uint ContractVersionV9 = 0x0009_0000U;

    public bool IsPublished => Capacity.IsPublished
        && !string.IsNullOrWhiteSpace(PersistenceRelativePath)
        && CatalogContractVersion == ContractVersionV6
        && ValueContractVersion == ContractVersionV4
        && ObservationContractVersion == ContractVersionV9
        && InventoryContractVersion == ContractVersionV2
        && PersistenceContractVersion == ContractVersionV5
        && CpuCounterContractVersion == ContractVersionV1;

    public static CompiledHostManagerMetricSnapshotRecreatePlan Unpublished { get; } =
        new(CompiledHostManagerMetricSnapshotCapacityPlan.Unpublished, string.Empty, 0, 0, 0, 0, 0, 0);
}

public sealed record CompiledHostManagerMetricSnapshotSourcePolicy(
    string SourceId,
    ulong SourceHandle,
    uint SourceRole,
    uint Priority,
    bool Required,
    ulong CapabilityMask,
    ulong SemanticFingerprint)
{
    public bool IsPublished => !string.IsNullOrWhiteSpace(SourceId)
        && SourceHandle > 0
        && SourceRole is 1 or 2
        && Priority > 0
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
        && ScopeExpansion is >= 1 and <= 17
        && MetricKind is >= 1 and <= 19
        && ScopeKind is >= 1 and <= 4
        && ValueKind is >= 1 and <= 6
        && (MetricKind is not (14 or 15 or 16 or 17 or 18 or 19) || ValueKind == 1)
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
        && (ScopeExpansion is 1 or 2 or 3 or 7 or 15
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
    public bool IsPublished
    {
        get
        {
            if (ConfigurationGeneration == 0
                || CatalogGenerationBase != (ConfigurationGeneration | 1UL)
                || (ConfigurationGeneration & uint.MaxValue) != 0
                || CatalogManifestVersion != 1
                || !IsSha256(CatalogManifestSha256)
                || MaximumFutureSkewMilliseconds == 0 || ResidentByteBudget == 0
                || SourcePolicies.IsDefaultOrEmpty || RuleTemplates.IsDefaultOrEmpty) return false;

            var inventorySourceCount = 0;
            for (var index = 0; index < SourcePolicies.Length; index++)
            {
                var policy = SourcePolicies[index];
                if (!policy.IsPublished || index > 0 && SourcePolicies[index - 1].SourceHandle >= policy.SourceHandle)
                    return false;
                if (policy.SourceRole == 2) inventorySourceCount++;
                for (var prior = 0; prior < index; prior++)
                    if (string.Equals(SourcePolicies[prior].SourceId, policy.SourceId, StringComparison.Ordinal))
                        return false;
            }
            if (inventorySourceCount != 1) return false;

            for (var index = 0; index < RuleTemplates.Length; index++)
            {
                var template = RuleTemplates[index];
                if (!template.IsPublished || index > 0 && StringComparer.Ordinal.Compare(
                        RuleTemplates[index - 1].TemplateId, template.TemplateId) >= 0) return false;
                var matched = false;
                foreach (var policy in SourcePolicies)
                {
                    if (policy.SourceHandle != template.SourceHandle) continue;
                    matched = string.Equals(policy.SourceId, template.SourceId, StringComparison.Ordinal)
                        && policy.Priority == template.SourcePriority && policy.SourceRole == 1
                        && (template.CapabilityMask & ~policy.CapabilityMask) == 0;
                    break;
                }
                if (!matched) return false;
            }
            return true;
        }
    }

    public static CompiledHostManagerMetricSnapshotHotPublishPlan Unpublished { get; } =
        new(0, 0, 0, string.Empty, 0, 0, [], []);

    private static bool IsSha256(string value)
    {
        if (value.Length != 64) return false;
        foreach (var character in value)
            if (!char.IsAsciiHexDigit(character)) return false;
        return true;
    }
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
