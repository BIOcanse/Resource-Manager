using System.Globalization;
using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Domain.CpuTopology;
using ResourceManager.App.Domain.GpuPlacement;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.NativeCore;

namespace ResourceManager.App.Infrastructure.Optimization;

public sealed partial class HostManagerSmartCoordinator
{
    private IReadOnlyList<HostManagerPlacementDesired> CreateIdealProcessorDesired(
        CpuTopologySnapshot topology, CompiledHardwareScorePlan hardwareScores,
        IReadOnlyList<HostManagerAutomaticPlacementProcess> processes,
        IReadOnlyList<HostManagerAutomaticCpuPlacement> hardPlacements,
        IReadOnlyList<HostManagerAppliedPlacementReceipt> existingPlacements,
        HostManagerCpuReservations? reservations, int maximumThreadCount)
    {
        var inputs = new List<HostManagerIdealProcessorInput>();
        foreach (var process in processes.Where(static p => p.CanApplyPhysicalPlacement
            && p.CanonicalCpuScore is >= 0 && p.Policy.CpuManualLockedPositionIds.Count == 0
            && p.Policy.CpuMaximumOccupancyMode.Equals(CpuMaximumOccupancyModes.AllCores, StringComparison.OrdinalIgnoreCase)))
        {
            var threads = processPolicyWriter.ReadProcessThreadIdealProcessors(process.ProcessId, process.ProcessStartKey, maximumThreadCount);
            if (threads.Status != RecoveryReadStatus.Found || threads.Value is null || threads.Value.Count == 0) continue;
            var baseline = processPolicyWriter.ReadPlacementStateForRecovery(process.ProcessId,
                OperatingSystem.IsWindowsVersionAtLeast(10) ? ProcessPlacementReadFields.DefaultCpuSets : ProcessPlacementReadFields.Affinity);
            if (baseline.Status != RecoveryReadStatus.Found || baseline.Value is null
                || !MatchesAutomaticProcessIdentity(process, baseline.Value)) continue;
            inputs.Add(new(process, threads.Value, baseline.Value.DefaultCpuSetIds ?? []));
        }
        var plans = HostManagerAutomaticPlacementPlanner.PlanIdealProcessors(topology, hardwareScores, inputs, hardPlacements, reservations);
        var existing = existingPlacements.SelectMany(p => p.Records.Select(r => (Placement: p, Record: r)))
            .Where(static p => p.Record.Kind == HostManagerAppliedRecordKinds.CpuThreadIdealProcessor
                && IsAutomaticPlacementRecord(p.Record))
            .ToDictionary(static p => p.Record.RecordId, StringComparer.Ordinal);
        var results = new List<HostManagerPlacementDesired>();
        foreach (var plan in plans)
        {
            var thread = plan.Thread;
            var recordId = CreateStableId($"automatic-placement|ideal|{plan.Process.TargetId}|{thread.ProcessStartKey}|{thread.ThreadId}|{thread.ThreadStartKey}");
            var next = new ThreadIdealProcessor(checked((ushort)plan.Processor.ProcessorGroup), checked((byte)plan.Processor.GroupRelativeIndex));
            existing.TryGetValue(recordId, out var old);
            ThreadIdealProcessor previous;
            if (old.Record is not null)
            {
                if (!ThreadIdealProcessorPlacementRecord.TryRead(old.Record, out var prior)) continue;
                previous = prior.Previous;
            }
            else previous = thread.IdealProcessor;
            // Returning to the baseline is a restore, not a new receipt with identical payloads.
            if (previous == next) continue;
            var metadata = CreateProcessPlacementMetadata(plan.Process,
                DateTimeOffset.FromFileTime(checked((long)thread.ProcessStartKey)), plan.Process.ProcessName, plan.Process.ExecutablePath ?? string.Empty);
            metadata["threadId"] = thread.ThreadId.ToString(CultureInfo.InvariantCulture);
            metadata["threadStartKey"] = thread.ThreadStartKey.ToString(CultureInfo.InvariantCulture);
            metadata["previousProcessorGroup"] = previous.Group.ToString(CultureInfo.InvariantCulture);
            metadata["previousProcessorNumber"] = previous.Number.ToString(CultureInfo.InvariantCulture);
            metadata["appliedProcessorGroup"] = next.Group.ToString(CultureInfo.InvariantCulture);
            metadata["appliedProcessorNumber"] = next.Number.ToString(CultureInfo.InvariantCulture);
            metadata["source"] = "automatic-score-balanced-ideal-processor";
            var record = new HostManagerAppliedRecord(HostManagerAppliedRecordKinds.CpuThreadIdealProcessor, recordId, metadata);
            var now = durableTimeSource.NextUtc();
            var placement = new HostManagerAppliedPlacementReceipt(plan.Process.TargetId, plan.Process.DisplayName,
                plan.Process.SoftwareId, OptimizationResourceKinds.Cpu, [record], old.Placement?.AppliedAt ?? now, now);
            results.Add(new(placement, record, ToPlacementPriority(plan.Process.CanonicalCpuScore!.Value)));
        }
        return results;
    }

    private HostManagerPlacementObservation ObserveIdealProcessor(
        HostManagerAppliedPlacementReceipt placement, HostManagerAppliedRecord record, ulong receiptDigest, ulong previousDigest)
    {
        if (!ThreadIdealProcessorPlacementRecord.TryRead(record, out var value)) return HostManagerPlacementObservation.Unchecked;
        var read = processPolicyWriter.ReadThreadIdealProcessor(value.ProcessId, value.ProcessStartKey, value.ThreadId);
        if (read.Status == RecoveryReadStatus.Unavailable) return HostManagerPlacementObservation.Unavailable(read.NativeErrorCode);
        if (read.Status != RecoveryReadStatus.Found || read.Value is not { } current || !value.Matches(current))
            return HostManagerPlacementObservation.NotFound(read.NativeErrorCode);
        if (current.IdealProcessor == value.Applied) return HostManagerPlacementObservation.FoundReceipt(receiptDigest);
        if (current.IdealProcessor == value.Previous) return HostManagerPlacementObservation.FoundPrevious(previousDigest);
        return HostManagerPlacementObservation.FoundForeign(CreateForeignPlacementDigest(placement, record,
            $"{current.IdealProcessor.Group}:{current.IdealProcessor.Number}", receiptDigest, previousDigest));
    }

    private int WriteIdealProcessor(HostManagerAppliedRecord record)
    {
        if (!ThreadIdealProcessorPlacementRecord.TryRead(record, out var value)) return 87;
        var read = processPolicyWriter.ReadThreadIdealProcessor(value.ProcessId, value.ProcessStartKey, value.ThreadId);
        if (read.Status != RecoveryReadStatus.Found || read.Value is not { } current
            || !value.Matches(current) || current.IdealProcessor != value.Previous) return 1306;
        return processPolicyWriter.TrySetThreadIdealProcessor(current, value.Applied).Succeeded ? 0 : 1306;
    }
}
