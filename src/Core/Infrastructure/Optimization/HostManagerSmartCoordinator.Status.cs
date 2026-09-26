using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Domain.Settings;
using ResourceManager.App.Infrastructure.NativeCore;

namespace ResourceManager.App.Infrastructure.Optimization;

public sealed partial class HostManagerSmartCoordinator
{
    private HostManagerSmartCoordinatorStatus CreateStatus(
        string mode,
        HostManagerRollbackStateDocument state,
        bool running,
        uint nativePendingCount)
    {
        var normalizedMode = HostManagerOptimizationModes.Normalize(mode);
        var schedulingEnabled = normalizedMode != AppOptimizationModes.Normal;
        return new HostManagerSmartCoordinatorStatus(
            normalizedMode,
            running && schedulingEnabled,
            state.LastRunAt,
            state.LastRestoreAt,
            checked((int)nativePendingCount),
            CountAppliedTargets(
                state.AppliedPlacements,
                nativeWorkspace is null
                    ? ReadOnlySpan<NativeSmartCoordinatorSnapshotRow>.Empty
                    : nativeWorkspace.CurrentSnapshotRows),
            state.Message);
    }

    internal static int CountAppliedTargets(
        IReadOnlyList<HostManagerAppliedPlacementReceipt> placements,
        ReadOnlySpan<NativeSmartCoordinatorSnapshotRow> rows)
    {
        var targets = new HashSet<ulong>();
        foreach (var placement in placements)
        {
            if (placement.Records.Count != 0)
            {
                targets.Add(NativeStableIdentity.CreateCaseInsensitiveKey(placement.TargetId));
            }
        }

        foreach (var row in rows)
        {
            var ownership = row.RowKind == NativeSmartCoordinatorSnapshotRowKind.Process
                ? NativeSmartCoordinatorSnapshotRowFlags.ProcessOwned
                : NativeSmartCoordinatorSnapshotRowFlags.CpuOwned |
                    NativeSmartCoordinatorSnapshotRowFlags.GpuOwned;
            if ((row.Flags & ownership) != 0)
            {
                targets.Add(row.TargetKey);
            }
        }
        return targets.Count;
    }
}
