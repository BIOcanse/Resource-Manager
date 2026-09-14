using ResourceManager.App.Infrastructure.NativeCore;

namespace ResourceManager.App.Infrastructure.Optimization;

internal static class HostManagerMemoryCleanupScoreIndex
{
    internal static bool TryCreate(
        HostManagerComputeScoringCycleResult? compute,
        ulong processInventoryGeneration,
        IReadOnlyCollection<HostManagerMemoryCleanupTargetFact> expectedTargets,
        out IReadOnlyDictionary<HostManagerComputeProcessIdentity, HostManagerComputeScore> scores)
    {
        scores = new Dictionary<HostManagerComputeProcessIdentity, HostManagerComputeScore>();
        var cpu = compute?.Cpu;
        if (compute is null
            || cpu is null
            || expectedTargets is null
            || compute.SchedulingGeneration == 0
            || cpu.SchedulingGeneration != compute.SchedulingGeneration
            || cpu.SourceIdentity.InventoryGeneration == 0
            || cpu.SourceIdentity.InventoryGeneration != processInventoryGeneration)
        {
            return false;
        }

        var expected = new HashSet<HostManagerComputeProcessIdentity>();
        foreach (var target in expectedTargets)
        {
            if (target.ProcessId <= 0
                || target.ProcessStartKey == 0
                || !expected.Add(new HostManagerComputeProcessIdentity(
                    target.ProcessId,
                    target.ProcessStartKey)))
            {
                return false;
            }
        }

        var index = new Dictionary<HostManagerComputeProcessIdentity, HostManagerComputeScore>(
            expected.Count);
        foreach (var score in cpu.Scores)
        {
            if (score.Kind != NativeComputeScoringOutputKind.ProcessCpu)
            {
                continue;
            }

            var identity = new HostManagerComputeProcessIdentity(
                score.ProcessId,
                score.ProcessStartKey);
            if (!expected.Contains(identity))
            {
                continue;
            }
            if (score.SchedulingGeneration != compute.SchedulingGeneration
                || score.TargetKey == 0
                || score.SoftwareKey == 0
                || score.ProcessId <= 0
                || score.ProcessStartKey == 0
                || score.AdapterKey != 0
                || score.MemberCount != 1
                || !double.IsFinite(score.Score)
                || score.Score < 0
                || !index.TryAdd(identity, score))
            {
                return false;
            }
        }

        if (index.Count != expected.Count)
        {
            return false;
        }

        scores = index;
        return true;
    }
}
