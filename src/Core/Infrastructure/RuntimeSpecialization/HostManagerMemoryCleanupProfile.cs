using System.Text.Json.Serialization;

namespace ResourceManager.App.Infrastructure.RuntimeSpecialization;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class HostManagerMemoryCleanupRecreateProfile
{
    [JsonPropertyName("state_capacity")]
    public required int StateCapacity { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class HostManagerMemoryCleanupProfile
{
    [JsonPropertyName("critical_free_ratio")]
    public required double CriticalFreeRatio { get; init; }

    [JsonPropertyName("very_low_free_ratio")]
    public required double VeryLowFreeRatio { get; init; }

    [JsonPropertyName("low_free_ratio")]
    public required double LowFreeRatio { get; init; }

    [JsonPropertyName("guarded_free_ratio")]
    public required double GuardedFreeRatio { get; init; }

    [JsonPropertyName("critical_batch_count")]
    public required int CriticalBatchCount { get; init; }

    [JsonPropertyName("very_low_batch_count")]
    public required int VeryLowBatchCount { get; init; }

    [JsonPropertyName("low_batch_count")]
    public required int LowBatchCount { get; init; }

    [JsonPropertyName("guarded_batch_count")]
    public required int GuardedBatchCount { get; init; }

    [JsonPropertyName("emergency_batch_count")]
    public required int EmergencyBatchCount { get; init; }
}
