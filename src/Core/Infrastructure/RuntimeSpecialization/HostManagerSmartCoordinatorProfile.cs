using System.Text.Json.Serialization;

namespace ResourceManager.App.Infrastructure.RuntimeSpecialization;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class HostManagerSmartCoordinatorRecreateProfile
{
    [JsonPropertyName("maximum_processes")]
    public required int MaximumProcesses { get; init; }

    [JsonPropertyName("maximum_software_groups")]
    public required int MaximumSoftwareGroups { get; init; }

    [JsonPropertyName("maximum_gpu_states")]
    public required int MaximumGpuStates { get; init; }

    [JsonPropertyName("maximum_input_rows")]
    public required int MaximumInputRows { get; init; }

    [JsonPropertyName("maximum_actions")]
    public required int MaximumActions { get; init; }

    [JsonPropertyName("maximum_reservations")]
    public required int MaximumReservations { get; init; }

    [JsonPropertyName("maximum_atomic_groups")]
    public required int MaximumAtomicGroups { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class HostManagerSmartCoordinatorHotPublishProfile
{
    [JsonPropertyName("feature_flags")]
    public required string[] FeatureFlags { get; init; }

    [JsonPropertyName("normal_interval_ms")]
    public required int NormalIntervalMilliseconds { get; init; }

    [JsonPropertyName("event_interval_ms")]
    public required int EventIntervalMilliseconds { get; init; }

    [JsonPropertyName("event_boost_ms")]
    public required int EventBoostMilliseconds { get; init; }

    [JsonPropertyName("game_start_grace_ms")]
    public required int GameStartGraceMilliseconds { get; init; }

    [JsonPropertyName("required_consecutive_decisions")]
    public required int RequiredConsecutiveDecisions { get; init; }

    [JsonPropertyName("failure_retry_ms")]
    public required int FailureRetryMilliseconds { get; init; }

    [JsonPropertyName("reservation_timeout_ms")]
    public required int ReservationTimeoutMilliseconds { get; init; }

    [JsonPropertyName("maximum_actions_per_realtime_tick")]
    public required int MaximumActionsPerRealtimeTick { get; init; }

    [JsonPropertyName("memory_mode_policy")]
    public required HostManagerMemoryModePolicyProfile MemoryModePolicy { get; init; }

    [JsonPropertyName("base_score_tiers")]
    public required HostManagerBaseScoreTierProfile BaseScoreTiers { get; init; }

    [JsonPropertyName("process_policy")]
    public required HostManagerSmartCoordinatorProcessPolicyProfile ProcessPolicy { get; init; }

    [JsonPropertyName("cpu_adapter_policy")]
    public required HostManagerSmartCoordinatorCpuAdapterPolicyProfile CpuAdapterPolicy { get; init; }

    [JsonPropertyName("gpu_adapter_policy")]
    public required HostManagerSmartCoordinatorGpuAdapterPolicyProfile GpuAdapterPolicy { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class HostManagerBaseScoreTierProfile
{
    [JsonPropertyName("high_minimum_base_score")]
    public required double HighMinimumBaseScore { get; init; }

    [JsonPropertyName("middle_minimum_base_score")]
    public required double MiddleMinimumBaseScore { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class HostManagerMemoryModePolicyProfile
{
    [JsonPropertyName("enabled")]
    public required bool Enabled { get; init; }

    [JsonPropertyName("source_kind")]
    public required string SourceKind { get; init; }

    [JsonPropertyName("ratio_units_maximum")]
    public required uint RatioUnitsMaximum { get; init; }

    [JsonPropertyName("strong_begin_free_ratio_units")]
    public required uint StrongBeginFreeRatioUnits { get; init; }

    [JsonPropertyName("normal_minimum_free_ratio_units")]
    public required uint NormalMinimumFreeRatioUnits { get; init; }

    [JsonPropertyName("unrestricted_minimum_free_ratio_units")]
    public required uint UnrestrictedMinimumFreeRatioUnits { get; init; }

    [JsonPropertyName("allow_unrestricted")]
    public required bool AllowUnrestricted { get; init; }

    [JsonPropertyName("optimize_memory_priority")]
    public required uint OptimizeMemoryPriority { get; init; }

    [JsonPropertyName("paged_frozen_memory_priority")]
    public required uint PagedFrozenMemoryPriority { get; init; }

    [JsonPropertyName("foreign_memory_priority_disposition")]
    public required string ForeignMemoryPriorityDisposition { get; init; }

    [JsonPropertyName("owned_state_verification_interval_cycles")]
    public required uint OwnedStateVerificationIntervalCycles { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class HostManagerSmartCoordinatorProcessPolicyProfile
{
    [JsonPropertyName("state_multipliers")]
    public required double[] StateMultipliers { get; init; }

    [JsonPropertyName("a1_minimum_cpu_score")]
    public required double A1MinimumCpuScore { get; init; }

    [JsonPropertyName("default_minimum_cpu_score_scale")]
    public required double DefaultMinimumCpuScoreScale { get; init; }

    [JsonPropertyName("level1_maximum_cpu_score_scale")]
    public required double Level1MaximumCpuScoreScale { get; init; }

    [JsonPropertyName("level2_maximum_cpu_score_scale")]
    public required double Level2MaximumCpuScoreScale { get; init; }

    [JsonPropertyName("level3_maximum_cpu_score_scale")]
    public required double Level3MaximumCpuScoreScale { get; init; }

    [JsonPropertyName("low_tier_level4_maximum_cpu_score_scale")]
    public required double LowTierLevel4MaximumCpuScoreScale { get; init; }
}

internal abstract class HostManagerSmartCoordinatorAdapterPolicyProfile
{
    [JsonPropertyName("extreme_minimum_score")]
    public required double ExtremeMinimumScore { get; init; }

    [JsonPropertyName("normal_minimum_score")]
    public required double NormalMinimumScore { get; init; }

    [JsonPropertyName("optimize_minimum_score")]
    public required double OptimizeMinimumScore { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class HostManagerSmartCoordinatorCpuAdapterPolicyProfile
    : HostManagerSmartCoordinatorAdapterPolicyProfile
{
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class HostManagerSmartCoordinatorGpuAdapterPolicyProfile
    : HostManagerSmartCoordinatorAdapterPolicyProfile
{
    [JsonPropertyName("state_multipliers")]
    public required double[] StateMultipliers { get; init; }
}
