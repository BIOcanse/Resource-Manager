using System.Text.Json.Serialization;

namespace ResourceManager.App.Infrastructure.RuntimeSpecialization;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class HostManagerTransactionJournalRecreateProfile
{
    [JsonPropertyName("record_capacity")]
    public required int RecordCapacity { get; init; }

    [JsonPropertyName("resident_byte_budget")]
    public required long ResidentByteBudget { get; init; }

    [JsonPropertyName("payload_count")]
    public required int PayloadCount { get; init; }

    [JsonPropertyName("payload_byte_budget")]
    public required long PayloadByteBudget { get; init; }

    [JsonPropertyName("journal_relative_path")]
    public required string JournalRelativePath { get; init; }

    [JsonPropertyName("payload_relative_directory")]
    public required string PayloadRelativeDirectory { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class HostManagerTransactionJournalHotPublishProfile
{
    [JsonPropertyName("maximum_recovery_attempts")]
    public required int MaximumRecoveryAttempts { get; init; }

    [JsonPropertyName("retry_delay_ms")]
    public required int RetryDelayMilliseconds { get; init; }

    [JsonPropertyName("recovery_deadline_ms")]
    public required int RecoveryDeadlineMilliseconds { get; init; }

    [JsonPropertyName("maximum_future_skew_ms")]
    public required int MaximumFutureSkewMilliseconds { get; init; }

    [JsonPropertyName("shutdown_drain_timeout_ms")]
    public required int ShutdownDrainTimeoutMilliseconds { get; init; }
}
