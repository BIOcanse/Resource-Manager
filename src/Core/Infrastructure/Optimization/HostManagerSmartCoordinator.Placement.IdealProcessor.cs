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
    internal const string AutomaticIdealProcessorSource = "automatic-score-balanced-ideal-processor";

    private readonly HostManagerIdealProcessorWorkspace idealProcessorWorkspace = new();

    internal static HostManagerAppliedRecord CreateIdealProcessorAppliedRecord(
        string recordId,
        HostManagerIdealProcessorDesiredMetadata metadata)
    {
        var recordMetadata = CreateProcessPlacementMetadata(
            metadata.ProcessId,
            metadata.ProcessStartKey,
            metadata.ProcessStartedAtUnixMilliseconds,
            metadata.ProcessName,
            metadata.ExecutablePath);
        recordMetadata["threadId"] = metadata.ThreadId.ToString(CultureInfo.InvariantCulture);
        recordMetadata["threadStartKey"] = metadata.ThreadStartKey.ToString(CultureInfo.InvariantCulture);
        recordMetadata["previousProcessorGroup"] = metadata.Previous.Group.ToString(CultureInfo.InvariantCulture);
        recordMetadata["previousProcessorNumber"] = metadata.Previous.Number.ToString(CultureInfo.InvariantCulture);
        recordMetadata["appliedProcessorGroup"] = metadata.Applied.Group.ToString(CultureInfo.InvariantCulture);
        recordMetadata["appliedProcessorNumber"] = metadata.Applied.Number.ToString(CultureInfo.InvariantCulture);
        recordMetadata["source"] = AutomaticIdealProcessorSource;
        return new HostManagerAppliedRecord(
            HostManagerAppliedRecordKinds.CpuThreadIdealProcessor,
            recordId,
            recordMetadata);
    }

    private IReadOnlyList<HostManagerPlacementDesired> CreateIdealProcessorDesired(
        IThreadIdealProcessorReadCycle idealReads,
        CpuTopologySnapshot topology, CompiledHardwareScorePlan hardwareScores,
        IReadOnlyList<HostManagerAutomaticPlacementProcess> processes,
        IReadOnlyList<HostManagerAutomaticCpuPlacement> hardPlacements,
        IReadOnlyList<HostManagerAppliedPlacementReceipt> existingPlacements,
        HostManagerCpuReservations? reservations, int maximumThreadCount)
    {
        var workspace = idealProcessorWorkspace;
        try
        {
            workspace.ResetCall();
            var fields = OperatingSystem.IsWindowsVersionAtLeast(10)
                ? ProcessPlacementReadFields.DefaultCpuSets
                : ProcessPlacementReadFields.Affinity;
            for (var processIndex = 0; processIndex < processes.Count; processIndex++)
            {
                var process = processes[processIndex];
                if (!process.CanApplyPhysicalPlacement
                    || process.Policy.CpuSchedulingExcluded
                    || process.CanonicalCpuScore is not >= 0
                    || process.Policy.CpuManualLockedPositionIds.Count != 0
                    || !process.Policy.CpuMaximumOccupancyMode.Equals(
                        CpuMaximumOccupancyModes.AllCores,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var threads = idealReads.ReadProcessThreads(process.ProcessId, process.ProcessStartKey, maximumThreadCount);
                if (threads.Status != RecoveryReadStatus.Found || threads.Value is null || threads.Value.Count == 0) continue;
                var baseline = processPolicyWriter.ReadPlacementStateForRecovery(process.ProcessId, fields);
                if (baseline.Status != RecoveryReadStatus.Found || baseline.Value is null
                    || !MatchesAutomaticProcessIdentity(process, baseline.Value)) continue;
                workspace.Inputs.Add(new(process, threads.Value, baseline.Value.DefaultCpuSetIds ?? []));
            }

            workspace.ReconcileStableRecordIds();
            HostManagerAutomaticPlacementPlanner.PlanIdealProcessorsIntoWorkspace(
                topology, hardwareScores, hardPlacements, reservations, workspace);
            for (var placementIndex = 0; placementIndex < existingPlacements.Count; placementIndex++)
            {
                var placement = existingPlacements[placementIndex];
                for (var recordIndex = 0; recordIndex < placement.Records.Count; recordIndex++)
                {
                    var record = placement.Records[recordIndex];
                    if (record.Kind == HostManagerAppliedRecordKinds.CpuThreadIdealProcessor
                        && IsAutomaticPlacementRecord(record))
                    {
                        workspace.ExistingRecords.Add(record.RecordId,
                            new HostManagerIdealProcessorWorkspace.ExistingIdealRecord(placement, record));
                    }
                }
            }

            foreach (var plan in workspace.Plans)
            {
                var thread = plan.Thread;
                if (!workspace.TryGetStableRecordId(plan.Process.TargetId, thread, out var recordId))
                {
                    throw new InvalidDataException("An ideal-processor plan has no current thread identity.");
                }
                var next = new ThreadIdealProcessor(checked((ushort)plan.Processor.ProcessorGroup), checked((byte)plan.Processor.GroupRelativeIndex));
                workspace.ExistingRecords.TryGetValue(recordId, out var old);
                ThreadIdealProcessor previous;
                if (old.Record is not null)
                {
                    if (!ThreadIdealProcessorPlacementRecord.TryRead(old.Record, out var prior)) continue;
                    previous = prior.Previous;
                }
                else previous = thread.IdealProcessor;
                // Returning to the baseline is a restore, not a new receipt with identical payloads.
                if (previous == next) continue;
                var idealProcessorMetadata = new HostManagerIdealProcessorDesiredMetadata(
                    plan.Process.ProcessId,
                    plan.Process.ProcessStartKey,
                    DateTimeOffset.FromFileTime(checked((long)thread.ProcessStartKey)).ToUnixTimeMilliseconds(),
                    plan.Process.ProcessName,
                    plan.Process.ExecutablePath ?? string.Empty,
                    thread.ThreadId,
                    thread.ThreadStartKey,
                    previous,
                    next);
                var record = new HostManagerAppliedRecord(
                    HostManagerAppliedRecordKinds.CpuThreadIdealProcessor,
                    recordId);
                var now = durableTimeSource.NextUtc();
                var placement = new HostManagerAppliedPlacementReceipt(plan.Process.TargetId, plan.Process.DisplayName,
                    plan.Process.SoftwareId, OptimizationResourceKinds.Cpu, [record], old.Placement?.AppliedAt ?? now, now);
                workspace.Desired.Add(new(placement, record, ToPlacementPriority(plan.Process.CanonicalCpuScore!.Value))
                {
                    IdealProcessorMetadata = idealProcessorMetadata
                });
            }
            return workspace.Desired.Count == 0
                ? Array.Empty<HostManagerPlacementDesired>()
                : workspace.Desired.ToArray();
        }
        finally
        {
            workspace.ClearEphemeralReferences();
        }
    }

    private HostManagerPlacementObservation ObserveIdealProcessor(
        HostManagerAppliedPlacementReceipt placement, HostManagerAppliedRecord record, ulong receiptDigest, ulong previousDigest,
        IThreadIdealProcessorReadCycle? idealReads = null)
    {
        if (!ThreadIdealProcessorPlacementRecord.TryRead(record, out var value)) return HostManagerPlacementObservation.Unchecked;
        // Write/restore verification calls this without the planning context and must read afresh.
        var read = idealReads is null
            ? processPolicyWriter.ReadThreadIdealProcessor(value.ProcessId, value.ProcessStartKey, value.ThreadId)
            : idealReads.ReadThread(value.ProcessId, value.ProcessStartKey, value.ThreadId);
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
