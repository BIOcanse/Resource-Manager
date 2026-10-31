using System.Text.Json.Serialization;
using ResourceManager.App.Domain.Settings;

namespace ResourceManager.App.Application.CrashReports;

// One executable image RM ships and the role the crash list names it by.
public sealed record CrashReportImage(
    [property: JsonPropertyName("image")] string Image,
    [property: JsonPropertyName("role")] string Role);

/// <summary>Immutable compiled backend freedom point <c>service_recovery.crash_reports</c>
/// (docs/crash_dumps_2026-10-31.md). Every behaviour-changing value of the crash-dump feature is declared here.</summary>
public sealed record CrashReportOptions(
    [property: JsonPropertyName("beta_default_mode")] string BetaDefaultMode,
    [property: JsonPropertyName("stable_default_mode")] string StableDefaultMode,
    [property: JsonPropertyName("beta_version_marker")] string BetaVersionMarker,
    [property: JsonPropertyName("images")] IReadOnlyList<CrashReportImage> Images,
    [property: JsonPropertyName("folder_name")] string FolderName,
    [property: JsonPropertyName("reports_folder_name")] string ReportsFolderName,
    [property: JsonPropertyName("users_may_write_dumps")] bool UsersMayWriteDumps,
    [property: JsonPropertyName("mini_dump_custom_flags")] int MiniDumpCustomFlags,
    [property: JsonPropertyName("keep_count_maximum")] int KeepCountMaximum,
    [property: JsonPropertyName("record_capacity")] int RecordCapacity,
    [property: JsonPropertyName("subscriber_capacity")] int SubscriberCapacity,
    [property: JsonPropertyName("dump_scan_max_entries")] int DumpScanMaxEntries,
    [property: JsonPropertyName("event_match_window_seconds")] int EventMatchWindowSeconds,
    [property: JsonPropertyName("event_merge_window_seconds")] int EventMergeWindowSeconds,
    [property: JsonPropertyName("event_query_max_events")] int EventQueryMaxEvents,
    [property: JsonPropertyName("first_scan_event_lookback_seconds")] int FirstScanEventLookbackSeconds,
    [property: JsonPropertyName("managed_stack_max_chars")] int ManagedStackMaxChars,
    [property: JsonPropertyName("service_log_before_seconds")] int ServiceLogBeforeSeconds,
    [property: JsonPropertyName("service_log_after_seconds")] int ServiceLogAfterSeconds,
    [property: JsonPropertyName("service_log_max_entries")] int ServiceLogMaxEntries,
    [property: JsonPropertyName("version_history_capacity")] int VersionHistoryCapacity,
    [property: JsonPropertyName("issue_attachment_limit_bytes")] long IssueAttachmentLimitBytes,
    [property: JsonPropertyName("issue_new_url")] string IssueNewUrl,
    [property: JsonPropertyName("issue_url_max_chars")] int IssueUrlMaxChars,
    [property: JsonPropertyName("issue_stack_max_lines")] int IssueStackMaxLines)
{
    public CrashReportOptions Validate()
    {
        if (!AppCrashDumpModes.IsResolved(BetaDefaultMode) || !AppCrashDumpModes.IsResolved(StableDefaultMode))
            throw new InvalidDataException("Crash dump channel defaults must be ask, on or off.");
        if (string.IsNullOrEmpty(BetaVersionMarker))
            throw new InvalidDataException("The beta version marker must not be empty.");
        if (Images is not { Count: > 0 } || Images.Any(static image => image is null
                || string.IsNullOrWhiteSpace(image.Image) || image.Image.IndexOfAny(['\\', '/']) >= 0
                || !image.Image.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                || image.Role is not ("service" or "window" or "resident"))
            || Images.Select(static image => image.Image).Distinct(StringComparer.OrdinalIgnoreCase).Count() != Images.Count)
            throw new InvalidDataException("Crash dump images must be distinct executable names with a service, window or resident role.");
        if (!PlainName(FolderName) || !PlainName(ReportsFolderName)
            || string.Equals(FolderName, ReportsFolderName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The crash dump and report folders must be distinct plain directory names.");
        if (MiniDumpCustomFlags <= 0 || KeepCountMaximum < 1 || RecordCapacity < 1 || SubscriberCapacity < 1 || DumpScanMaxEntries < 1
            || EventMatchWindowSeconds < 1 || EventMergeWindowSeconds < 0 || EventQueryMaxEvents < 1
            || FirstScanEventLookbackSeconds < 0 || ManagedStackMaxChars < 1 || ServiceLogBeforeSeconds < 0
            || ServiceLogAfterSeconds < 0 || ServiceLogMaxEntries < 0 || VersionHistoryCapacity < 1
            || IssueAttachmentLimitBytes < 1 || IssueUrlMaxChars < 1 || IssueStackMaxLines < 0)
            throw new InvalidDataException("A crash report bound is out of range.");
        if (!Uri.TryCreate(IssueNewUrl, UriKind.Absolute, out var issue) || issue.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(issue.Query) || !string.IsNullOrEmpty(issue.Fragment))
            throw new InvalidDataException("The issue URL must be an absolute https URL without query or fragment.");
        if ((IssueNewUrl + "?title=&body=").Length > IssueUrlMaxChars)
            throw new InvalidDataException("The crash issue URL limit must hold the configured base URL and query keys.");
        return this;
    }

    private static bool PlainName(string? name)
        => !string.IsNullOrWhiteSpace(name) && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && name is not ("." or "..");

    public string? RoleOf(string image)
        => Images.FirstOrDefault(entry => string.Equals(entry.Image, image, StringComparison.OrdinalIgnoreCase))?.Role;
}
