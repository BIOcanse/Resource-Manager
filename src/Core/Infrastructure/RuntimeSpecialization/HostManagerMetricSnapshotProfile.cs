using System.Text.Json.Serialization;

namespace ResourceManager.App.Infrastructure.RuntimeSpecialization;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class HostManagerMetricSnapshotCapacityProfile
{
    [JsonPropertyName("maximum_source_count")]
    public required uint MaximumSourceCount { get; init; }
    [JsonPropertyName("maximum_metric_count")]
    public required uint MaximumMetricCount { get; init; }
    [JsonPropertyName("maximum_rule_count")]
    public required uint MaximumRuleCount { get; init; }
    [JsonPropertyName("maximum_requested_count")]
    public required uint MaximumRequestedCount { get; init; }
    [JsonPropertyName("maximum_observation_count")]
    public required uint MaximumObservationCount { get; init; }
    [JsonPropertyName("maximum_gpu_adapter_count")]
    public required uint MaximumGpuAdapterCount { get; init; }
    [JsonPropertyName("maximum_persistence_source_count")]
    public required uint MaximumPersistenceSourceCount { get; init; }
    [JsonPropertyName("maximum_persistence_rule_count")]
    public required uint MaximumPersistenceRuleCount { get; init; }
    [JsonPropertyName("maximum_persistence_gpu_count")]
    public required uint MaximumPersistenceGpuCount { get; init; }
    [JsonPropertyName("source_index_capacity")]
    public required uint SourceIndexCapacity { get; init; }
    [JsonPropertyName("metric_index_capacity")]
    public required uint MetricIndexCapacity { get; init; }
    [JsonPropertyName("rule_index_capacity")]
    public required uint RuleIndexCapacity { get; init; }
    [JsonPropertyName("gpu_index_capacity")]
    public required uint GpuIndexCapacity { get; init; }
    [JsonPropertyName("gpu_luid_index_capacity")]
    public required uint GpuLuidIndexCapacity { get; init; }
    [JsonPropertyName("gpu_key_index_capacity")]
    public required uint GpuKeyIndexCapacity { get; init; }
    [JsonPropertyName("maximum_plan_metric_count")]
    public required uint MaximumPlanMetricCount { get; init; }
    [JsonPropertyName("maximum_source_mode_count")]
    public required uint MaximumSourceModeCount { get; init; }
    [JsonPropertyName("maximum_source_plan_count")]
    public required uint MaximumSourcePlanCount { get; init; }
    [JsonPropertyName("maximum_metric_plan_count")]
    public required uint MaximumMetricPlanCount { get; init; }
    [JsonPropertyName("resident_byte_budget")]
    public required ulong ResidentByteBudget { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class HostManagerMetricSnapshotRecreateProfile
{
    [JsonPropertyName("capacity")]
    public required HostManagerMetricSnapshotCapacityProfile Capacity { get; init; }
    [JsonPropertyName("persistence_relative_path")]
    public required string PersistenceRelativePath { get; init; }
    [JsonPropertyName("catalog_contract_version")]
    public required uint CatalogContractVersion { get; init; }
    [JsonPropertyName("value_contract_version")]
    public required uint ValueContractVersion { get; init; }
    [JsonPropertyName("observation_contract_version")]
    public required uint ObservationContractVersion { get; init; }
    [JsonPropertyName("inventory_contract_version")]
    public required uint InventoryContractVersion { get; init; }
    [JsonPropertyName("persistence_contract_version")]
    public required uint PersistenceContractVersion { get; init; }
    [JsonPropertyName("cpu_counter_contract_version")]
    public required uint CpuCounterContractVersion { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class HostManagerMetricSnapshotSourcePolicyProfile
{
    [JsonPropertyName("source_id")]
    public required string SourceId { get; init; }
    [JsonPropertyName("source_handle")]
    public required ulong SourceHandle { get; init; }
    [JsonPropertyName("source_role")]
    public required string SourceRole { get; init; }
    [JsonPropertyName("priority")]
    public required uint Priority { get; init; }
    [JsonPropertyName("retention")]
    public required string Retention { get; init; }
    [JsonPropertyName("required")]
    public required bool Required { get; init; }
    [JsonPropertyName("capability_mask")]
    public required ulong CapabilityMask { get; init; }
    [JsonPropertyName("semantic_fingerprint")]
    public required ulong SemanticFingerprint { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class HostManagerMetricSnapshotRuleTemplateProfile
{
    [JsonPropertyName("template_id")]
    public required string TemplateId { get; init; }
    [JsonPropertyName("metric_id_template")]
    public required string MetricIdTemplate { get; init; }
    [JsonPropertyName("source_id")]
    public required string SourceId { get; init; }
    [JsonPropertyName("scope_expansion")]
    public required string ScopeExpansion { get; init; }
    [JsonPropertyName("metric_kind")]
    public required string MetricKind { get; init; }
    [JsonPropertyName("value_kind")]
    public required string ValueKind { get; init; }
    [JsonPropertyName("retention")]
    public required string Retention { get; init; }
    [JsonPropertyName("capability_mask")]
    public required ulong CapabilityMask { get; init; }
    [JsonPropertyName("metric_flags")]
    public required string[] MetricFlags { get; init; }
    [JsonPropertyName("minimum_value_bits")]
    public required ulong MinimumValueBits { get; init; }
    [JsonPropertyName("maximum_value_bits")]
    public required ulong MaximumValueBits { get; init; }
    [JsonPropertyName("gpu_vendor_mask")]
    public required uint GpuVendorMask { get; init; }
    [JsonPropertyName("applicability_mask")]
    public required uint ApplicabilityMask { get; init; }
    [JsonPropertyName("maximum_instance_count")]
    public required uint MaximumInstanceCount { get; init; }
    [JsonPropertyName("semantic_fingerprint")]
    public required ulong SemanticFingerprint { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class HostManagerMetricSnapshotHotPublishProfile
{
    [JsonPropertyName("catalog_manifest_version")]
    public required uint CatalogManifestVersion { get; init; }
    [JsonPropertyName("maximum_future_skew_ms")]
    public required ulong MaximumFutureSkewMilliseconds { get; init; }
    [JsonPropertyName("resident_byte_budget")]
    public required ulong ResidentByteBudget { get; init; }
    [JsonPropertyName("source_policies")]
    public required HostManagerMetricSnapshotSourcePolicyProfile[] SourcePolicies { get; init; }
    [JsonPropertyName("rule_templates")]
    public required HostManagerMetricSnapshotRuleTemplateProfile[] RuleTemplates { get; init; }
}
