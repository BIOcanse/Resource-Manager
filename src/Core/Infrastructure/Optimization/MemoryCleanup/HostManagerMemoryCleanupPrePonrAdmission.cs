using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Domain.Optimization.MemoryCleanup;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Infrastructure.NativeCore;

namespace ResourceManager.App.Infrastructure.Optimization;

internal enum HostManagerMemoryCleanupCandidateAdmissionStatus : byte
{
    Eligible = 1,
    KnownIneligible = 2,
    Unknown = 3
}

internal static class HostManagerMemoryCleanupPrePonrAdmission
{
    internal static HostManagerMemoryCleanupCandidateAdmissionStatus EvaluateCurrentFact(
        AutomaticMemoryCleanupCandidate baseline,
        HostManagerComputeScore baselineScore,
        SchedulingProcessFact current,
        double currentMaximumBaseScore,
        string currentRuntimeState,
        int protectionLevel)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(baselineScore);
        ArgumentNullException.ThrowIfNull(current);
        var currentTargetKey = NativeStableIdentity.CreateCaseInsensitiveKey(
            HostManagerTargetIdentity.CreateProcessTargetId(
                current.ProcessId,
                current.ProcessStartKey));
        if (baseline.ProcessId <= 0
            || current.ProcessId != baseline.ProcessId
            || current.ProcessStartKey == 0
            || CreateStartKey(baseline.ProcessStartedAt) != current.ProcessStartKey
            || currentTargetKey == 0
            || NativeStableIdentity.CreateCaseInsensitiveKey(baseline.TargetId) != currentTargetKey
            || baselineScore.TargetKey != currentTargetKey
            || baselineScore.SoftwareKey != NativeStableIdentity.CreateCaseInsensitiveKey(
                current.SoftwareId)
            || !double.IsFinite(currentMaximumBaseScore)
            || currentMaximumBaseScore < 0
            || currentMaximumBaseScore != baseline.BaseScore
            || !HostManagerTargetCapabilityClassifier.CanWriteProcessPolicy(
                current.SoftwareId,
                current.SoftwareKind)
            || baseline.RuntimeState is HostManagerRuntimeStates.ForegroundFocused
                or HostManagerRuntimeStates.ForegroundUnfocused
            || currentRuntimeState is HostManagerRuntimeStates.ForegroundFocused
                or HostManagerRuntimeStates.ForegroundUnfocused
            || protectionLevel >= OptimizationProtectionLevels.Level2NoOptimization)
        {
            return HostManagerMemoryCleanupCandidateAdmissionStatus.KnownIneligible;
        }

        return HostManagerMemoryCleanupCandidateAdmissionStatus.Eligible;
    }

    internal static HostManagerMemoryCleanupCandidateAdmissionStatus EvaluateLiveIdentity(
        AutomaticMemoryCleanupCandidate baseline,
        RecoveryReadResult<ProcessInstanceRecoverySnapshot> read)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        switch (read.Status)
        {
            case RecoveryReadStatus.Found:
                if (read.Value is null)
                {
                    return HostManagerMemoryCleanupCandidateAdmissionStatus.Unknown;
                }
                return read.Value.ProcessId == baseline.ProcessId
                    && read.Value.StartedAt == baseline.ProcessStartedAt
                    ? HostManagerMemoryCleanupCandidateAdmissionStatus.Eligible
                    : HostManagerMemoryCleanupCandidateAdmissionStatus.KnownIneligible;
            case RecoveryReadStatus.NotFoundOrExited:
                return read.Value is null
                    ? HostManagerMemoryCleanupCandidateAdmissionStatus.KnownIneligible
                    : HostManagerMemoryCleanupCandidateAdmissionStatus.Unknown;
            case RecoveryReadStatus.Unavailable:
                return HostManagerMemoryCleanupCandidateAdmissionStatus.Unknown;
            default:
                return HostManagerMemoryCleanupCandidateAdmissionStatus.Unknown;
        }
    }

    private static ulong CreateStartKey(DateTimeOffset startedAt)
    {
        try
        {
            return checked((ulong)startedAt.ToFileTime());
        }
        catch (Exception exception) when (
            exception is ArgumentOutOfRangeException or OverflowException)
        {
            return 0;
        }
    }
}
