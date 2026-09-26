using System.Text.Json.Serialization;

namespace ResourceManager.App.Infrastructure.RuntimeSpecialization;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class HostManagerDisplayCoordinatorCapacityProfile
{
    [JsonPropertyName("maximum_source_count")]
    public required uint MaximumSourceCount { get; init; }

    [JsonPropertyName("maximum_observation_count")]
    public required uint MaximumObservationCount { get; init; }

    [JsonPropertyName("maximum_node_count")]
    public required uint MaximumNodeCount { get; init; }

    [JsonPropertyName("maximum_edge_count")]
    public required uint MaximumEdgeCount { get; init; }

    [JsonPropertyName("maximum_capability_count")]
    public required uint MaximumCapabilityCount { get; init; }

    [JsonPropertyName("maximum_diff_entry_count")]
    public required uint MaximumDiffEntryCount { get; init; }

    [JsonPropertyName("maximum_text_binding_count")]
    public required uint MaximumTextBindingCount { get; init; }

    [JsonPropertyName("maximum_text_byte_count")]
    public required uint MaximumTextByteCount { get; init; }

    [JsonPropertyName("maximum_unresolved_count")]
    public required uint MaximumUnresolvedCount { get; init; }

    [JsonPropertyName("maximum_source_batch_count")]
    public required uint MaximumSourceBatchCount { get; init; }

    [JsonPropertyName("identity_index_capacity")]
    public required uint IdentityIndexCapacity { get; init; }

    [JsonPropertyName("text_index_capacity")]
    public required uint TextIndexCapacity { get; init; }

    [JsonPropertyName("resident_byte_budget")]
    public required ulong ResidentByteBudget { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class HostManagerDisplayCoordinatorRecreateProfile
{
    [JsonPropertyName("capacity")]
    public required HostManagerDisplayCoordinatorCapacityProfile Capacity { get; init; }

    [JsonPropertyName("persistence_relative_path")]
    public required string PersistenceRelativePath { get; init; }

    [JsonPropertyName("required_sources")]
    public required string[] RequiredSources { get; init; }

    [JsonPropertyName("optional_sources")]
    public required string[] OptionalSources { get; init; }

    [JsonPropertyName("identity_source_priority")]
    public required string[] IdentitySourcePriority { get; init; }

    [JsonPropertyName("friendly_name_source_priority")]
    public required string[] FriendlyNameSourcePriority { get; init; }

    [JsonPropertyName("capability_source_priority")]
    public required string[] CapabilitySourcePriority { get; init; }

    [JsonPropertyName("projection_source_priority")]
    public required string[] ProjectionSourcePriority { get; init; }

    [JsonPropertyName("identity_contract_version")]
    public required uint IdentityContractVersion { get; init; }

    [JsonPropertyName("capability_contract_version")]
    public required uint CapabilityContractVersion { get; init; }

    [JsonPropertyName("maximum_future_skew_ms")]
    public required uint MaximumFutureSkewMilliseconds { get; init; }
}
