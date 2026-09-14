namespace ResourceManager.App.Domain.Optimization;

public sealed record OptimizationReportStateDocument(
    int Version,
    IReadOnlyList<OptimizationObservationRecord> Observations);

public sealed record OptimizationObservationWindowBucket(
    DateTimeOffset BucketStart,
    double Value,
    int EventCount);

public sealed record OptimizationObservationRecord(
    string Key,
    string ReportId,
    string Type,
    string ResourceKind,
    string TargetType,
    string TargetKey,
    string DisplayName,
    string? SoftwareId,
    string? SoftwareKind,
    string? DisplayKind,
    IReadOnlyList<string> ProcessNames,
    string? DriveLetter,
    DateTimeOffset FirstObservedAt,
    DateTimeOffset LastObservedAt,
    int SampleCount,
    int ActiveSampleCount,
    double ValueSum,
    double PeakValue,
    double CurrentValue,
    string Unit,
    bool IsBytes,
    string ContextKind = OptimizationActivityContextKinds.Unknown,
    int? ForegroundProcessId = null,
    string? ForegroundProcessName = null,
    string? ForegroundExecutablePath = null,
    string? ForegroundSoftwareId = null,
    string? ForegroundSoftwareName = null,
    string ContextConfidence = "Low",
    IReadOnlyList<string>? ContextEvidence = null,
    IReadOnlyList<OptimizationObservationWindowBucket>? WindowBuckets = null,
    string? AdvisoryFamily = null,
    string? TitleOverride = null,
    string? MessageOverride = null,
    string? SeverityOverride = null,
    string? ConfidenceOverride = null,
    int? StaleAfterSeconds = null);
