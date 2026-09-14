using System.Text.Json.Serialization;

namespace ResourceManager.App.Infrastructure.RuntimeSpecialization;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class HostManagerAppliedOwnershipRecreateProfile
{
    [JsonPropertyName("record_capacity")]
    public required int RecordCapacity { get; init; }

    [JsonPropertyName("primary_index_capacity")]
    public required int PrimaryIndexCapacity { get; init; }

    [JsonPropertyName("payload_index_capacity")]
    public required int PayloadIndexCapacity { get; init; }

    [JsonPropertyName("resident_byte_budget")]
    public required long ResidentByteBudget { get; init; }

    [JsonPropertyName("image_byte_budget")]
    public required long ImageByteBudget { get; init; }

    [JsonPropertyName("ledger_relative_path")]
    public required string LedgerRelativePath { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class HostManagerAppliedOwnershipHotPublishProfile
{
    [JsonPropertyName("maximum_future_skew_ms")]
    public required int MaximumFutureSkewMilliseconds { get; init; }

    [JsonPropertyName("persistence_retry_delay_ms")]
    public required int PersistenceRetryDelayMilliseconds { get; init; }

    [JsonPropertyName("recovery_deadline_ms")]
    public required int RecoveryDeadlineMilliseconds { get; init; }

    [JsonPropertyName("shutdown_drain_timeout_ms")]
    public required int ShutdownDrainTimeoutMilliseconds { get; init; }
}
