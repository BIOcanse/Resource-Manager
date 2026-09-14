using System.Text.Json.Serialization;

namespace ResourceManager.App.Infrastructure.RuntimeSpecialization;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class HostManagerOperationCoordinatorCapacityProfile
{
    [JsonPropertyName("maximum_operation_count")]
    public required uint MaximumOperationCount { get; init; }

    [JsonPropertyName("maximum_domain_count")]
    public required uint MaximumDomainCount { get; init; }

    [JsonPropertyName("maximum_action_count")]
    public required uint MaximumActionCount { get; init; }

    [JsonPropertyName("operation_index_capacity")]
    public required uint OperationIndexCapacity { get; init; }

    [JsonPropertyName("domain_index_capacity")]
    public required uint DomainIndexCapacity { get; init; }

    [JsonPropertyName("maximum_read_count")]
    public required uint MaximumReadCount { get; init; }

    [JsonPropertyName("maximum_persistence_byte_count")]
    public required ulong MaximumPersistenceByteCount { get; init; }

    [JsonPropertyName("resident_byte_budget")]
    public required ulong ResidentByteBudget { get; init; }

    [JsonPropertyName("maximum_payload_count")]
    public required uint MaximumPayloadCount { get; init; }

    [JsonPropertyName("maximum_payload_byte_count")]
    public required ulong MaximumPayloadByteCount { get; init; }

    [JsonPropertyName("maximum_effect_receipt_count")]
    public required uint MaximumEffectReceiptCount { get; init; }

    [JsonPropertyName("maximum_envelope_byte_count")]
    public required ulong MaximumEnvelopeByteCount { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class HostManagerOperationCoordinatorRecreateProfile
{
    [JsonPropertyName("capacity")]
    public required HostManagerOperationCoordinatorCapacityProfile Capacity { get; init; }

    [JsonPropertyName("canonical_envelope_relative_path")]
    public required string CanonicalEnvelopeRelativePath { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class HostManagerOperationKindProfile
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("priority")]
    public required long Priority { get; init; }

    [JsonPropertyName("maximum_attempts")]
    public required uint MaximumAttempts { get; init; }

    [JsonPropertyName("retry_delay_ms")]
    public required uint RetryDelayMilliseconds { get; init; }

    [JsonPropertyName("execution_timeout_ms")]
    public required ulong ExecutionTimeoutMilliseconds { get; init; }

    [JsonPropertyName("cancel_grace_ms")]
    public required ulong CancelGraceMilliseconds { get; init; }

    [JsonPropertyName("terminal_retention_ms")]
    public required ulong TerminalRetentionMilliseconds { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class HostManagerOperationCoordinatorHotPublishProfile
{
    [JsonPropertyName("maximum_global_running_count")]
    public required uint MaximumGlobalRunningCount { get; init; }

    [JsonPropertyName("maximum_recent_terminal_count")]
    public required uint MaximumRecentTerminalCount { get; init; }

    [JsonPropertyName("maximum_start_actions_per_plan")]
    public required uint MaximumStartActionsPerPlan { get; init; }

    [JsonPropertyName("maximum_cancel_actions_per_plan")]
    public required uint MaximumCancelActionsPerPlan { get; init; }

    [JsonPropertyName("maximum_recover_actions_per_plan")]
    public required uint MaximumRecoverActionsPerPlan { get; init; }

    [JsonPropertyName("maximum_future_skew_ms")]
    public required ulong MaximumFutureSkewMilliseconds { get; init; }

    [JsonPropertyName("kinds")]
    public required HostManagerOperationKindProfile[] Kinds { get; init; }
}
