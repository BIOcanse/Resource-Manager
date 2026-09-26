namespace ResourceManager.App.Domain.Software;

public static class SoftwareIssueKinds
{
    public const string KnownSecurityVulnerability = "KnownSecurityVulnerability";
    public const string DependencyIssue = "DependencyIssue";
    public const string CompatibilityIssue = "CompatibilityIssue";
    public const string KnownMalware = "KnownMalware";
    public const string AbnormalMemoryUsage = "AbnormalMemoryUsage";
    public const string ExcessiveSystemInterrupts = "ExcessiveSystemInterrupts";
    public const string LongSystemInterrupts = "LongSystemInterrupts";

    public static bool IsKnown(string value)
        => value is KnownSecurityVulnerability
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
    public const string StaticCatalog = "StaticCatalog";
    public const string DynamicReport = "DynamicReport";
}

public static class SoftwareIssueSeverities
{
    public const string Info = "Info";
    public const string Warning = "Warning";
    public const string Critical = "Critical";

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
    IReadOnlyList<string> ReportIds);

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
