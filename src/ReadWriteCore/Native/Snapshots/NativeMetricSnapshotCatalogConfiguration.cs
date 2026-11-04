using System.Collections.Immutable;
namespace ResourceManager.App.Infrastructure.Monitoring;

// Complete canonical catalog input; host publication and deployment policy remain with the caller.
public sealed record NativeMetricSnapshotCatalogConfiguration(
    ulong CatalogGenerationBase, string CatalogManifestSha256,
    uint MaximumGpuAdapterCount, uint MaximumRuleCount, uint MaximumMetricCount,
    ImmutableArray<NativeMetricSnapshotSourcePolicy> SourcePolicies,
    ImmutableArray<NativeMetricSnapshotRuleTemplate> RuleTemplates)
{
    public bool IsPublished => CatalogGenerationBase > 0
        && CatalogManifestSha256 is { Length: 64 } && CatalogManifestSha256.All(char.IsAsciiHexDigit)
        && MaximumRuleCount > 0 && MaximumMetricCount > 0
        && !SourcePolicies.IsDefaultOrEmpty && SourcePolicies.All(static row => row.IsPublished)
        && !RuleTemplates.IsDefaultOrEmpty && RuleTemplates.All(static row => row.IsPublished)
        && SourcePolicies.SequenceEqual(SourcePolicies.OrderBy(static row => row.SourceHandle))
        && RuleTemplates.SequenceEqual(RuleTemplates.OrderBy(static row => row.TemplateId, StringComparer.Ordinal))
        && SourcePolicies.Select(static row => row.SourceId).Distinct(StringComparer.Ordinal).Count() == SourcePolicies.Length
        && SourcePolicies.Select(static row => row.SourceHandle).Distinct().Count() == SourcePolicies.Length
        && SourcePolicies.Count(static row => row.SourceRole == 2) == 1
        && RuleTemplates.Select(static row => row.TemplateId).Distinct(StringComparer.Ordinal).Count() == RuleTemplates.Length
        && RuleTemplates.All(template => SourcePolicies.Any(policy => policy.SourceId == template.SourceId
            && policy.SourceHandle == template.SourceHandle && policy.Priority == template.SourcePriority
            && policy.SourceRole == 1 && (template.CapabilityMask & ~policy.CapabilityMask) == 0));
}

public sealed record NativeMetricSnapshotSourcePolicy(
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

public sealed record NativeMetricSnapshotRuleTemplate(
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

