using System.Collections.Immutable;
using ResourceManager.App.Infrastructure.Monitoring;
namespace ResourceManager.ReadWrite.Windows.Metrics.Configuration;

public sealed record MetricSnapshotCapacity(
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

    public bool FitsWithin(MetricSnapshotCapacity maximum)
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

    public static MetricSnapshotCapacity Unpublished { get; } =
        new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

    private static bool IsValidIndexCapacity(uint capacity, uint itemCount)
        => capacity >= itemCount
            && capacity > 0
            && (capacity & (capacity - 1)) == 0;
}

public sealed record MetricSnapshotRecreateConfiguration(MetricSnapshotCapacity Capacity,
    uint CatalogContractVersion, uint ValueContractVersion, uint ObservationContractVersion,
    uint InventoryContractVersion, uint PersistenceContractVersion, uint CpuCounterContractVersion);

public sealed record MetricSnapshotPolicyConfiguration(
    ulong ConfigurationGeneration,
    ulong CatalogGenerationBase,
    uint CatalogManifestVersion,
    string CatalogManifestSha256,
    ulong MaximumFutureSkewMilliseconds,
    ulong ResidentByteBudget,
    ImmutableArray<NativeMetricSnapshotSourcePolicy> SourcePolicies,
    ImmutableArray<NativeMetricSnapshotRuleTemplate> RuleTemplates)
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

    public static MetricSnapshotPolicyConfiguration Unpublished { get; } =
        new(0, 0, 0, string.Empty, 0, 0, [], []);

    private static bool IsSha256(string value)
        => value.Length == 64
            && value.All(static character => char.IsAsciiHexDigit(character));
}

