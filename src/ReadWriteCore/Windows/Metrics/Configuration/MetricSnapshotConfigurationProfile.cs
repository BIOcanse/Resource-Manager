using System.Text.Json.Serialization;
namespace ResourceManager.ReadWrite.Windows.Metrics.Configuration;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class MetricSnapshotConfigurationProfile
{
    [JsonPropertyName("profile_revision")] public required int ProfileRevision { get; init; }
    [JsonPropertyName("abi_version")] public required uint AbiVersion { get; init; }
    [JsonPropertyName("build_capacity")] public required MetricSnapshotCapacityProfile BuildCapacity { get; init; }
    [JsonPropertyName("recreate")] public required MetricSnapshotRecreateProfile Recreate { get; init; }
    [JsonPropertyName("hot_publish")] public required MetricSnapshotHotPublishProfile HotPublish { get; init; }
    [JsonPropertyName("pdh")] public required MetricPdhConfigurationProfile Pdh { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class MetricPdhConfigurationProfile
{
    [JsonPropertyName("abi_version")] public required uint AbiVersion { get; init; }
    [JsonPropertyName("baseline_reset_interval_ms")] public required int BaselineResetIntervalMilliseconds { get; init; }
    [JsonPropertyName("frame_reuse_window_ms")] public required int FrameReuseWindowMilliseconds { get; init; }
    [JsonPropertyName("last_good_lifetime_ms")] public required int LastGoodLifetimeMilliseconds { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class MetricSnapshotCapacityProfile
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
public class MetricSnapshotRecreateProfile
{
    [JsonPropertyOrder(0)]
    [JsonPropertyName("capacity")]
    public required MetricSnapshotCapacityProfile Capacity { get; init; }
    [JsonPropertyOrder(2)]
    [JsonPropertyName("catalog_contract_version")]
    public required uint CatalogContractVersion { get; init; }
    [JsonPropertyOrder(3)]
    [JsonPropertyName("value_contract_version")]
    public required uint ValueContractVersion { get; init; }
    [JsonPropertyOrder(4)]
    [JsonPropertyName("observation_contract_version")]
    public required uint ObservationContractVersion { get; init; }
    [JsonPropertyOrder(5)]
    [JsonPropertyName("inventory_contract_version")]
    public required uint InventoryContractVersion { get; init; }
    [JsonPropertyOrder(6)]
    [JsonPropertyName("persistence_contract_version")]
    public required uint PersistenceContractVersion { get; init; }
    [JsonPropertyOrder(7)]
    [JsonPropertyName("cpu_counter_contract_version")]
    public required uint CpuCounterContractVersion { get; init; }
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class MetricSnapshotSourcePolicyProfile
{
    [JsonPropertyName("source_id")]
    public required string SourceId { get; init; }
    [JsonPropertyName("source_handle")]
    public required ulong SourceHandle { get; init; }
    [JsonPropertyName("source_role")]
    public required string SourceRole { get; init; }
    [JsonPropertyName("priority")]
    public required uint Priority { get; init; }
    [JsonPropertyName("required")]
    public required bool Required { get; init; }
    [JsonPropertyName("capability_mask")]
    public required ulong CapabilityMask { get; init; }
    [JsonPropertyName("semantic_fingerprint")]
    public required ulong SemanticFingerprint { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class MetricSnapshotRuleTemplateProfile
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
public sealed class MetricSnapshotHotPublishProfile
{
    [JsonPropertyName("catalog_manifest_version")]
    public required uint CatalogManifestVersion { get; init; }
    [JsonPropertyName("maximum_future_skew_ms")]
    public required ulong MaximumFutureSkewMilliseconds { get; init; }
    [JsonPropertyName("resident_byte_budget")]
    public required ulong ResidentByteBudget { get; init; }
    [JsonPropertyName("source_policies")]
    public required MetricSnapshotSourcePolicyProfile[] SourcePolicies { get; init; }
    [JsonPropertyName("rule_templates")]
    public required MetricSnapshotRuleTemplateProfile[] RuleTemplates { get; init; }
}
