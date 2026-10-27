using ResourceManager.App.Domain.Settings;

namespace ResourceManager.App.Application.MemoryProtection;

public sealed record HostMemoryProcess(int ProcessId, string ProcessStartKey, string Name, ulong? PrivateCommitBytes)
{
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
public sealed record HostMemoryActionResult(string Outcome, string? Reason, string? DumpPath, ulong BytesWritten)
{
    public bool SuspendFailed { get; init; }
}
public sealed record HostMemoryActionRecord(DateTimeOffset At, int ProcessId, string ProcessStartKey, string Name,
    ulong BaselineBytes, ulong CurrentBytes, decimal GrowthBytes, string? DumpPath, ulong BytesWritten, string Outcome, string? Reason);
public sealed record HostMemoryProtectionStatus(bool Enabled, double ThresholdPercent, double CheckIntervalSeconds,
    string DumpDirectory, decimal? TriggerBytes, decimal? RemainingBytes, HostMemoryCommit? CurrentCommit,
    NormalRunningPointSummary? NormalRunningPoint, string? InactiveReason,
    IReadOnlyList<HostMemoryExclusion> ExcludedCandidates, int ActionRecordLimit, IReadOnlyList<HostMemoryActionRecord> Actions);

// The source only subscribes to existing producers and projects their current values.
public interface IHostMemoryProtectionSource
{
    HostMemoryCommit? ReadCurrentCommit();
    IAsyncEnumerable<HostMemoryReading> SubscribeAsync(string holder, TimeSpan interval, CancellationToken cancellationToken);
}
public interface IHostMemoryProtectionExecutor
{
    IHostMemoryProtectionTarget Open(HostMemoryProcess process);
}
public interface IHostMemoryProtectionTarget : IDisposable
{
    string? ExclusionReason { get; }
    HostMemoryActionResult Execute(string dumpDirectory);
}

public sealed class HostMemoryProtectionSettingsException(string reason) : ArgumentException(reason)
{
    public string Reason { get; } = reason;
}
