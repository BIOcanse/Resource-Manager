using ResourceManager.App.Domain.Optimization.MemoryCleanup;
using ResourceManager.App.Infrastructure.NativeCore;

namespace ResourceManager.App.Infrastructure.Optimization;

internal sealed record HostManagerMemoryCleanupTargetFact(
    string TargetId,
    string SoftwareId,
    string DisplayName,
    int ProcessId,
    ulong ProcessStartKey,
    DateTimeOffset ProcessStartedAt,
    string RuntimeState,
    double BaseScore,
    bool CanApply);

internal static class HostManagerMemoryCleanupCandidateProjection
{
    internal static IReadOnlyList<AutomaticMemoryCleanupCandidate> Create(
        IReadOnlyList<HostManagerMemoryCleanupTargetFact> targets,
        IReadOnlyDictionary<HostManagerComputeProcessIdentity, HostManagerComputeScore> scores)
    {
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(scores);
        if (targets.Count != scores.Count)
        {
            return [];
        }

        var maximumBaseScoreBySoftware = new Dictionary<ulong, double>();
        var identities = new HashSet<HostManagerComputeProcessIdentity>();
        for (var index = 0; index < targets.Count; index++)
        {
            var target = targets[index];
            var identity = new HostManagerComputeProcessIdentity(
                target.ProcessId,
                target.ProcessStartKey);
            var softwareKey = NativeStableIdentity.CreateCaseInsensitiveKey(target.SoftwareId);
            if (string.IsNullOrWhiteSpace(target.TargetId)
                || string.IsNullOrWhiteSpace(target.DisplayName)
                || target.ProcessId <= 0
                || target.ProcessStartKey == 0
                || target.ProcessStartedAt == default
                || string.IsNullOrWhiteSpace(target.RuntimeState)
                || softwareKey == 0
                || !double.IsFinite(target.BaseScore)
                || target.BaseScore < 0
                || !identities.Add(identity))
            {
                return [];
            }

            ulong actualStartKey;
            try
            {
                actualStartKey = checked((ulong)target.ProcessStartedAt.ToFileTime());
            }
            catch (ArgumentOutOfRangeException)
            {
                return [];
            }
            if (actualStartKey != target.ProcessStartKey
                || !scores.TryGetValue(identity, out var score)
                || score.TargetKey != NativeStableIdentity.CreateCaseInsensitiveKey(target.TargetId)
                || score.SoftwareKey != softwareKey)
            {
                return [];
            }

            maximumBaseScoreBySoftware[softwareKey] = maximumBaseScoreBySoftware.TryGetValue(
                softwareKey,
                out var currentMaximum)
                ? Math.Max(currentMaximum, target.BaseScore)
                : target.BaseScore;
        }

        var candidates = new AutomaticMemoryCleanupCandidate[targets.Count];
        for (var index = 0; index < targets.Count; index++)
        {
            var target = targets[index];
            var identity = new HostManagerComputeProcessIdentity(
                target.ProcessId,
                target.ProcessStartKey);
            var score = scores[identity];
            candidates[index] = new(
                target.TargetId,
                target.DisplayName,
                target.ProcessId,
                target.ProcessStartedAt,
                target.RuntimeState,
                maximumBaseScoreBySoftware[score.SoftwareKey],
                score.Score,
                MemoryUsedPercent: 0,
                target.CanApply);
        }
        return candidates;
    }
}
