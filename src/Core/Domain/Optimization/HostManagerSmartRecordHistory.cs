namespace ResourceManager.App.Domain.Optimization;

public sealed record HostManagerSmartRecordHistoryEntry(
    long Sequence,
    string State,
    string Kind,
    string RecordId,
    string TargetId,
    string? SoftwareId,
    string ResourceKind,
    DateTimeOffset AppliedAt,
    int? ProcessId,
    string? ProcessStartKey,
    string? ProcessName,
    DateTimeOffset RecordedAt,
    DateTimeOffset RestoredAt,
    string RestoreResult);

public sealed record HostManagerSmartRecordHistoryPage(
    IReadOnlyList<HostManagerSmartRecordHistoryEntry> Records,
    long? NextBeforeSequence);
