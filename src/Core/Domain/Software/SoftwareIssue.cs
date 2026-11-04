namespace ResourceManager.App.Domain.Software;

public static class SoftwareIssueKinds
{
    public const string Unclassified = "unclassified";
    public const string KnownSecurityVulnerability = "known_security_vulnerability";
    public const string DependencyIssue = "dependency_issue";
    public const string CompatibilityIssue = "compatibility_issue";
    public const string KnownMalware = "known_malware";
    public const string AbnormalMemoryUsage = "abnormal_memory_usage";
    public const string ExcessiveSystemInterrupts = "excessive_system_interrupts";
    public const string LongSystemInterrupts = "long_system_interrupts";

    public static bool IsKnown(string value)
        => value is Unclassified or KnownSecurityVulnerability
            or DependencyIssue
            or CompatibilityIssue
            or KnownMalware
            or AbnormalMemoryUsage
            or ExcessiveSystemInterrupts
            or LongSystemInterrupts;

    public static bool IsDynamic(string value)
        => value is AbnormalMemoryUsage
            or ExcessiveSystemInterrupts
            or LongSystemInterrupts;
}

public static class SoftwareIssueSources
{
    public const string RuntimeAttribution = "runtime_attribution";
    public const string StaticCatalog = "static_catalog";
    public const string DynamicReport = "dynamic_report";
}

public static class SoftwareIssueSeverities
{
    public const string Info = "info";
    public const string Warning = "warning";
    public const string Critical = "critical";

    public static bool IsKnown(string value)
        => value is Info or Warning or Critical;
}

public sealed record SoftwareIssueReference(
    string Label,
    string Url);

public sealed record SoftwareIssueTag(
    string Id,
    string Kind,
    string Severity,
    string Source,
    string Label,
    string Message,
    bool Dynamic,
    IReadOnlyList<SoftwareIssueReference> References,
    IReadOnlyList<string> ReportIds)
{
    public IReadOnlyList<SoftwareIssueEvidence> Evidence { get; init; } = [];
}

public sealed record SoftwareIssueEvidence(string MetricKind, double Value, string ValueUnit, string? ModuleName = null);

public static class SoftwareIssueMetricKinds
{
    public const string Memory = "memory";
    public const string InterruptMaximumSingleDurationMilliseconds = "interrupt_maximum_single_duration_milliseconds";
    public const string InterruptEventsAtOrAboveOneMillisecond = "interrupt_events_at_or_above_one_millisecond";
    public const string InterruptCpuCapacityPercent = "interrupt_cpu_capacity_percent";
}

public sealed record SoftwareIssueCatalogDocument(
    string Version,
    IReadOnlyList<SoftwareIssueCatalogEntry> Entries);

public sealed record SoftwareIssueCatalogEntry(
    string Id,
    string SoftwareIdentityId,
    string Kind,
    string Severity,
    string Label,
    string Message,
    IReadOnlyList<SoftwareIssueArtifactRequirement> Artifacts,
    IReadOnlyList<SoftwareIssueReference> References);

public sealed record SoftwareIssueArtifactRequirement(
    string Path,
    long Length,
    string Sha256,
    string? FileVersion = null);
