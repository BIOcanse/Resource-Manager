using ResourceManager.App.Domain.Settings;

namespace ResourceManager.App.Application.MemoryProtection;

public sealed record HostMemoryProcess(int ProcessId, string ProcessStartKey, string Name, ulong? PrivateCommitBytes)
{
    public string? SoftwareId { get; init; }
    public string? ExecutablePath { get; init; }
    public int? ParentProcessId { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsResourceManager { get; init; }
}

public sealed record HostMemoryCommit(ulong CurrentBytes, ulong LimitBytes, double Percent);
public sealed record HostMemoryReading(DateTimeOffset At, HostMemoryCommit? Commit, IReadOnlyList<HostMemoryProcess>? Processes);
public sealed record NormalRunningPoint(DateTimeOffset At, HostMemoryCommit Commit, IReadOnlyList<HostMemoryProcess> Processes)
{
    public required string Id { get; init; }
    public int ProcessCount => Processes.Count;
}
public sealed record NormalRunningPointSummary(string Id, DateTimeOffset At, int ProcessCount, HostMemoryCommit Commit);
// The saved normal running points, oldest first, and the one in effect (null: none in effect, protection inactive).
public sealed record NormalRunningPointList(string? ActiveId, IReadOnlyList<NormalRunningPointSummary> Points, int Limit);
public sealed record NormalRunningPointSelection(string Id);
public sealed record HostMemoryExclusion(int ProcessId, string ProcessStartKey, string Name, string Reason);
public sealed record HostMemoryActionResult(string Outcome, string? Reason, IReadOnlyList<string> DumpPaths, ulong BytesWritten)
{
    public string? Detail { get; init; }
    public bool SuspendFailed { get; init; }
}
public sealed record HostMemoryActionRecord(DateTimeOffset At, int ProcessId, string ProcessStartKey, string Name,
    ulong? BaselineBytes, ulong? CurrentBytes, decimal? GrowthBytes, IReadOnlyList<string> DumpPaths, ulong BytesWritten, string Outcome, string? Reason,
    HostMemorySelectionBasis SelectionBasis)
{
    public string ActionId { get; init; } = "";
    public string TargetKind { get; init; } = "process";
    public HostMemoryTreeRoot? TreeRoot { get; init; }
    public string? Detail { get; init; }
    // A record is written once its process is handled, so it is a history report entry from the start
    // (docs/report_lifecycle_2026-10-28.md); an action in progress is the status currentActionId.
    public HostMemoryActionRecordState State { get; init; } = HostMemoryActionRecordState.History;
}
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<HostMemoryActionRecordState>))]
public enum HostMemoryActionRecordState
{
    [System.Text.Json.Serialization.JsonStringEnumMemberName("history")] History
}
public sealed record HostMemorySelectionBasis(string Rule, decimal? ProcessGrowthBytes,
    decimal? DirectChildrenMemoryGrowthBytes, long MinimumProcessGrowthBytes);
public sealed record HostMemoryProtectionStatus(bool Enabled, double ThresholdPercent, double CheckIntervalSeconds,
    HostMemoryReserveStatus Reserve, decimal? TriggerBytes, decimal? RemainingBytes, HostMemoryCommit? CurrentCommit,
    NormalRunningPointSummary? NormalRunningPoint, string? InactiveReason,
    IReadOnlyList<HostMemoryExclusion> ExcludedCandidates, int RecordCapacity, IReadOnlyList<HostMemoryActionRecord> Actions)
{
    // Records a full report could not write; clearing history resets it with the records it frees room for.
    public int UnrecordedRecordCount { get; init; }
    public string ActionMode { get; init; } = HostMemoryProtectionActionModes.DumpThenTerminate;
    public bool Active => InactiveReason is null;
    public bool? AtOrAboveThreshold { get; init; }
    public string? CurrentActionId { get; init; }
    public long MinimumProcessGrowthBytes { get; init; } = 1073741824;
    public IReadOnlyList<string> ProtectedProcesses { get; init; } = ["explorer.exe"];
    public string? DecisionReason { get; init; }
}

public sealed record HostMemoryTreeRoot(int ProcessId, string? ProcessStartKey, string? Name);

