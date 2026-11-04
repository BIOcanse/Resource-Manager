using System.Text.Json.Serialization;

namespace ResourceManager.App.Infrastructure.RuntimeSpecialization;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class HostManagerSoftwareIdentityResolutionCapacityProfile
{
    [JsonPropertyName("maximum_policy_count")]
    public required int MaximumPolicyCount { get; init; }

    [JsonPropertyName("maximum_observation_count")]
    public required int MaximumObservationCount { get; init; }

    [JsonPropertyName("policy_index_capacity")]
    public required int PolicyIndexCapacity { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class HostManagerSoftwareIdentityResolutionRecreateProfile
{
    [JsonPropertyName("capacity")]
    public required HostManagerSoftwareIdentityResolutionCapacityProfile Capacity { get; init; }

    [JsonPropertyName("source_ids")]
    public required uint[] SourceIds { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class HostManagerSoftwareIdentitySourcePolicyProfile
{
    [JsonPropertyName("source_id")]
    public required uint SourceId { get; init; }

    [JsonPropertyName("priority")]
    public required uint Priority { get; init; }

    [JsonPropertyName("stop_on_unavailable")]
    public required bool StopOnUnavailable { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class HostManagerSoftwareIdentityResolutionHotPublishProfile
{
    [JsonPropertyName("maximum_future_skew_ms")]
    public required int MaximumFutureSkewMilliseconds { get; init; }

    [JsonPropertyName("resident_byte_budget")]
    public required long ResidentByteBudget { get; init; }

    [JsonPropertyName("source_policies")]
    public required HostManagerSoftwareIdentitySourcePolicyProfile[] SourcePolicies { get; init; }
}
