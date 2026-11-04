namespace ResourceManager.App.Infrastructure.CrashReports;

// One Application log crash event of an RM image: Application Error 1000 or .NET Runtime 1026.
public sealed record CrashEvent(
    DateTimeOffset TimeCreated,
    int EventId,
    string Image,
    string? Module,
    string? Offset,
    string? ExceptionCode,
    string? Description,
    string? ManagedStack,
    string Text);

// One Application log entry written by the service's own log source.
public sealed record ServiceLogEntry(DateTimeOffset TimeCreated, string Level, string Text);

// The seam between the crash-report owner and the Windows event log. Tests use a fake.
public interface ICrashEventSource
{
    // 1000/1026 events of the given images in [from, to), oldest first, at most max.
    IReadOnlyList<CrashEvent> ReadCrashEvents(DateTimeOffset from, DateTimeOffset to, IReadOnlyCollection<string> images, int max);

    // Application log entries of the service's source in [from, to), oldest first, at most max.
    IReadOnlyList<ServiceLogEntry> ReadServiceLog(string source, DateTimeOffset from, DateTimeOffset to, int max);
}
