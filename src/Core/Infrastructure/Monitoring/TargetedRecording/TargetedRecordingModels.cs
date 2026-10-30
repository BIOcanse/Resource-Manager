using ResourceManager.App.Domain.Settings;
using ResourceManager.ReadWrite.Windows.Frames;
namespace ResourceManager.App.Infrastructure.Monitoring.TargetedRecording;

public sealed record TargetedRecordingHeader(
    string Id,
    string SoftwareId,
    string SoftwareName,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    int MaximumDurationSeconds,
    string Status,
    string? StopReason,
    bool Incomplete)
{
    /// <summary>The profile the request named, or null when the request carried its own items.</summary>
    public string? ProfileId { get; init; }
    public string? ProfileName { get; init; }
    /// <summary>Snapshot of the recorded items taken at start; actual sources, intervals and gaps are
    /// written when the recording finishes. Later profile edits never change it.</summary>
    public IReadOnlyList<TargetedRecordingItemReport> Items { get; init; } = [];
    /// <summary>Every target process seen during the recording, merged as processes appear.</summary>
    public IReadOnlyList<TargetedRecordingProcess> Processes { get; init; } = [];
    public long StoredBytes { get; init; }
    /// <summary>The per-report size cap in effect for this recording (0 for reports made before the cap).</summary>
    public int MaximumReportKib { get; init; }
}

public static class TargetedRecordingStopReasons
{
    public const string UserStop = "user-stop";
    public const string MaximumDuration = "max-duration";
    public const string AllProcessesExited = "all-processes-exited";
    public const string NoProcess = "no-process";
    public const string Interrupted = "interrupted";
    public const string CaptureError = "capture-error";
    public static readonly IReadOnlyList<string> All =
        [UserStop, MaximumDuration, AllProcessesExited, NoProcess, Interrupted, CaptureError];
}

public static class TargetedRecordingGapReasons
{
    /// <summary>The item was not in the live catalog when the recording started.</summary>
    public const string ItemUnavailable = "item-unavailable";
    /// <summary>The item's source published no current value for this span.</summary>
    public const string ValueUnavailable = "value-unavailable";
    /// <summary>The report reached its size cap; nothing more was stored.</summary>
    public const string SizeLimit = "size-limit";
    /// <summary>The frame ledger reported an incomplete batch.</summary>
    public const string FramesIncomplete = "frames-incomplete";
    /// <summary>More than the frame ledger window passed between reads; older presents were lost.</summary>
    public const string FramesWindowSkipped = "frames-window-skipped";
    public static readonly IReadOnlyList<string> All =
        [ItemUnavailable, ValueUnavailable, SizeLimit, FramesIncomplete, FramesWindowSkipped];
}

public static class TargetedRecordingIntervalOrigins
{
    /// <summary>The item named its own interval.</summary>
    public const string Item = "item";
    /// <summary>The item's source publishes a natural interval and the item named none.</summary>
    public const string Natural = "natural";
    /// <summary>Neither the item nor its source names an interval; the profile default applies.</summary>
    public const string ProfileDefault = "profile-default";
    /// <summary>Frames are recorded once per present.</summary>
    public const string PerPresent = "per-present";
    public static readonly IReadOnlyList<string> All = [Item, Natural, ProfileDefault, PerPresent];
}

/// <summary>One recorded item: what was selected and what the sources actually delivered.</summary>
public sealed record TargetedRecordingItemReport(
    string Kind,
    string Id,
    string Unit,
    string SelectedSource,
    IReadOnlyList<string> ActualSources,
    int? SelectedIntervalMs,
    string IntervalOrigin,
    int? ActualIntervalMs,
    string SyncPrecision,
    long PointCount,
    IReadOnlyList<TargetedRecordingGap> Gaps);

/// <summary>A span (milliseconds from the recording start) in which the item has no stored values.</summary>
public sealed record TargetedRecordingGap(long FromMs, long ToMs, string Reason);

public sealed record TargetedRecordingProcess(
    int ProcessId,
    long ProcessStartKey,
    string Name,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset? ExitedAt);

public sealed record TargetedProcessIdentity(int ProcessId, long ProcessStartKey);

/// <summary>One stored sample row. Software holds the target software's values for software items and
/// System the metric item values, both keyed by item id, only for items due at this time. Processes is
/// empty in reports with a header process list.</summary>
public sealed record TargetedResourceSample(
    DateTimeOffset CapturedAt,
    IReadOnlyDictionary<string, double?> Software,
    IReadOnlyDictionary<string, double?> System,
    IReadOnlyList<TargetedProcessIdentity> Processes);

public sealed record FpsDistributionBucket(int FromFps, int ToFps, int Count);

public sealed record TargetedRecordingReport(
    TargetedRecordingHeader Recording,
    FrameIntervalStatistics? Summary,
    IReadOnlyList<FrameIntervalSample> FrameIntervals,
    IReadOnlyList<FpsDistributionBucket> FpsDistribution,
    IReadOnlyList<TargetedResourceSample> ResourceSamples);

/// <summary>The JSON export: the whole report plus the export schema version.</summary>
public sealed record TargetedRecordingJsonExport(
    int SchemaVersion,
    TargetedRecordingHeader Recording,
    FrameIntervalStatistics? Summary,
    IReadOnlyList<FrameIntervalSample> FrameIntervals,
    IReadOnlyList<FpsDistributionBucket> FpsDistribution,
    IReadOnlyList<TargetedResourceSample> ResourceSamples)
{
    public const int CurrentSchemaVersion = 1;
}

/// <summary>A software with at least one running attributed process, as the start check sees it.</summary>
public sealed record TargetedRecordingTarget(string SoftwareId, string Name, int ProcessCount);

/// <summary>One recordable item with its unit, the concrete sources a profile may name, and where it can be
/// used: a recording profile, the overlay, or both.</summary>
public sealed record TargetedRecordingCatalogItem(
    string Kind,
    string Id,
    string Unit,
    IReadOnlyList<string> Sources,
    int? NaturalIntervalMs,
    IReadOnlyList<string> Uses);

public static class TargetedRecordingItemUses
{
    public const string Recording = "recording";
    public const string Overlay = "overlay";
    public static readonly IReadOnlyList<string> Both = [Recording, Overlay];
    public static readonly IReadOnlyList<string> OverlayOnly = [Overlay];
}

/// <summary>Start failures the endpoint maps to status codes; Code is the wire reason.</summary>
public sealed class TargetedRecordingStartException(int statusCode, string code) : Exception(code)
{
    public int StatusCode { get; } = statusCode;
    public string Code { get; } = code;
}
