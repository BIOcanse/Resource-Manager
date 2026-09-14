namespace ResourceManager.App.Domain.Optimization;

public static class OptimizationReportTypes
{
    public const string GameBackgroundResourceUsage = "GameBackgroundResourceUsage";
    public const string BackgroundHighUsage = "BackgroundHighUsage";
    public const string BackgroundPersistentMicroUsage = "BackgroundPersistentMicroUsage";
    public const string VramResidency = "VramResidency";
    public const string DiskPressure = "DiskPressure";
    public const string RollingDiskTraffic = "RollingDiskTraffic";
    public const string RollingDiskWrite = "RollingDiskWrite";
    public const string RollingNetworkTraffic = "RollingNetworkTraffic";
    public const string RollingNetworkActivity = "RollingNetworkActivity";
    public const string SoftwareFootprint = "SoftwareFootprint";
    public const string PowerProfileNotPerformanceFocused = "PowerProfileNotPerformanceFocused";
    public const string ExternalDisplayLinkCapabilityGap = "ExternalDisplayLinkCapabilityGap";
    public const string DeviceDriverProblem = "DeviceDriverProblem";
    public const string ExternalDiskDuplicateSecurityScan = "ExternalDiskDuplicateSecurityScan";
    public const string ExternalDiskIdleTimeoutTooShort = "ExternalDiskIdleTimeoutTooShort";
    public const string PhysicalDiskLatencyHigh = "PhysicalDiskLatencyHigh";
    public const string PhysicalDiskHealthWarning = "PhysicalDiskHealthWarning";
    public const string ExternalDiskLinkCapabilityGap = "ExternalDiskLinkCapabilityGap";
    public const string VolumeFragmentationHigh = "VolumeFragmentationHigh";
    public const string CpuSustainedThermalThrottling = "CpuSustainedThermalThrottling";
    public const string SystemInterruptPressure = "SystemInterruptPressure";
}

public static class OptimizationReportStates
{
    public const string Active = "Active";
    public const string TrustedSuppressed = "TrustedSuppressed";
}

public static class OptimizationSeverity
{
    public const string Info = "Info";
    public const string Warning = "Warning";
    public const string Critical = "Critical";
}

public static class OptimizationConfidence
{
    public const string Low = "Low";
    public const string Medium = "Medium";
    public const string High = "High";
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
    int SampleIntervalSeconds);
