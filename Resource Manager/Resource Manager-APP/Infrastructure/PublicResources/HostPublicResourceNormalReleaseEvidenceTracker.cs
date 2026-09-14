using ResourceManager.Adapter.SharedMemory;
using ResourceManager.App.Infrastructure.Optimization;

namespace ResourceManager.App.Infrastructure.PublicResources;

internal readonly record struct HostPublicResourceNormalReleaseCycle(
    long RuntimePlanVersion,
    ulong HostPublicationSequence,
    ulong NativeHostSessionIncarnation,
    ulong HostPlanEpoch,
    string HostPlanSha256,
    ulong MemoryCleanupConfigurationGeneration,
    ulong SmartConfigurationGeneration,
    string SmartConfigurationSha256,
    bool NormalMemoryReleaseEnabled,
    ulong HardwareWorkspaceIdentity,
    ulong HardwareConfigurationGeneration,
    ulong HardwareCatalogGeneration,
    ulong HardwareCommittedGeneration,
    long HardwareCapturedAtUtcTicks)
{
    internal bool IsValid => RuntimePlanVersion > 0
        && HostPublicationSequence > 0
        && NativeHostSessionIncarnation > 0
        && HostPlanEpoch > 0
        && IsSha256(HostPlanSha256)
        && MemoryCleanupConfigurationGeneration > 0
        && SmartConfigurationGeneration > 0
        && IsSha256(SmartConfigurationSha256)
        && HardwareWorkspaceIdentity > 0
        && HardwareConfigurationGeneration > 0
        && HardwareCatalogGeneration > 0
        && HardwareCommittedGeneration > 0
        && HardwareCapturedAtUtcTicks > 0;

    private static bool IsSha256(string? value)
        => value is not null
            && value.Length == 64
            && value.All(static character => char.IsAsciiHexDigit(character));
}

internal sealed class HostPublicResourceNormalReleaseEvidenceTracker
{
    internal const int RequiredCompletedRoundCount = 2;

    private ChainIdentity? chainIdentity;
    private PendingRound? pendingRound;
    private HostPublicResourceNormalReleaseCycle currentCycle;
    private int confirmedRoundCount;
    private bool pressureEscalated;
    private bool acceptsCurrentRound;

    internal HostPublicResourceCapacityObservation BeginCycle(
        HostPublicResourceNormalReleaseCycle cycle,
        HostPublicResourceCapacityObservation observation)
    {
        acceptsCurrentRound = false;
        currentCycle = default;
        if (!cycle.IsValid
            || !cycle.NormalMemoryReleaseEnabled
            || observation.State != HostPublicResourceCapacityObservationState.ShortageFresh)
        {
            Reset();
            return observation.State == HostPublicResourceCapacityObservationState.ShortageFresh
                ? HostPublicResourceCapacityObservation.ShortageFresh()
                : observation;
        }

        var nextIdentity = ChainIdentity.From(cycle);
        if (chainIdentity is null || chainIdentity.Value != nextIdentity)
        {
            Reset();
            chainIdentity = nextIdentity;
        }

        if (pendingRound is { } pending)
        {
            if (!pending.IsSameHardwareStream(cycle)
                || cycle.HardwareCommittedGeneration < pending.HardwareCommittedGeneration)
            {
                confirmedRoundCount = 0;
                pressureEscalated = false;
                pendingRound = null;
            }
            else if (cycle.HardwareCommittedGeneration == pending.HardwareCommittedGeneration)
            {
                currentCycle = cycle;
                return HostPublicResourceCapacityObservation.ShortageFresh(
                    pressureEscalated);
            }
            else
            {
                if (cycle.HardwareCapturedAtUtcTicks <= pending.CompletedAtUtcTicks)
                {
                    return HostPublicResourceCapacityObservation.ShortageFresh(
                        pressureEscalated);
                }
                confirmedRoundCount = Math.Min(
                    RequiredCompletedRoundCount,
                    checked(confirmedRoundCount + 1));
                pendingRound = null;
                pressureEscalated = confirmedRoundCount >= RequiredCompletedRoundCount;
            }
        }

        currentCycle = cycle;
        acceptsCurrentRound = !pressureEscalated;
        return HostPublicResourceCapacityObservation.ShortageFresh(
            pressureEscalated);
    }

    internal void CompleteCycle(
        HostPublicResourceNormalReleaseCycle cycle,
        HostManagerMemoryCleanupExecutionResult result)
    {
        if (pressureEscalated)
        {
            acceptsCurrentRound = false;
            return;
        }
        if (!acceptsCurrentRound || currentCycle != cycle)
        {
            return;
        }

        acceptsCurrentRound = false;
        if (!result.CompletedNormalReleaseRound
            || result.CompletedAtUtcTicks <= cycle.HardwareCapturedAtUtcTicks)
        {
            confirmedRoundCount = 0;
            pendingRound = null;
            return;
        }

        pendingRound = PendingRound.From(cycle, result.CompletedAtUtcTicks);
    }

    private void Reset()
    {
        chainIdentity = null;
        pendingRound = null;
        currentCycle = default;
        confirmedRoundCount = 0;
        pressureEscalated = false;
        acceptsCurrentRound = false;
    }

    private readonly record struct ChainIdentity(
        long RuntimePlanVersion,
        ulong HostPublicationSequence,
        ulong NativeHostSessionIncarnation,
        ulong HostPlanEpoch,
        string HostPlanSha256,
        ulong MemoryCleanupConfigurationGeneration,
        ulong SmartConfigurationGeneration,
        string SmartConfigurationSha256,
        ulong HardwareWorkspaceIdentity,
        ulong HardwareConfigurationGeneration,
        ulong HardwareCatalogGeneration)
    {
        internal static ChainIdentity From(HostPublicResourceNormalReleaseCycle cycle)
            => new(
                cycle.RuntimePlanVersion,
                cycle.HostPublicationSequence,
                cycle.NativeHostSessionIncarnation,
                cycle.HostPlanEpoch,
                cycle.HostPlanSha256,
                cycle.MemoryCleanupConfigurationGeneration,
                cycle.SmartConfigurationGeneration,
                cycle.SmartConfigurationSha256,
                cycle.HardwareWorkspaceIdentity,
                cycle.HardwareConfigurationGeneration,
                cycle.HardwareCatalogGeneration);
    }

    private readonly record struct PendingRound(
        ulong HardwareWorkspaceIdentity,
        ulong HardwareConfigurationGeneration,
        ulong HardwareCatalogGeneration,
        ulong HardwareCommittedGeneration,
        long CompletedAtUtcTicks)
    {
        internal static PendingRound From(
            HostPublicResourceNormalReleaseCycle cycle,
            long completedAtUtcTicks)
            => new(
                cycle.HardwareWorkspaceIdentity,
                cycle.HardwareConfigurationGeneration,
                cycle.HardwareCatalogGeneration,
                cycle.HardwareCommittedGeneration,
                completedAtUtcTicks);

        internal bool IsSameHardwareStream(HostPublicResourceNormalReleaseCycle cycle)
            => HardwareWorkspaceIdentity == cycle.HardwareWorkspaceIdentity
                && HardwareConfigurationGeneration == cycle.HardwareConfigurationGeneration
                && HardwareCatalogGeneration == cycle.HardwareCatalogGeneration;
    }
}
