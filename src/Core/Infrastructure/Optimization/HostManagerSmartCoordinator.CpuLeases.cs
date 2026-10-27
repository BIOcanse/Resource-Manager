using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Domain.CpuTopology;
using ResourceManager.App.Domain.GpuPlacement;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Domain.Settings;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.NativeCore;

namespace ResourceManager.App.Infrastructure.Optimization;

public sealed partial class HostManagerSmartCoordinator
{
    private readonly RuntimeCpuLeaseStore runtimeCpuLeases = cpuLeaseStore;
    private readonly Dictionary<string, string> cpuLeaseTargetFailures = new(StringComparer.Ordinal);
    private readonly Dictionary<string, uint> cpuPlacementWriteFailures = new(StringComparer.Ordinal);

    public Task<RuntimeCpuLeaseSnapshot?> ReadCpuLeaseAsync(string leaseId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Last completed observation is data, never starts sampling or performs writes.
        var snapshot = runtimeCpuLeases.Get(leaseId)?.Snapshot;
        return Task.FromResult(snapshot is null ? null : ProjectCpuLeaseAvailability(snapshot));
    }

    public RuntimeCpuLeaseSnapshot ProjectCpuLeaseAvailability(RuntimeCpuLeaseSnapshot snapshot)
    {
        lock (lifecycleSync)
            return LifecycleState == HostManagerSmartCoordinatorLifecycleState.Running ? snapshot
                : snapshot with { State = "unavailable", ReasonCode = "scheduler-stopped" };
    }

    private void PublishCpuLeasesUnavailable(string reason)
    {
        foreach (var lease in runtimeCpuLeases.Capture())
            runtimeCpuLeases.Publish(lease.Snapshot.LeaseId, timeProvider.GetUtcNow(), [], reason);
    }

    private void ExpireExitedCpuLeases(ResourceManager.App.Domain.ResourceBreakdown.SchedulingProcessFactSnapshot facts)
    {
        if (!facts.IsInventoryCurrentComplete()) return;
        foreach (var lease in runtimeCpuLeases.Capture())
            if (lease.Snapshot.Request.Target.ProcessId is { } pid
                && !facts.CurrentInventoryProcesses.Any(process => process.ProcessId == pid
                    && process.ProcessStartKey == lease.Snapshot.Request.Target.ProcessStartKey))
                runtimeCpuLeases.DeleteCpuLease(lease.Snapshot.LeaseId);
    }

    private static bool MatchesCpuLease(RuntimeCpuLeaseTarget target, HostManagerAutomaticPlacementProcess process)
        => target.SoftwareId is { } software ? software.Equals(process.SoftwareId, StringComparison.OrdinalIgnoreCase)
            : target.ProcessId is { } pid ? pid == process.ProcessId && target.ProcessStartKey == process.ProcessStartKey
            : string.Equals(target.ExecutablePath, process.ExecutablePath, StringComparison.OrdinalIgnoreCase);

