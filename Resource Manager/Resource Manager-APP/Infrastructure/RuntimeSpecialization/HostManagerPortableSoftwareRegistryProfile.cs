using System.Text.Json.Serialization;

namespace ResourceManager.App.Infrastructure.RuntimeSpecialization;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class HostManagerPortableSoftwareRegistryCapacityProfile
{
    [JsonPropertyName("maximum_registration_count")]
    public required int MaximumRegistrationCount { get; init; }

    [JsonPropertyName("maximum_path_count")]
    public required int MaximumPathCount { get; init; }

    [JsonPropertyName("maximum_persistence_operation_count")]
    public required int MaximumPersistenceOperationCount { get; init; }

    [JsonPropertyName("maximum_registration_snapshot_count")]
    public required int MaximumRegistrationSnapshotCount { get; init; }

    [JsonPropertyName("maximum_path_snapshot_count")]
    public required int MaximumPathSnapshotCount { get; init; }

    [JsonPropertyName("maximum_executable_path_byte_count")]
    public required int MaximumExecutablePathByteCount { get; init; }

    [JsonPropertyName("maximum_root_path_byte_count")]
    public required int MaximumRootPathByteCount { get; init; }

    [JsonPropertyName("registration_index_capacity")]
    public required int RegistrationIndexCapacity { get; init; }

    [JsonPropertyName("path_index_capacity")]
    public required int PathIndexCapacity { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class HostManagerPortableSoftwareRegistryRecreateProfile
{
    [JsonPropertyName("capacity")]
    public required HostManagerPortableSoftwareRegistryCapacityProfile Capacity { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class HostManagerPortableSoftwareRegistryHotPublishProfile
{
    [JsonPropertyName("maximum_future_skew_ms")]
    public required int MaximumFutureSkewMilliseconds { get; init; }

    [JsonPropertyName("resident_byte_budget")]
    public required long ResidentByteBudget { get; init; }
}
