using ResourceManager.Adapter.SharedMemory;
using ResourceManager.App.Application.PublicResources;

namespace ResourceManager.App.Infrastructure.Optimization;

public sealed partial class HostManagerSmartCoordinator
{
    private void RunHostPublicResourceSelfManager(
        HostManagerCycleEffectPermit permit,
        HostPublicResourceCapacityShortage shortage)
    {
        permit.Require(HostManagerCycleEffectKind.PublicResourceLifecycle);
        if (hostPublicResourceSelfManagerOwner is IHostPublicResourceCapability capability
            && !capability.GetCapability().Available)
        {
            return;
        }
        try
        {
            HostManagerCycleEffectReservation? newEffectReservation = null;
            HostManagerCycleEffectReservation? recoveryReservation = null;
            var maximumNewEffectAttempts = permit.NewPointOfNoReturnRemaining;
            var maximumRecoveryAttempts = permit.RecoveryRemaining;
            if (maximumNewEffectAttempts != 0)
            {
                _ = permit.TryReserveNewPointOfNoReturn(
                    maximumNewEffectAttempts,
                    out newEffectReservation);
            }
            if (maximumRecoveryAttempts != 0)
            {
                _ = permit.TryReserveRecovery(
                    maximumRecoveryAttempts,
                    out recoveryReservation);
            }
            var result = hostPublicResourceSelfManagerOwner.Tick(
                new HostPublicResourceSelfManagerTickRequest(
                    shortage,
                    newEffectReservation?.Count ?? 0,
                    recoveryReservation?.Count ?? 0));
            if (result.NewEffectAttemptCount > (newEffectReservation?.Count ?? 0)
                || result.RecoveryAttemptCount > (recoveryReservation?.Count ?? 0))
            {
                throw new InvalidDataException(
                    "Host public-resource self-management exceeded its admitted cycle effect budget.");
            }
            newEffectReservation?.ReleaseUnusedBeforePointOfNoReturn(
                result.NewEffectAttemptCount);
            recoveryReservation?.ReleaseUnusedBeforePointOfNoReturn(
                result.RecoveryAttemptCount);
            if (result.UnloadedCount != 0
                || result.RecalledCount != 0
                || result.RejectedCount != 0)
            {
                logger.LogInformation(
                    "Host public-resource self-management planned {PlannedCount} actions, unloaded {UnloadedCount}, recalled {RecalledCount}, rejected {RejectedCount}, released {ReleasedBytes} bytes, and restored {RestoredBytes} bytes at sample {SampleGeneration}.",
                    result.PlannedCount,
                    result.UnloadedCount,
                    result.RecalledCount,
                    result.RejectedCount,
                    result.ReleasedBytes,
                    result.RestoredBytes,
                    result.SampleGeneration);
            }
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Host public-resource self-management failed closed for this cycle.");
        }
    }

}
