using ResourceManager.Adapter.SharedMemory;
using ResourceManager.App.Application.Optimization.SmartControl;
using ResourceManager.App.Application.PublicResources;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Infrastructure.PublicResources;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;

namespace ResourceManager.App.Infrastructure.Optimization;

public sealed partial class HostManagerSmartCoordinator
{
    private bool HasAvailablePublicResourceLifecycle()
        => hostPublicResourceSelfManagerOwner is not IHostPublicResourceCapability capability
            || capability.GetCapability().Available;

    private void RunNormalModePublicResourceLifecycle(
        HostManagerCycleEffectAdmission effectAdmission,
        HostManagerSmartCoordinatorRuntimePlan desired)
    {
        if (!HasAvailablePublicResourceLifecycle()
            || !effectAdmission.TryAcquire(
                HostManagerCycleEffectKind.PublicResourceLifecycle,
                out var permit))
        {
            return;
        }

        var memory = HostPublicResourceCapacityObservation.UnknownOrStale;
        var videoMemory = HostPublicResourceCapacityObservation.UnknownOrStale;
        IReadOnlyList<HostPublicResourceAdapterCapacityObservation> adapters = [];
        var hardware = metricSnapshotSource.ReadLatest(CreatePublicResourceMetricRequest());
        if (hardware is not null)
        {
            var policyExecutionEnabled = hostManagerSmartControlZones.CanRun(
                HostManagerSmartControlZoneIds.PolicyExecution);
            var freshness = hostPublicResourceCapacitySampleFreshness.Observe(hardware);
            var idle = CreateIdleCapacity(hardware);
            var observedMemory = HostPublicResourceCapacityObserver.ObserveMemory(
                policyExecutionEnabled,
                desired.HostPlan.HotPublish.MemoryCleanup,
                idle.MemoryFreeRatio,
                idle.MemoryFreeRatioCurrent);
            var observedVideoMemory = HostPublicResourceCapacityObserver.ObserveVideoMemory(
                policyExecutionEnabled,
                desired.HostPlan.HotPublish.ResourceScheduler.Configuration,
                hardware.GpuInventory);
            if (freshness.Memory)
            {
                memory = observedMemory;
            }
            if (freshness.VideoMemory)
            {
                videoMemory = observedVideoMemory.Summary;
                adapters = observedVideoMemory.Adapters;
            }
        }

        RunHostPublicResourceSelfManager(
            permit,
            new HostPublicResourceCapacityShortage(memory, videoMemory, adapters));
    }

    private void RunHostPublicResourceSelfManager(
        HostManagerCycleEffectPermit permit,
        HostPublicResourceCapacityShortage shortage)
    {
        permit.Require(HostManagerCycleEffectKind.PublicResourceLifecycle);
        if (!HasAvailablePublicResourceLifecycle())
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
