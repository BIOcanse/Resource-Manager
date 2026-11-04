using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Domain.Settings;
using ResourceManager.App.Infrastructure.NativeCore;

namespace ResourceManager.App.Infrastructure.Optimization;

public sealed partial class HostManagerSmartCoordinator
{
    // Each owner supplies a synchronized current value; no round gate or workspace borrow.
    private async Task<HostManagerSmartCoordinatorStatus> CreateStatusAsync(
        string mode,
        HostManagerRollbackStateDocument state,
        CancellationToken cancellationToken,
        PublishedObservation? observation = null)
    {
        observation ??= Volatile.Read(ref publishedObservation);
        var normalizedMode = HostManagerOptimizationModes.Normalize(mode);
        var faultClosedReason = ReadFaultClosedReason();
        // 账本尚未打开时还没有已知的所有权，和原生工作区尚未建立时一样计为没有对象。
        var ownership = await nativeActionTransactions.AppliedOwnership
            .TryReadSnapshotAsync(cancellationToken);
        IReadOnlyCollection<NativeAppliedOwnershipProcessIdentity> memoryOwners = ownership is null
            ? []
            : CreateAppliedOwnershipFactIndex(ownership).MemoryProcessRecords.Keys.ToArray();
        return new HostManagerSmartCoordinatorStatus(
            normalizedMode,
            schedulerRunning
                && observation.Decisions.Failure is null
                && normalizedMode != AppOptimizationModes.Normal
                && faultClosedReason is null,
            state.LastRunAt,
            state.LastRestoreAt,
            checked((int)(observation.NativeSnapshot?.PendingCount ?? 0)),
            CountAppliedTargets(state.AppliedPlacements, observation.NativeRows, memoryOwners),
            faultClosedReason,
            state.Coordinator)
        {
            Failure = observation.Decisions.Failure,
            FactExclusionCount = observation.Decisions.Failure is null ? 0 : observation.Decisions.FactExclusionCount,
            FactExclusionCounts = observation.Decisions.Failure is null ? [] : observation.Decisions.FactExclusionCounts,
            FactExclusions = observation.Decisions.Failure is null ? [] : observation.Decisions.FactExclusions
        };
    }

    /// <summary>
    /// 验证作用域故障或过期时，运行时拒绝一切新的效果准入（故障关闭）。返回原因码；正常准入时返回 null。
    /// 作用域的状态推导只在作用域权威里做一次，这里直接取它的状态。
    /// </summary>
    private string? ReadFaultClosedReason()
    {
        var scope = processEffectValidationScopeAuthorityOwner.GetStatus();
        return scope.State is "faultedBlocked" or "expiredBlocked"
            ? scope.Failure ?? scope.State
            : null;
    }

    /// <summary>
    /// 产品当前持有改动的对象数：旧式放置回执、原生行上的进程所有权，以及账本里的进程内存优先级所有权。
    /// 内存优先级归属到它所在的进程对象，同一进程的多项改动只算一个对象。
    /// </summary>
    internal static int CountAppliedTargets(
        IReadOnlyList<HostManagerAppliedPlacementReceipt> placements,
        ReadOnlySpan<NativeSmartCoordinatorSnapshotRow> rows,
        IEnumerable<NativeAppliedOwnershipProcessIdentity> memoryOwners)
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
            if (row.RowKind == NativeSmartCoordinatorSnapshotRowKind.Process
                && row.Flags.HasFlag(NativeSmartCoordinatorSnapshotRowFlags.ProcessOwned))
            {
                targets.Add(row.TargetKey);
            }
        }

        foreach (var owner in memoryOwners)
        {
            targets.Add(NativeStableIdentity.CreateCaseInsensitiveKey(
                HostManagerTargetIdentity.CreateProcessTargetId(
                    checked((int)owner.ProcessId),
                    owner.ProcessStartKey)));
        }
        return targets.Count;
    }
}
