using System.Text.Json.Serialization;

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

public sealed record CrashReportPackage(string Path, long SizeBytes, DateTimeOffset CreatedAt);

public sealed record CrashReport(
    string Id,
    DateTimeOffset OccurredAt,
    string Program,
    string Role,
    string? Version,
    string DumpStatus,
    CrashReportDump? Dump,
    [property: JsonPropertyOrder(1)] CrashReportFault? Fault,
    [property: JsonPropertyOrder(1)] CrashReportPackage? Package,
    [property: JsonPropertyOrder(1)] bool Acknowledged)
{
    // A dump that was saved and has since been deleted (Windows keeps only the newest dumps per program):
    // the owner turned the record Missing and kept its old Dump. Derived on read, never stored; on the wire it
    // follows dump.
    public bool DumpRemoved => DumpStatus == CrashReportDumpStatuses.Missing && Dump is not null;
}

public static class CrashReportDumpStatuses
{
    public const string Saved = "saved";
    public const string NotEnabled = "notEnabled";
    public const string Missing = "missing";
}

// Newest first. DumpFolder comes from the compiled freedom point.
public sealed record CrashReportList(
    IReadOnlyList<CrashReport> Reports,
    string DumpFolder);

public sealed record CrashReportPackageResult(CrashReport Report, CrashReportPackage Package);

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

    CrashReportAcknowledgement? Acknowledge(string id);

    bool Delete(string id);
}