public sealed record HostMemoryProtectionEvent(string Kind, string? Reason = null, HostMemoryActionRecord? Action = null);
public sealed record HostMemoryProtectionPublication(HostMemoryProtectionStatus Status, IReadOnlyList<HostMemoryProtectionEvent> Events)
{
    public NormalRunningPoint? NormalRunningPoint { get; init; }
    public bool PointChanged { get; init; }
    public NormalRunningPointList? NormalRunningPoints { get; init; }
    public bool PointsChanged { get; init; }
    // The record list was replaced (history cleared) or its unrecorded count changed; appended records are events.
    public bool RecordsChanged { get; init; }
    public bool IsReplay { get; init; }
}

// ReadVolatility: EventDriven, change event: normal-running-point-changed (create, select, delete of the point in effect).
public sealed record HostMemoryProtectionPointFrame(NormalRunningPoint? NormalRunningPoint)
{
    public string Type => "normalRunningPoint";
}

// ReadVolatility: EventDriven. Sent on subscribe and whenever the saved points or the one in effect change,
// so a point created or deleted elsewhere reaches every page without a GET.
public sealed record HostMemoryProtectionPointsFrame(NormalRunningPointList NormalRunningPoints)
{
    public string Type => "normalRunningPoints";
}

// The selection service resolves settings for this action; the executor owns process effects.
public sealed record HostMemoryActionOptions(string ActionMode = HostMemoryProtectionActionModes.DumpThenTerminate,
    bool SoftwareNoDump = false)
{
    public IReadOnlyList<string> ProtectedProcesses { get; init; } = [];
}

// The source only subscribes to existing producers and projects their current values.
public interface IHostMemoryProtectionSource
{
    Task<HostMemoryCommit?> ReadCurrentCommitAsync(CancellationToken cancellationToken);
    // Machine commit only; every reading has Processes == null. The protection check and its status hold this.
    IAsyncEnumerable<HostMemoryReading> SubscribeCommitAsync(string holder, TimeSpan interval, CancellationToken cancellationToken);
    // One fresh per-process reading, requested only when commit reached the threshold; null when inventory is unavailable.
    Task<IReadOnlyList<HostMemoryProcess>?> ReadProcessesOnceAsync(string holder, CancellationToken cancellationToken);
    // Commit and per-process data together; only normal running point creation holds this.
    IAsyncEnumerable<HostMemoryReading> SubscribeAsync(string holder, TimeSpan interval, CancellationToken cancellationToken);
}
public interface IHostMemoryProtectionExecutor
{
    IHostMemoryProtectionTarget Open(HostMemoryProcess process);
}
public interface IHostMemoryProtectionTarget : IDisposable
{
    string? ExclusionReason { get; }
    HostMemoryActionResult Execute(IHostMemoryProtectionReserve reserve, HostMemoryActionOptions? options = null);
}

public sealed record HostMemoryReserveEntryStatus(string Location, ulong ConfiguredSizeBytes, bool Built, string? Reason, ulong RemainingBytes)
{
    public ulong ClusterBytes { get; init; }
}
public sealed record HostMemoryReserveStatus(IReadOnlyList<HostMemoryReserveEntryStatus> Entries,
    ulong ConfiguredSizeBytes, bool Built, string? Reason, ulong RemainingBytes);

// Owns the preallocated file. A lease keeps rebuilds from changing space during one action.
public interface IHostMemoryProtectionReserve : IDisposable
{
    HostMemoryReserveStatus ReadStatus();
    HostMemoryReserveStatus Prepare(IReadOnlyList<HostMemoryReserveSettings> entries);
    HostMemoryReserveStatus Rebuild();
    IHostMemoryProtectionReserveLease? TryAcquire();
}
public interface IHostMemoryProtectionReserveEntry : IDisposable
{
    HostMemoryReserveEntryStatus ReadStatus();
    HostMemoryReserveEntryStatus Prepare(string location, ulong sizeBytes);
    HostMemoryReserveEntryStatus Rebuild();
    IHostMemoryProtectionReserveLease? TryAcquire();
}
public interface IHostMemoryProtectionReserveLease : IDisposable
{
    string Location { get; }
    ulong RemainingBytes { get; }
    IReadOnlyList<string> SlicePaths { get; }
    bool CanFit(ulong bytes);
    Stream CreateSlice(string fileName);
}

public sealed class HostMemoryProtectionSettingsException(string reason) : ArgumentException(reason)
{
    public string Reason { get; } = reason;
}
public sealed class NormalRunningPointException(string reason) : InvalidOperationException(reason)
{
    public string Reason { get; } = reason;
}
