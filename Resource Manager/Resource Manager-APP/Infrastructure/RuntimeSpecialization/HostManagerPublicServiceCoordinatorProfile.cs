using System.Text.Json.Serialization;

namespace ResourceManager.App.Infrastructure.RuntimeSpecialization;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class HostManagerPublicServiceCoordinatorCapacityProfile
{
    [JsonPropertyName("maximum_capability_count")]
    public required int MaximumCapabilityCount { get; init; }

    [JsonPropertyName("maximum_route_count")]
    public required int MaximumRouteCount { get; init; }

    [JsonPropertyName("maximum_model_count")]
    public required int MaximumModelCount { get; init; }

    [JsonPropertyName("maximum_model_alias_count")]
    public required int MaximumModelAliasCount { get; init; }

    [JsonPropertyName("maximum_request_count")]
    public required int MaximumRequestCount { get; init; }

    [JsonPropertyName("maximum_rate_bucket_count")]
    public required int MaximumRateBucketCount { get; init; }

    [JsonPropertyName("maximum_lease_count")]
    public required int MaximumLeaseCount { get; init; }

    [JsonPropertyName("maximum_subscription_count")]
    public required int MaximumSubscriptionCount { get; init; }

    [JsonPropertyName("maximum_task_count")]
    public required int MaximumTaskCount { get; init; }

    [JsonPropertyName("capability_index_capacity")]
    public required int CapabilityIndexCapacity { get; init; }

    [JsonPropertyName("model_index_capacity")]
    public required int ModelIndexCapacity { get; init; }

    [JsonPropertyName("alias_index_capacity")]
    public required int AliasIndexCapacity { get; init; }

    [JsonPropertyName("request_index_capacity")]
    public required int RequestIndexCapacity { get; init; }

    [JsonPropertyName("rate_bucket_index_capacity")]
    public required int RateBucketIndexCapacity { get; init; }

    [JsonPropertyName("lease_index_capacity")]
    public required int LeaseIndexCapacity { get; init; }

    [JsonPropertyName("subscription_index_capacity")]
    public required int SubscriptionIndexCapacity { get; init; }

    [JsonPropertyName("task_index_capacity")]
    public required int TaskIndexCapacity { get; init; }

    [JsonPropertyName("maximum_catalog_text_bytes")]
    public required int MaximumCatalogTextBytes { get; init; }

    [JsonPropertyName("maximum_model_text_bytes")]
    public required int MaximumModelTextBytes { get; init; }

    [JsonPropertyName("resident_byte_budget")]
    public required long ResidentByteBudget { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class HostManagerPublicServiceCoordinatorRecreateProfile
{
    [JsonPropertyName("capacity")]
    public required HostManagerPublicServiceCoordinatorCapacityProfile Capacity { get; init; }

    [JsonPropertyName("maximum_concurrent_model_tasks")]
    public required int MaximumConcurrentModelTasks { get; init; }

    [JsonPropertyName("maximum_requests_per_rate_window")]
    public required int MaximumRequestsPerRateWindow { get; init; }

    [JsonPropertyName("maximum_inflight_requests_per_caller")]
    public required int MaximumInflightRequestsPerCaller { get; init; }

    [JsonPropertyName("retryable_task_outcomes")]
    public required string[] RetryableTaskOutcomes { get; init; }

    [JsonPropertyName("maximum_task_attempt_count")]
    public required int MaximumTaskAttemptCount { get; init; }

    [JsonPropertyName("retryable_http_status_policies")]
    public required string[] RetryableHttpStatusPolicies { get; init; }

    [JsonPropertyName("rate_window_ms")]
    public required long RateWindowMilliseconds { get; init; }

    [JsonPropertyName("request_timeout_ms")]
    public required long RequestTimeoutMilliseconds { get; init; }

    [JsonPropertyName("lease_timeout_ms")]
    public required long LeaseTimeoutMilliseconds { get; init; }

    [JsonPropertyName("subscription_timeout_ms")]
    public required long SubscriptionTimeoutMilliseconds { get; init; }

    [JsonPropertyName("task_timeout_ms")]
    public required long TaskTimeoutMilliseconds { get; init; }

    [JsonPropertyName("retry_delay_ms")]
    public required long RetryDelayMilliseconds { get; init; }

    [JsonPropertyName("model_catalog_acquisition_interval_ms")]
    public required long ModelCatalogAcquisitionIntervalMilliseconds { get; init; }

    [JsonPropertyName("model_catalog_last_good_lifetime_ms")]
    public required long ModelCatalogLastGoodLifetimeMilliseconds { get; init; }

    [JsonPropertyName("model_catalog_acquisition_timeout_ms")]
    public required long ModelCatalogAcquisitionTimeoutMilliseconds { get; init; }

    [JsonPropertyName("loopback_only")]
    public required bool LoopbackOnly { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class HostManagerPublicServiceCapabilityProfile
{
    [JsonPropertyName("handle")]
    public required ulong Handle { get; init; }

    [JsonPropertyName("payload_handle")]
    public required ulong PayloadHandle { get; init; }

    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("display_name")]
    public required string DisplayName { get; init; }

    [JsonPropertyName("description")]
    public required string Description { get; init; }

    [JsonPropertyName("setting")]
    public required string Setting { get; init; }

    [JsonPropertyName("available")]
    public required bool Available { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class HostManagerPublicServiceRouteProfile
{
    [JsonPropertyName("handle")]
    public required ulong Handle { get; init; }

    [JsonPropertyName("capability_handle")]
    public required ulong CapabilityHandle { get; init; }

    [JsonPropertyName("payload_handle")]
    public required ulong PayloadHandle { get; init; }

    [JsonPropertyName("path")]
    public required string Path { get; init; }

    [JsonPropertyName("methods")]
    public required string[] Methods { get; init; }

    [JsonPropertyName("exact_path")]
    public required bool ExactPath { get; init; }

    [JsonPropertyName("catalog_route")]
    public required bool CatalogRoute { get; init; }

    [JsonPropertyName("bypass_rate_limit")]
    public required bool BypassRateLimit { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class HostManagerPublicServiceCoordinatorHotPublishProfile
{
    [JsonPropertyName("service_name")]
    public required string ServiceName { get; init; }

    [JsonPropertyName("api_version")]
    public required string ApiVersion { get; init; }

    [JsonPropertyName("base_path")]
    public required string BasePath { get; init; }

    [JsonPropertyName("anonymous_caller_handle")]
    public required ulong AnonymousCallerHandle { get; init; }

    [JsonPropertyName("ai_provider_handle")]
    public required ulong AiProviderHandle { get; init; }

    [JsonPropertyName("model_alias_projection_contract_version")]
    public required uint ModelAliasProjectionContractVersion { get; init; }

    [JsonPropertyName("load_task_base_score")]
    public required long LoadTaskBaseScore { get; init; }

    [JsonPropertyName("unload_task_base_score")]
    public required long UnloadTaskBaseScore { get; init; }

    [JsonPropertyName("capabilities")]
    public required HostManagerPublicServiceCapabilityProfile[] Capabilities { get; init; }

    [JsonPropertyName("routes")]
    public required HostManagerPublicServiceRouteProfile[] Routes { get; init; }
}
