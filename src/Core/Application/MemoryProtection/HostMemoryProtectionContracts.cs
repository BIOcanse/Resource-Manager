using ResourceManager.App.Domain.Settings;

namespace ResourceManager.App.Application.MemoryProtection;

public sealed record HostMemoryProcess(int ProcessId, string ProcessStartKey, string Name, ulong? PrivateCommitBytes)
{
    public string? SoftwareId { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsResourceManager { get; init; }
}

public sealed record HostMemoryCommit(ulong CurrentBytes, ulong LimitBytes, double Percent);
public sealed record HostMemoryReading(DateTimeOffset At, HostMemoryCommit? Commit, IReadOnlyList<HostMemoryProcess>? Processes);
public sealed record NormalRunningPoint(DateTimeOffset At, HostMemoryCommit Commit, IReadOnlyList<HostMemoryProcess> Processes)
{
    public int ProcessCount => Processes.Count;
}
public sealed record NormalRunningPointSummary(DateTimeOffset At, int ProcessCount);
public sealed record HostMemoryExclusion(int ProcessId, string ProcessStartKey, string Name, string Reason);
public sealed record HostMemoryActionResult(string Outcome, string? Reason, IReadOnlyList<string> DumpPaths, ulong BytesWritten)
{
    public bool SuspendFailed { get; init; }
}
public sealed record HostMemoryActionRecord(DateTimeOffset At, int ProcessId, string ProcessStartKey, string Name,
    ulong BaselineBytes, ulong CurrentBytes, decimal GrowthBytes, IReadOnlyList<string> DumpPaths, ulong BytesWritten, string Outcome, string? Reason);
public sealed record HostMemoryProtectionStatus(bool Enabled, double ThresholdPercent, double CheckIntervalSeconds,
    HostMemoryReserveStatus Reserve, decimal? TriggerBytes, decimal? RemainingBytes, HostMemoryCommit? CurrentCommit,
    NormalRunningPointSummary? NormalRunningPoint, string? InactiveReason,
    IReadOnlyList<HostMemoryExclusion> ExcludedCandidates, int ActionRecordLimit, IReadOnlyList<HostMemoryActionRecord> Actions)
{
    public string ActionMode { get; init; } = HostMemoryProtectionActionModes.DumpThenTerminate;
}

// The selection service resolves settings for this action; the executor owns process effects.
public sealed record HostMemoryActionOptions(string ActionMode = HostMemoryProtectionActionModes.DumpThenTerminate,
    bool SoftwareNoDump = false);

// The source only subscribes to existing producers and projects their current values.
public interface IHostMemoryProtectionSource
{
    Task<HostMemoryCommit?> ReadCurrentCommitAsync(CancellationToken cancellationToken);
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
