using System.Text.Json.Serialization;

namespace ResourceManager.App.Infrastructure.RuntimeSpecialization;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class HostManagerAdapterPrivateResourceRecreateProfile
{
    [JsonPropertyName("state_capacity")]
    public required int StateCapacity { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class HostManagerAdapterPrivateResourceHotPublishProfile
{
    [JsonPropertyName("maximum_snapshot_age_ms")]
    public required int MaximumSnapshotAgeMilliseconds { get; init; }

    [JsonPropertyName("maximum_future_clock_skew_ms")]
    public required int MaximumFutureClockSkewMilliseconds { get; init; }

    [JsonPropertyName("settlement_interval_ms")]
    public required int SettlementIntervalMilliseconds { get; init; }

    [JsonPropertyName("request_timeout_ms")]
    public required int RequestTimeoutMilliseconds { get; init; }

    [JsonPropertyName("maximum_response_bytes")]
    public required int MaximumResponseBytes { get; init; }

    [JsonPropertyName("maximum_concurrent_reads")]
    public required int MaximumConcurrentReads { get; init; }

    [JsonPropertyName("maximum_cycle_duration_ms")]
    public required int MaximumCycleDurationMilliseconds { get; init; }

    [JsonPropertyName("active_increment")]
    public required byte ActiveIncrement { get; init; }

    [JsonPropertyName("decay_numerator")]
    public required byte DecayNumerator { get; init; }

    [JsonPropertyName("decay_denominator")]
    public required byte DecayDenominator { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class HostManagerProcessPolicyExecutorHotPublishProfile
{
    [JsonPropertyName("maximum_batch_items")]
    public required int MaximumBatchItems { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class HostManagerPdhCollectorRecreateProfile
{
    [JsonPropertyName("baseline_reset_interval_ms")]
    public required int BaselineResetIntervalMilliseconds { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class HostManagerPdhCollectorHotPublishProfile
{
    [JsonPropertyName("frame_reuse_window_ms")]
    public required int FrameReuseWindowMilliseconds { get; init; }

    [JsonPropertyName("last_good_lifetime_ms")]
    public required int LastGoodLifetimeMilliseconds { get; init; }
}
