using ResourceManager.App.Domain.Optimization;

namespace ResourceManager.App.Endpoints.Transport;

internal sealed record HostManagerSmartRecordHistoryWirePage(
    IReadOnlyList<HostManagerSmartRecordHistoryWireSnapshot> Records,
    long? NextBeforeSequence);

internal sealed record HostManagerSmartRecordHistoryWireSnapshot(
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

internal sealed record HostManagerSmartRecordHistoryClearResponse(long RemovedCount);

internal static class HostManagerSmartRecordHistoryWireProjection
{
    internal static HostManagerSmartRecordHistoryWirePage Project(HostManagerSmartRecordHistoryPage page)
        => new(page.Records.Select(entry => new HostManagerSmartRecordHistoryWireSnapshot(
            entry.Sequence, entry.State, entry.Kind, entry.RecordId, entry.TargetId, entry.SoftwareId, entry.ResourceKind,
            entry.AppliedAt, entry.ProcessId, entry.ProcessStartKey, entry.ProcessName,
            entry.RecordedAt, entry.RestoredAt, entry.RestoreResult)).ToArray(), page.NextBeforeSequence);
}
