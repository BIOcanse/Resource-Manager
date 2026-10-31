namespace ResourceManager.App.Application.CrashReports;

// Wire shapes of /api/crash-reports (docs/crash_dumps_2026-10-31.md; JSON camelCase).

public sealed record CrashReportDump(string Path, long SizeBytes);

// Facts from the Application log near the crash: Application Error (1000) gives module, offset and exception code;
// .NET Runtime (1026) gives the description and the managed stack.
public sealed record CrashReportFault(
    string? Module,
    string? Offset,
    string? ExceptionCode,
    string? ManagedStack,
    string? Description);

public sealed record CrashReportPackage(string Path, long SizeBytes, DateTimeOffset CreatedAt, bool ExceedsIssueLimit);

public sealed record CrashReport(
    string Id,
    DateTimeOffset OccurredAt,
    string Program,
    string Role,
    string? Version,
    string DumpStatus,
    CrashReportDump? Dump,
    CrashReportFault? Fault,
    CrashReportPackage? Package,
    bool Acknowledged,
    bool Reported);

public static class CrashReportDumpStatuses
{
    public const string Saved = "saved";
    public const string NotEnabled = "notEnabled";
    public const string Missing = "missing";
}

// Newest first. DumpFolder and the attachment limit come from the compiled freedom point.
public sealed record CrashReportList(
    IReadOnlyList<CrashReport> Reports,
    string DumpFolder,
    long IssueAttachmentLimitBytes);

public sealed record CrashReportPackageResult(CrashReport Report, CrashReportPackage Package, string? Reason);

public static class CrashReportPackageReasons
{
    public const string ExceedsIssueAttachmentLimit = "exceedsIssueAttachmentLimit";
}

public sealed record CrashReportIssueLink(string Url, CrashReport Report);

public sealed record CrashReportAcknowledgement(CrashReport Report);

public interface ICrashReportOwner
{
    // Rescans first (bounded), then returns the list. No notification.
    CrashReportList Read();

    // The list after a rescan first, then the whole list after every change.
    IAsyncEnumerable<CrashReportList> SubscribeAsync(CancellationToken cancellationToken);

    // Startup discovery: rescans and posts one crash-report notification when it registered new records.
    void DiscoverAtStartup();

    CrashReportPackageResult? Package(string id);

    CrashReportIssueLink? IssueLink(string id);

    CrashReportAcknowledgement? Acknowledge(string id);

    bool Delete(string id);
}
