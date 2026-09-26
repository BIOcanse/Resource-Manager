using System.Globalization;
using ResourceManager.App.Domain.Optimization;

namespace ResourceManager.App.Infrastructure.GpuPlacement;

internal static class GpuActionFacts
{
    internal static IReadOnlyDictionary<(int ProcessId, ulong ProcessStartKey), ulong> CompletedAttemptTimes(
        IReadOnlyList<HostManagerAppliedPlacementReceipt> placements)
    {
        var times = new Dictionary<(int, ulong), ulong>();
        foreach (var placement in placements)
        foreach (var record in placement.Records)
        {
            if (!GpuRemoteCallRecord.TryRead(record, out var fact) || fact.BlocksProcess
                || (fact.Settlement ?? fact.Result) is not { Completed: true, ThreadCreationFileTimeUtc: { } time }) continue;
            var identity = (fact.Request.Process.ProcessId, fact.Request.Process.ProcessStartKey);
            times[identity] = Math.Max(times.GetValueOrDefault(identity), time);
        }
        return times;
    }

    internal static bool IsActionFact(HostManagerAppliedRecord record)
        => GpuWindowActionRecord.IsActionFact(record) || GpuRemoteCallRecord.IsActionFact(record);

    internal static bool BlocksProcess(IReadOnlyList<HostManagerAppliedPlacementReceipt> placements,
        string targetId, int processId, ulong creationFileTimeUtc)
        => GpuWindowActionRecord.BlocksProcess(placements, targetId, processId, creationFileTimeUtc)
            || RemoteCallBlocksProcess(placements, targetId, processId, creationFileTimeUtc);

    internal static bool RemoteCallBlocksProcess(IReadOnlyList<HostManagerAppliedPlacementReceipt> placements,
        string targetId, int processId, ulong creationFileTimeUtc)
    {
        foreach (var placement in placements)
        foreach (var record in placement.Records)
        {
            if (!GpuRemoteCallRecord.IsActionFact(record)) continue;
            if (!GpuRemoteCallRecord.TryRead(record, out var fact))
            {
                if (placement.TargetId.Equals(targetId, StringComparison.OrdinalIgnoreCase)) return true;
                continue;
            }
            if (fact.BlocksProcess && fact.Request.Process.ProcessId == processId
                && fact.Request.Process.ProcessStartKey == creationFileTimeUtc) return true;
        }
        return false;
    }

    internal static bool BlocksEffect(IReadOnlyList<HostManagerAppliedPlacementReceipt> placements,
        HostManagerAppliedPlacementReceipt owner, HostManagerAppliedRecord record)
    {
        if (!owner.ResourceKind.Equals(OptimizationResourceKinds.Gpu, StringComparison.OrdinalIgnoreCase)
            || IsActionFact(record)) return false;
        int.TryParse(record.Metadata?.GetValueOrDefault("processId"), NumberStyles.None, CultureInfo.InvariantCulture, out var pid);
        ulong.TryParse(record.Metadata?.GetValueOrDefault("processStartKey"), NumberStyles.None, CultureInfo.InvariantCulture, out var birth);
        return RemoteCallBlocksProcess(placements, owner.TargetId, pid, birth);
    }

    internal static bool HasPlacementEffects(IReadOnlyList<HostManagerAppliedPlacementReceipt> placements)
        => placements.Any(static placement => placement.Records.Any(static record => !IsActionFact(record)));

    internal static bool HasUnsettledActions(IReadOnlyList<HostManagerAppliedPlacementReceipt> placements)
        => placements.Any(static placement => placement.Records.Any(static record =>
            GpuWindowActionRecord.IsActionFact(record)
                ? !GpuWindowActionRecord.TryRead(record, out var window) || window.BlocksAutomaticAction
                : GpuRemoteCallRecord.IsActionFact(record) && (!GpuRemoteCallRecord.TryRead(record, out var call) || call.BlocksProcess)));

    internal static IReadOnlyList<HostManagerAppliedPlacementReceipt> PlacementEffects(
        IReadOnlyList<HostManagerAppliedPlacementReceipt> placements)
        => placements.Select(static placement => placement with
        {
            Records = placement.Records.Where(static record => !IsActionFact(record)).ToArray()
        }).Where(static placement => placement.Records.Count != 0).ToArray();

    internal static IReadOnlyList<HostManagerAppliedPlacementReceipt> AvailablePlacementEffects(
        IReadOnlyList<HostManagerAppliedPlacementReceipt> placements)
        => placements.Select(placement => placement with
        {
            Records = placement.Records.Where(record => !IsActionFact(record) && !BlocksEffect(placements, placement, record)).ToArray()
        }).Where(static placement => placement.Records.Count != 0).ToArray();
}
