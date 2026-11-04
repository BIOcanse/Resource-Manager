namespace ResourceManager.App.Application.CpuMonopolyProtection;

public sealed record CpuMonopolyProcess(int ProcessId, string ProcessStartKey, string Name,
    double? CpuPercent, string? PriorityClass, string? ExecutablePath = null, bool IsResourceManager = false);
public sealed record CpuMonopolyReading(DateTimeOffset At, IReadOnlyList<CpuMonopolyProcess>? Processes, bool Complete = true);
public interface ICpuMonopolyProtectionSource
{
    IAsyncEnumerable<CpuMonopolyReading> SubscribeAsync(string holder, TimeSpan interval, CancellationToken cancellationToken);
}
public sealed record CpuMonopolyExclusion(int ProcessId, string ProcessStartKey, string Name, string Reason);
public sealed record CpuMonopolyActionRecord(string ActionId, int ProcessId, string ProcessStartKey, string Name,
    DateTimeOffset At, double CpuPercent, string PreviousPriorityClass, double ThresholdPercent, double SustainedSeconds,
    string State, string Outcome, string? Reason, string? Detail, DateTimeOffset? EndedAt = null, string? EndReason = null);
public sealed record CpuMonopolyProtectionStatus(bool Enabled, bool Active, string? InactiveReason,
    double ThresholdPercent, double SustainedSeconds, double CheckIntervalSeconds, string? CurrentActionId,
    IReadOnlyList<CpuMonopolyActionRecord> Actions, int RecordCapacity, int UnrecordedRecordCount,
    IReadOnlyList<CpuMonopolyExclusion> ExcludedCandidates);
public sealed record CpuMonopolyProtectionEvent(string Kind, string? Reason = null, CpuMonopolyActionRecord? Record = null);
public sealed record CpuMonopolyPublication(CpuMonopolyProtectionStatus Status,
    IReadOnlyList<CpuMonopolyProtectionEvent> Events, bool IsReplay = false, bool RecordsChanged = false);
