using System.Text.Json.Serialization;

namespace ResourceManager.App.Infrastructure.RuntimeSpecialization;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class HostManagerSamplingSubscriptionRoleCapacityProfile
{
    [JsonPropertyName("role_id")]
    public required int RoleId { get; init; }

    [JsonPropertyName("maximum_source_count")]
    public required int MaximumSourceCount { get; init; }

    [JsonPropertyName("maximum_item_count")]
    public required int MaximumItemCount { get; init; }

    [JsonPropertyName("maximum_membership_count")]
    public required int MaximumMembershipCount { get; init; }

    [JsonPropertyName("maximum_due_item_count")]
    public required int MaximumDueItemCount { get; init; }

    [JsonPropertyName("maximum_source_view_count")]
    public required int MaximumSourceViewCount { get; init; }

    [JsonPropertyName("maximum_expired_source_count")]
    public required int MaximumExpiredSourceCount { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class HostManagerSamplingSubscriptionRecreateProfile
{
    [JsonPropertyName("roles")]
    public required HostManagerSamplingSubscriptionRoleCapacityProfile[] Roles { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class HostManagerSamplingSubscriptionRoleHotPublishProfile
{
    [JsonPropertyName("role_id")]
    public required int RoleId { get; init; }

    [JsonPropertyName("default_interval_ms")]
    public required long DefaultIntervalMilliseconds { get; init; }

    [JsonPropertyName("minimum_interval_ms")]
    public required long MinimumIntervalMilliseconds { get; init; }

    [JsonPropertyName("active_ttl_ms")]
    public required long ActiveTtlMilliseconds { get; init; }

    [JsonPropertyName("maximum_future_skew_ms")]
    public required long MaximumFutureSkewMilliseconds { get; init; }

    [JsonPropertyName("freshness_grace_ms")]
    public required long FreshnessGraceMilliseconds { get; init; }

}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class HostManagerSamplingSubscriptionHotPublishProfile
{
    [JsonPropertyName("roles")]
    public required HostManagerSamplingSubscriptionRoleHotPublishProfile[] Roles { get; init; }
}
