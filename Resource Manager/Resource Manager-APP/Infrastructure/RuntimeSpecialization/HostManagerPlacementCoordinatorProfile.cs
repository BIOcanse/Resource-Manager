using System.Text.Json.Serialization;

namespace ResourceManager.App.Infrastructure.RuntimeSpecialization;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class HostManagerPlacementCoordinatorRecreateProfile
{
    [JsonPropertyName("maximum_desired_count")]
    public required int MaximumDesiredCount { get; init; }

    [JsonPropertyName("maximum_applied_count")]
    public required int MaximumAppliedCount { get; init; }

    [JsonPropertyName("maximum_action_count")]
    public required int MaximumActionCount { get; init; }

    [JsonPropertyName("maximum_state_count")]
    public required int MaximumStateCount { get; init; }

    [JsonPropertyName("core_capacity")]
    public required int CoreCapacity { get; init; }

    [JsonPropertyName("ccd_capacity")]
    public required int CcdCapacity { get; init; }

    [JsonPropertyName("target_capacity")]
    public required int TargetCapacity { get; init; }

    [JsonPropertyName("reservation_capacity")]
    public required int ReservationCapacity { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class HostManagerPlacementCoordinatorHotPublishProfile
{
    [JsonPropertyName("retry_delay_ms")]
    public required int RetryDelayMilliseconds { get; init; }

    [JsonPropertyName("action_timeout_ms")]
    public required int ActionTimeoutMilliseconds { get; init; }

    [JsonPropertyName("maximum_future_skew_ms")]
    public required int MaximumFutureSkewMilliseconds { get; init; }
}
