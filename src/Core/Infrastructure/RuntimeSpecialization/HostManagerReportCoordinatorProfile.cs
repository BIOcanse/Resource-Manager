using System.Text.Json.Serialization;
using ResourceManager.App.Domain.RuntimeSpecialization;

namespace ResourceManager.App.Infrastructure.RuntimeSpecialization;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class HostManagerReportCoordinatorCapacityProfile
{
    [JsonPropertyName("maximum_source_count")]
    public required int MaximumSourceCount { get; init; }

    [JsonPropertyName("maximum_rule_count")]
    public required int MaximumRuleCount { get; init; }

    [JsonPropertyName("maximum_observation_count")]
    public required int MaximumObservationCount { get; init; }

    [JsonPropertyName("maximum_report_count")]
    public required int MaximumReportCount { get; init; }

    [JsonPropertyName("maximum_trust_count")]
    public required int MaximumTrustCount { get; init; }

    [JsonPropertyName("maximum_bucket_count")]
    public required int MaximumBucketCount { get; init; }

    [JsonPropertyName("maximum_persistence_operation_count")]
    public required int MaximumPersistenceOperationCount { get; init; }

    [JsonPropertyName("maximum_report_output_count")]
    public required int MaximumReportOutputCount { get; init; }

    [JsonPropertyName("maximum_rolling_observation_count")]
    public required int MaximumRollingObservationCount { get; init; }

    [JsonPropertyName("source_index_capacity")]
    public required int SourceIndexCapacity { get; init; }

    [JsonPropertyName("rule_index_capacity")]
    public required int RuleIndexCapacity { get; init; }

    [JsonPropertyName("observation_index_capacity")]
    public required int ObservationIndexCapacity { get; init; }

    [JsonPropertyName("report_index_capacity")]
    public required int ReportIndexCapacity { get; init; }

    [JsonPropertyName("trust_index_capacity")]
    public required int TrustIndexCapacity { get; init; }

    [JsonPropertyName("bucket_index_capacity")]
    public required int BucketIndexCapacity { get; init; }

    [JsonPropertyName("planned_persistence_index_capacity")]
    public required int PlannedPersistenceIndexCapacity { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class HostManagerReportCoordinatorRecreateProfile
{
    [JsonPropertyName("capacity")]
    public required HostManagerReportCoordinatorCapacityProfile Capacity { get; init; }

    [JsonPropertyName("rules")]
    public required IReadOnlyList<HostManagerReportCoordinatorRuleProfile> Rules { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class HostManagerReportCoordinatorRuleProfile
{
    [JsonPropertyName("fact_kind")]
    public required HostManagerReportFactKind FactKind { get; init; }

    [JsonPropertyName("rule_handle")]
    public required ulong RuleHandle { get; init; }

    [JsonPropertyName("source_handle")]
    public required ulong SourceHandle { get; init; }

    [JsonPropertyName("coverage_scope_handle")]
    public required ulong CoverageScopeHandle { get; init; }

    [JsonPropertyName("family_handle")]
    public required ulong FamilyHandle { get; init; }

    [JsonPropertyName("report_type_handle")]
    public required ulong ReportTypeHandle { get; init; }

    [JsonPropertyName("resource_kind_handle")]
    public required ulong ResourceKindHandle { get; init; }

    [JsonPropertyName("payload_handle")]
    public required ulong PayloadHandle { get; init; }

    [JsonPropertyName("metric_selector")]
    public required uint MetricSelector { get; init; }

    [JsonPropertyName("comparison")]
    public required uint Comparison { get; init; }

    [JsonPropertyName("priority")]
    public required uint Priority { get; init; }

    [JsonPropertyName("severity")]
    public required uint Severity { get; init; }

    [JsonPropertyName("required_consecutive_hits")]
    public required uint RequiredConsecutiveHits { get; init; }

    [JsonPropertyName("required_consecutive_misses")]
    public required uint RequiredConsecutiveMisses { get; init; }

    [JsonPropertyName("minimum_sample_duration_ms")]
    public required long MinimumSampleDurationMilliseconds { get; init; }

    [JsonPropertyName("activation_threshold")]
    public required double ActivationThreshold { get; init; }

    [JsonPropertyName("clear_threshold")]
    public required double ClearThreshold { get; init; }

    [JsonPropertyName("stale_after_ms")]
    public required long StaleAfterMilliseconds { get; init; }

    [JsonPropertyName("retention_ms")]
    public required long RetentionMilliseconds { get; init; }

    [JsonPropertyName("rolling")]
    public required bool Rolling { get; init; }

    [JsonPropertyName("predicate_group_handle")]
    public required ulong PredicateGroupHandle { get; init; }

    [JsonPropertyName("predicate_index")]
    public required uint PredicateIndex { get; init; }

    [JsonPropertyName("predicate_count")]
    public required uint PredicateCount { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class HostManagerReportCoordinatorHotPublishProfile
{
    [JsonPropertyName("bucket_width_ms")]
    public required long BucketWidthMilliseconds { get; init; }

    [JsonPropertyName("window_24h_ms")]
    public required long Window24HoursMilliseconds { get; init; }

    [JsonPropertyName("window_7d_ms")]
    public required long Window7DaysMilliseconds { get; init; }

    [JsonPropertyName("maximum_future_skew_ms")]
    public required long MaximumFutureSkewMilliseconds { get; init; }

    [JsonPropertyName("default_stale_after_ms")]
    public required long DefaultStaleAfterMilliseconds { get; init; }

    [JsonPropertyName("default_retention_ms")]
    public required long DefaultRetentionMilliseconds { get; init; }

    [JsonPropertyName("metadata_checkpoint_interval_ms")]
    public required long MetadataCheckpointIntervalMilliseconds { get; init; }

    [JsonPropertyName("resident_byte_budget")]
    public required long ResidentByteBudget { get; init; }
}
