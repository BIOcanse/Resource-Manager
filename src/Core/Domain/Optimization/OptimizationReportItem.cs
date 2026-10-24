namespace ResourceManager.App.Domain.Optimization;

public static class OptimizationReportTypes
{
    public const string SuspectedStrayProcess = "suspected_stray_process";
    public const string GameBackgroundResourceUsage = "game_background_resource_usage";
    public const string BackgroundHighUsage = "background_high_usage";
    public const string BackgroundPersistentMicroUsage = "background_persistent_micro_usage";
    public const string VramResidency = "vram_residency";
    public const string DiskPressure = "disk_pressure";
    public const string RollingDiskTraffic = "rolling_disk_traffic";
    public const string RollingDiskWrite = "rolling_disk_write";
    public const string RollingNetworkTraffic = "rolling_network_traffic";
    public const string RollingNetworkActivity = "rolling_network_activity";
    public const string SoftwareFootprint = "software_footprint";
    public const string PowerProfileNotPerformanceFocused = "power_profile_not_performance_focused";
    public const string ExternalDisplayLinkCapabilityGap = "external_display_link_capability_gap";
    public const string DeviceDriverProblem = "device_driver_problem";
    public const string ExternalDiskDuplicateSecurityScan = "external_disk_duplicate_security_scan";
    public const string ExternalDiskIdleTimeoutTooShort = "external_disk_idle_timeout_too_short";
    public const string PhysicalDiskLatencyHigh = "physical_disk_latency_high";
    public const string PhysicalDiskHealthWarning = "physical_disk_health_warning";
    public const string ExternalDiskLinkCapabilityGap = "external_disk_link_capability_gap";
    public const string VolumeFragmentationHigh = "volume_fragmentation_high";
    public const string CpuSustainedThermalThrottling = "cpu_sustained_thermal_throttling";
    public const string SystemInterruptPressure = "system_interrupt_pressure";
}

public static class OptimizationReportStates
{
    public const string Active = "active";
    public const string TrustedSuppressed = "trusted_suppressed";
}

public static class OptimizationSeverity
{
    public const string Info = "info";
    public const string Warning = "warning";
    public const string Critical = "critical";
}

public static class OptimizationConfidence
{
    public const string Low = "low";
    public const string Medium = "medium";
    public const string High = "high";
}

public sealed record OptimizationReportOverview(
    DateTimeOffset CapturedAt,
    IReadOnlyList<OptimizationReportItem> Reports,
    IReadOnlyList<TrustedOptimizationTarget> TrustedTargets,
    IReadOnlyList<ProtectedOptimizationTarget> ProtectedTargets,
    OptimizationRecorderStatus Status);

public sealed record OptimizationReportItem(
    string Id,
    string Type,
    string State,
    string Severity,
    string Confidence,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset FirstObservedAt,
    DateTimeOffset LastObservedAt,
    string Title,
    string Message,
    OptimizationActivityContext Context,
    OptimizationReportTarget Target,
    OptimizationReportEvidence Evidence,
    IReadOnlyList<OptimizationReportAction> SuggestedActions);

public sealed record OptimizationReportAction(
    string Id,
    string Label,
    string Kind,
    bool Enabled,
    string? DisabledReason);

public sealed record OptimizationRecorderStatus(
    DateTimeOffset? LastEvaluationAt,
    DateTimeOffset? LastObservedAt,
    int ConfiguredRuleCount,
    int AvailableRuleCount,
    int ActiveReportCount,
    int TrustedCount,
    int ProtectedCount,
    int SampleIntervalSeconds)
{
    public string EvaluationState { get; init; } = "notEvaluated";
    public string? EvaluationFailureReason { get; init; }
    public IReadOnlyList<string> UnavailableRules { get; init; } = [];
}