    private IReadOnlyList<HostManagerAutomaticPlacementProcess> MergeCpuLeaseLocks(
        IReadOnlyList<HostManagerAutomaticPlacementProcess> processes, RuntimeCpuLeaseStore.Entry[] leases)
    {
        cpuLeaseTargetFailures.Clear();
        return processes.Select(process =>
        {
            var locks = leases.Where(lease => lease.Snapshot.Request.Lock && MatchesCpuLease(lease.Snapshot.Request.Target, process)).ToArray();
            if (locks.Length == 0) return process;
            var cores = locks[0].Snapshot.Request.PhysicalCoreIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var lease in locks.Skip(1)) cores.IntersectWith(lease.Snapshot.Request.PhysicalCoreIds);
            if (process.Policy.CpuManualLockedPositionIds.Count != 0) cores.IntersectWith(process.Policy.CpuManualLockedPositionIds);
            if (cores.Count == 0)
            {
                cpuLeaseTargetFailures[process.TargetId] = "cpu-lock-conflict";
                return process with { CanApplyPhysicalPlacement = false };
            }
            return process with { Policy = process.Policy with { CpuManualLockedPositionIds = cores.Order(StringComparer.OrdinalIgnoreCase).ToArray() } };
        }).ToArray();
    }

    private IReadOnlyList<HostManagerAutomaticPlacementProcess> AddCpuLeaseReservationFacts(
        IReadOnlyList<HostManagerAutomaticPlacementProcess> processes, RuntimeCpuLeaseStore.Entry[] leases,
        out IReadOnlyDictionary<string, IReadOnlySet<string>> owners)
    {
        var facts = processes.ToList();
        var membership = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal);
        foreach (var lease in leases.Where(lease => lease.Snapshot.Request.Exclusive))
        {
            var targets = processes.Where(process => process.CanApplyPhysicalPlacement && !process.Policy.CpuSchedulingExcluded
                && MatchesCpuLease(lease.Snapshot.Request.Target, process)).ToArray();
            if (targets.Length == 0) continue;
            var owner = "cpu-lease:" + lease.Snapshot.LeaseId;
            membership.Add(owner, targets.Select(process => process.TargetId).ToHashSet(StringComparer.Ordinal));
            facts.Add(targets[0] with
            {
                SoftwareId = owner, SoftwareKind = null,
                CanonicalCpuScore = targets.Sum(process => process.CanonicalCpuScore ?? 0),
                Policy = targets[0].Policy with { CpuManualExclusivePositionIds = lease.Snapshot.Request.PhysicalCoreIds }
            });
        }
        owners = membership;
        return facts;
    }

    private void PublishCpuLeaseEffects(RuntimeCpuLeaseStore.Entry[] leases,
        IReadOnlyList<HostManagerAutomaticPlacementProcess> processes, HostManagerCpuReservations? reservations,
        IReadOnlyList<HostManagerAppliedPlacementReceipt> placements, HostManagerSample sample)
    {
        var inventoryComplete = sample.ProcessFacts.IsInventoryCurrentComplete();
        foreach (var lease in leases)
        {
            var snapshot = lease.Snapshot;
            var matching = processes.Where(process => MatchesCpuLease(snapshot.Request.Target, process)).ToArray();
            var sharesReservation = snapshot.Request.Exclusive && reservations is not null
                && reservations.BySoftware.Any(pair => pair.Key != "cpu-lease:" + snapshot.LeaseId
                    && pair.Value.Any(core => snapshot.Request.PhysicalCoreIds.Contains(core, StringComparer.OrdinalIgnoreCase))
                    && processes.Any(process => !matching.Any(target => target.TargetId == process.TargetId)
                        && (process.SoftwareId == pair.Key || reservations.LeaseTargets.GetValueOrDefault(pair.Key)?.Contains(process.TargetId) == true)));
            var effects = new List<RuntimeCpuLeaseEffect>();
            foreach (var process in processes)
            {
                var isTarget = matching.Any(target => target.TargetId == process.TargetId);
                if (!isTarget && (!snapshot.Request.Exclusive || matching.Length == 0)) continue;
                // Only avoidance of cores actually admitted by the global merge belongs to this lease.
                var owner = "cpu-lease:" + snapshot.LeaseId;
                var granted = reservations?.BySoftware.GetValueOrDefault(owner);
                if (!isTarget && (granted is null || granted.Count == 0)) continue;
                var reason = cpuLeaseTargetFailures.GetValueOrDefault(process.TargetId);
                if (!process.CanApplyPhysicalPlacement || process.Policy.CpuSchedulingExcluded) reason ??= "cpu-placement-not-admitted";
                if (isTarget && snapshot.Request.Exclusive
                    && (granted is null || !snapshot.Request.PhysicalCoreIds.All(granted.Contains))) reason ??= "exclusive-reservation-not-granted";
                if (isTarget && sharesReservation) reason ??= "exclusive-reservation-shared-with-other-target";
                var receipt = placements.FirstOrDefault(item => item.TargetId == process.TargetId && item.ResourceKind == OptimizationResourceKinds.Cpu);
                var record = receipt?.Records.FirstOrDefault(item => item.Kind == HostManagerAppliedRecordKinds.CpuAffinity);
                var usesSets = OperatingSystem.IsWindowsVersionAtLeast(10);
                var read = processPolicyWriter.ReadPlacementStateForRecovery(process.ProcessId,
                    usesSets ? ProcessPlacementReadFields.DefaultCpuSets | ProcessPlacementReadFields.Affinity : ProcessPlacementReadFields.Affinity);
                var nativeError = read.NativeErrorCode;
                var state = "pending";
                var topology = runtimePlanProvider.Current.CpuPlacementTopology;
                var observedCores = read.Value is { } current && topology is not null
                    ? ObserveCpuLeaseCoreIds(topology, current) : null;
                if (read.Status != RecoveryReadStatus.Found || read.Value is null) reason ??= "process-binding-unavailable";
                else if (!MatchesAutomaticProcessIdentity(process, read.Value)) reason ??= "process-instance-changed";
                else if (reason is null)
                {
                    if (isTarget && !snapshot.Request.Lock) state = "observed";
                    else if (observedCores is { Count: > 0 } && (isTarget
                        ? observedCores.All(core => snapshot.Request.PhysicalCoreIds.Contains(core, StringComparer.OrdinalIgnoreCase))
                        : !observedCores.Any(core => snapshot.Request.PhysicalCoreIds.Contains(core, StringComparer.OrdinalIgnoreCase)))) state = "observed";
                    else reason = "cpu-binding-not-observed";
                }
                if (cpuPlacementWriteFailures.TryGetValue(process.TargetId, out var writeError))
                {
                    state = "failed"; reason = "cpu-placement-write-failed"; nativeError = unchecked((int)writeError);
                }
                else if (reason is "cpu-lock-conflict" or "exclusive-reservation-not-granted" or "cpu-placement-not-admitted"
                    or "exclusive-reservation-shared-with-other-target" or "cpu-placement-outside-original-range") state = "failed";
                effects.Add(new(process.ProcessId, process.ProcessStartKey, process.SoftwareId, isTarget ? "target" : "avoidance",
                    state, reason, nativeError, read.Value?.DefaultCpuSetIds, read.Value?.ProcessorAffinityMask,
                    observedCores, state == "observed" && record is not null && read.Value is not null
                        && CpuLeaseReceiptMatchesBinding(record, read.Value)
                        && (isTarget ? snapshot.Request.Lock
                            : PreviousCpuLeaseRangeIntersected(topology!, record, snapshot.Request.PhysicalCoreIds))));
            }
            runtimeCpuLeases.Publish(snapshot.LeaseId, timeProvider.GetUtcNow(), effects,
                !inventoryComplete ? "process-inventory-unavailable" : reservations is null ? "cpu-reservations-unavailable"
                    : matching.Length == 0 && snapshot.Request.Target.ProcessId.HasValue ? "target-facts-unavailable" : null);
        }
    }

    private static IReadOnlyList<string>? ObserveCpuLeaseCoreIds(CpuTopologySnapshot topology, ProcessPlacementRecoverySnapshot binding)
    {
        var logical = topology.LogicalProcessors.AsEnumerable();
        if (binding.DefaultCpuSetIds is { Count: > 0 } ids)
        {
            if (ids.Any(id => !topology.LogicalProcessors.Any(processor => processor.CpuSetId == id))) return null;
            logical = logical.Where(processor => processor.CpuSetId is { } id && ids.Contains(id));
        }
        else if (binding.ProcessorAffinityMask is null) return null;
        if (binding.ProcessorAffinityMask is { } mask && topology.LogicalProcessors.All(processor => processor.ProcessorGroup == 0))
        {
            logical = logical.Where(processor => processor.GroupRelativeIndex is >= 0 and < 64
                && (unchecked((ulong)mask) & (1UL << processor.GroupRelativeIndex)) != 0);
        }
        else if (binding.DefaultCpuSetIds is not { Count: > 0 }) return null;
        return logical.Select(processor => processor.PhysicalCoreId).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static bool PreviousCpuLeaseRangeIntersected(CpuTopologySnapshot topology, HostManagerAppliedRecord record, IReadOnlyList<string> cores)
    {
        if (IsCpuSetsRecord(record) && TryParseCpuSetIds(ReadMetadata(record, "previousCpuSetIds"), out var ids))
            return ids.Count == 0 || topology.LogicalProcessors.Any(processor => cores.Contains(processor.PhysicalCoreId)
                && processor.CpuSetId.HasValue && ids.Contains(processor.CpuSetId.Value));
        return TryParseAffinityMask(ReadMetadata(record, "previousAffinityMask"), out var mask)
            && topology.LogicalProcessors.Any(processor => cores.Contains(processor.PhysicalCoreId)
                && processor.GroupRelativeIndex is >= 0 and < 64 && (unchecked((ulong)mask) & (1UL << processor.GroupRelativeIndex)) != 0);
    }

    private static bool CpuLeaseReceiptMatchesBinding(HostManagerAppliedRecord record, ProcessPlacementRecoverySnapshot binding)
        => IsCpuSetsRecord(record)
            ? binding.DefaultCpuSetIds is not null && TryParseCpuSetIds(ReadMetadata(record, "appliedCpuSetIds"), out var ids)
                && CpuSetIdsEqual(ids, binding.DefaultCpuSetIds)
            : TryParseAffinityMask(ReadMetadata(record, "appliedAffinityMask"), out var mask) && mask == binding.ProcessorAffinityMask;
}
