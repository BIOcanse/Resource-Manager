using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Domain.Optimization;

namespace ResourceManager.App.Endpoints.Transport;

internal sealed record HostManagerSmartStateWireSnapshot(
    int Version,
    DateTimeOffset? LastRunAt,
    DateTimeOffset? LastRestoreAt,
    IReadOnlyList<HostManagerPlacementWireSnapshot> AppliedPlacements,
    HostManagerCoordinatorFacts? Coordinator,
    ulong NativeHostSessionIncarnation);

internal sealed record HostManagerPlacementWireSnapshot(
    string TargetId,
    string? SoftwareId,
    string ResourceKind,
    IReadOnlyList<HostManagerAppliedRecordWireSnapshot> Records,
    DateTimeOffset AppliedAt,
    DateTimeOffset UpdatedAt,
    int? ProcessId,
    string? ProcessStartKey,
    string? ProcessName);

internal sealed record HostManagerAppliedRecordWireSnapshot(
    string Kind,
    string RecordId,
    long? Sequence,
    string? State,
    DateTimeOffset? RecordedAt);

internal static class HostManagerSmartStateWireProjection
{
    internal static async Task<HostManagerSmartStateWireSnapshot> ReadAsync(
        IHostManagerSmartCoordinator coordinator, CancellationToken cancellationToken)
        => Project(await coordinator.GetStateAsync(cancellationToken));

    internal static HostManagerSmartStateWireSnapshot Project(HostManagerRollbackStateDocument state)
        => new(state.Version, state.LastRunAt, state.LastRestoreAt,
            state.AppliedPlacements.Select(placement => new HostManagerPlacementWireSnapshot(
                placement.TargetId, placement.SoftwareId, placement.ResourceKind,
                placement.Records.Select(record => new HostManagerAppliedRecordWireSnapshot(
                    record.Kind, record.RecordId, record.Sequence,
                    record.Sequence.HasValue ? "active" : null, record.RecordedAt)).ToArray(),
                placement.AppliedAt, placement.UpdatedAt, placement.ProcessId, placement.ProcessStartKey, placement.ProcessName)).ToArray(),
            state.Coordinator, state.NativeHostSessionIncarnation);
}
