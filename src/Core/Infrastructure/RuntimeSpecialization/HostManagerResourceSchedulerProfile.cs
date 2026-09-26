using System.Text.Json.Serialization;

namespace ResourceManager.App.Infrastructure.RuntimeSpecialization;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class HostManagerResourceSchedulerRecreateProfile
{
    [JsonPropertyName("target_capacity")]
    public required int TargetCapacity { get; init; }

    [JsonPropertyName("private_resource_capacity")]
    public required int PrivateResourceCapacity { get; init; }

    [JsonPropertyName("pending_capacity")]
    public required int PendingCapacity { get; init; }

    [JsonPropertyName("journal_pending_capacity")]
    public required int JournalPendingCapacity { get; init; }

    [JsonPropertyName("authority_capacity")]
    public required int AuthorityCapacity { get; init; }

    [JsonPropertyName("feedback_capacity")]
    public required int FeedbackCapacity { get; init; }

    [JsonPropertyName("private_ledger_capacity")]
    public required int PrivateLedgerCapacity { get; init; }

    [JsonPropertyName("maximum_resident_bytes")]
    public required ulong MaximumResidentBytes { get; init; }

}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class HostManagerResourceSchedulerHotPublishProfile
{
    [JsonPropertyName("configuration")]
    public required HostManagerResourceSchedulerConfigurationProfile Configuration { get; init; }

    [JsonPropertyName("dispatch")]
    public required HostManagerResourceSchedulerDispatchProfile Dispatch { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class HostManagerResourceSchedulerDispatchProfile
{
    [JsonPropertyName("execution_capability_enabled")]
    public required bool ExecutionCapabilityEnabled { get; init; }

    [JsonPropertyName("per_action_timeout_ms")]
    public required int PerActionTimeoutMilliseconds { get; init; }

    [JsonPropertyName("pending_action_ttl_ms")]
    public required int PendingActionTtlMilliseconds { get; init; }

    [JsonPropertyName("maximum_in_flight_actions")]
    public required int MaximumInFlightActions { get; init; }

    [JsonPropertyName("maximum_in_flight_actions_per_target")]
    public required int MaximumInFlightActionsPerTarget { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class HostManagerResourceSchedulerConfigurationProfile
{
    [JsonPropertyName("bytes_per_megabyte")]
    public required ulong BytesPerMegabyte { get; init; }

    [JsonPropertyName("size_importance_minimum")]
    public required double SizeImportanceMinimum { get; init; }

    [JsonPropertyName("size_importance_maximum")]
    public required double SizeImportanceMaximum { get; init; }

    [JsonPropertyName("size_log_divisor")]
    public required double SizeLogDivisor { get; init; }

    [JsonPropertyName("resource_kind_multipliers")]
    public required double[] ResourceKindMultipliers { get; init; }

    [JsonPropertyName("surface_multipliers")]
    public required double[] SurfaceMultipliers { get; init; }

    [JsonPropertyName("policy_grade_multipliers")]
    public required double[] PolicyGradeMultipliers { get; init; }

    [JsonPropertyName("policy_grade_pressure")]
    public required int[] PolicyGradePressure { get; init; }

    [JsonPropertyName("scheduling_grade_multipliers")]
    public required double[] SchedulingGradeMultipliers { get; init; }

    [JsonPropertyName("absent_scheduling_grade_multiplier")]
    public required double AbsentSchedulingGradeMultiplier { get; init; }

    [JsonPropertyName("discard_kind_multipliers")]
    public required double[] DiscardKindMultipliers { get; init; }

    [JsonPropertyName("trim_kind_multipliers")]
    public required double[] TrimKindMultipliers { get; init; }

    [JsonPropertyName("move_down_kind_multipliers")]
    public required double[] MoveDownKindMultipliers { get; init; }

    [JsonPropertyName("move_up_kind_multipliers")]
    public required double[] MoveUpKindMultipliers { get; init; }

    [JsonPropertyName("activity_base_multiplier")]
    public required double ActivityBaseMultiplier { get; init; }

    [JsonPropertyName("activity_quadratic_scale")]
    public required double ActivityQuadraticScale { get; init; }

    [JsonPropertyName("activity_normalizer")]
    public required double ActivityNormalizer { get; init; }

    [JsonPropertyName("demand_multipliers")]
    public required double[] DemandMultipliers { get; init; }

    [JsonPropertyName("large_resource_bytes")]
    public required ulong LargeResourceBytes { get; init; }

    [JsonPropertyName("high_activity_score")]
    public required byte HighActivityScore { get; init; }

    [JsonPropertyName("physical_to_virtual_desperate_pressure_level")]
    public required byte PhysicalToVirtualDesperatePressureLevel { get; init; }

    [JsonPropertyName("physical_to_virtual_minimum_free_ratio")]
    public required double PhysicalToVirtualMinimumFreeRatio { get; init; }

    [JsonPropertyName("physical_to_virtual_desperate_minimum_free_ratio")]
    public required double PhysicalToVirtualDesperateMinimumFreeRatio { get; init; }

    [JsonPropertyName("vram_to_physical_minimum_free_ratio")]
    public required double VramToPhysicalMinimumFreeRatio { get; init; }

    [JsonPropertyName("pressure_free_ratio_thresholds")]
    public required double[] PressureFreeRatioThresholds { get; init; }

    [JsonPropertyName("vram_target_free_ratio")]
    public required double VramTargetFreeRatio { get; init; }

    [JsonPropertyName("desired_free_ratios_level_2_to_4")]
    public required double[] DesiredFreeRatiosLevel2To4 { get; init; }

    [JsonPropertyName("minimum_release_bytes")]
    public required ulong[] MinimumReleaseBytes { get; init; }

    [JsonPropertyName("maximum_release_shares")]
    public required double[] MaximumReleaseShares { get; init; }

    [JsonPropertyName("base_score_thresholds")]
    public required double[] BaseScoreThresholds { get; init; }

    [JsonPropertyName("base_release_multipliers")]
    public required double[] BaseReleaseMultipliers { get; init; }

    [JsonPropertyName("base_score_minimum")]
    public required double BaseScoreMinimum { get; init; }

    [JsonPropertyName("base_score_maximum")]
    public required double BaseScoreMaximum { get; init; }

    [JsonPropertyName("trim_release_numerator")]
    public required uint TrimReleaseNumerator { get; init; }

    [JsonPropertyName("trim_release_denominator")]
    public required uint TrimReleaseDenominator { get; init; }

    [JsonPropertyName("maximum_actions_per_target")]
    public required uint MaximumActionsPerTarget { get; init; }

    [JsonPropertyName("strong_pressure_free_ratio")]
    public required double StrongPressureFreeRatio { get; init; }

    [JsonPropertyName("danger_severity_weights")]
    public required int[] DangerSeverityWeights { get; init; }
}
