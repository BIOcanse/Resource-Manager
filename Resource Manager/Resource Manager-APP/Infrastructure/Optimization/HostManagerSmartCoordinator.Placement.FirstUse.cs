using ResourceManager.App.Application.GpuPlacement;
using ResourceManager.App.Application.Optimization.SmartControl;
using ResourceManager.App.Domain.GpuPlacement;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Infrastructure.GpuPlacement;

namespace ResourceManager.App.Infrastructure.Optimization;

public sealed partial class HostManagerSmartCoordinator
{
    private sealed record HostManagerNativeCycleSegmentResult(
        TimeSpan Delay, HostManagerCycleEffectAdmission? CompletedAdmission = null);

    private async Task<TimeSpan> RunNativeCycleCoreAsync(
        string trigger, bool realtimeCycle, HostManagerSmartCoordinatorOuterLoopCycleContext? outerLoopContext,
        CancellationToken cancellationToken)
    {
        var firstUse = new List<HostManagerAutomaticGpuPreferencePlacement>();
        var completed = await RunNativeCycleSegmentAsync(trigger, realtimeCycle, outerLoopContext, cancellationToken, firstUse);
        if (completed.CompletedAdmission is not { } admission) return completed.Delay;

        foreach (var planned in firstUse)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!CanContinueFirstUsePlacement(planned, out var current)) continue;
            if (!admission.TryAcquire(HostManagerCycleEffectKind.NativeActionTransaction, out var permit)
                || !permit.TryReserveExactNewPointOfNoReturn(1, out var reservation)
                || reservation is null) break;
            using (reservation)
            {
                var limits = runtimePlanProvider.Current.HostManager.HotPublish.PlacementCoordinator;
                using var work = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                work.CancelAfter(TimeSpan.FromMilliseconds(checked(
                    (long)limits.ApiObservationWindowMilliseconds + limits.ActionTimeoutMilliseconds)));
                using var cleanup = new CancellationTokenSource();
                var cleanupStarted = false;
                RunningGpuPlacementPreparation preparation;
                // The segment has disposed its publication/journal leases and completed its other effects.
                gate.Release();
                try
                {
                    preparation = await runningGpuPlacementActions.PrepareWithFirstApiObservationAsync(
                        CreateAutomaticGpuActionRequest(current),
                        new RunningGpuApiObservationExecution(limits.ApiObservationWindowMilliseconds,
                            async (execution, token) =>
                            {
                                var stopping = execution.Request.Kind == GpuRemoteCallKind.StopApiObservation;
                                if (stopping && !cleanupStarted)
                                {
                                    cleanupStarted = true;
                                    cleanup.CancelAfter(TimeSpan.FromMilliseconds(limits.WindowExecution.CleanupReserveMilliseconds));
                                }
                                try { await gate.WaitAsync(token).ConfigureAwait(false); }
                                catch { execution.Dispose(); throw; }
                                var handedToOwner = false;
                                try
                                {
                                    if (!stopping && !CanContinueFirstUsePlacement(planned, out _))
                                    {
                                        throw new OperationCanceledException("The original GPU placement is no longer permitted.", token);
                                    }
                                    if (!await TrySettleGpuActionCheckpointAsync())
                                    {
                                        throw new IOException("The original GPU checkpoint is not settled.");
                                    }
                                    var state = await LoadRollbackStateAsync(token);
                                    var process = RefreshFirstUseProcess(planned.Process);
                                    var key = HostManagerPlacementReceiptKey.Create(OptimizationResourceKinds.Gpu, process.TargetId);
                                    handedToOwner = true;
                                    var result = await ExecuteOwnedGpuRemoteCallCoreAsync(state, key, execution, process, token,
                                        () =>
                                        {
                                            using var startPublication = runtimePlanProvider.AcquirePublicationLease();
                                            lock (lifecycleSync)
                                            {
                                                if (token.IsCancellationRequested
                                                    || (!stopping && !CanContinueFirstUsePlacement(planned, out _))) return false;
                                                permit.EnterSingleNewAction(reservation);
                                                execution.Start();
                                                return true;
                                            }
                                        });
                                    return result.Result;
                                }
                                catch
                                {
                                    if (!handedToOwner) execution.Dispose();
                                    throw;
                                }
                                finally { gate.Release(); }
                            },
                            (_, window, token) => Task.Delay(window, token), cleanup.Token), work.Token).ConfigureAwait(false);
                }
                finally
                {
                    // The existing public/worker caller still owns the final gate release.
                    await gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                }

                using var publication = runtimePlanProvider.AcquirePublicationLease();
                if (preparation.Plan is not { } action || !CanContinueFirstUsePlacement(planned, out current)) continue;
                if (!await TrySettleGpuActionCheckpointAsync()) return completed.Delay;
                var latest = await LoadRollbackStateAsync(cancellationToken);
                var processNow = current.Process;
                if (GpuActionFacts.BlocksProcess(latest.AppliedPlacements, processNow.TargetId,
                        processNow.ProcessId, processNow.ProcessStartKey)) continue;
                var effects = GpuActionFacts.AvailablePlacementEffects(latest.AppliedPlacements);
                var keyNow = HostManagerPlacementReceiptKey.Create(OptimizationResourceKinds.Gpu, processNow.TargetId);
                var existing = effects.SingleOrDefault(item => HostManagerPlacementReceiptKey.Create(item) == keyNow);
                if (existing?.Records.Any(record => !IsAutomaticPlacementRecord(record)) == true) continue;
                var desired = CreatePreparedGpuShimPlacementDesired(current, existing, action);
                if (desired is null) continue;
                desired = desired with { ActionReservation = reservation };
                IReadOnlyList<HostManagerAppliedPlacementReceipt> selectedEffects = existing is null ? []
                    : [existing with { Records = existing.Records.Where(record =>
                        record.Kind == HostManagerAppliedRecordKinds.GpuShimPolicy).ToArray() }];
                if (!await RunPreparedAutomaticPlacementCycleAsync(admission, latest, [desired], selectedEffects, cancellationToken))
                    return completed.Delay;
            }
        }
        return completed.Delay;
    }

    private HostManagerAutomaticPlacementProcess RefreshFirstUseProcess(HostManagerAutomaticPlacementProcess process)
        => process with
        {
            Policy = runtimePlanProvider.Current.GpuPlacement.Resolve(process.SoftwareId, process.DisplayName, null,
                JsonGpuPlacementProcessHistoryStore.BuildProcessKey(process.ProcessName, process.ExecutablePath))
        };

    private bool CanContinueFirstUsePlacement(HostManagerAutomaticGpuPreferencePlacement planned,
        out HostManagerAutomaticGpuPreferencePlacement current)
    {
        var process = RefreshFirstUseProcess(planned.Process);
        current = planned with { Process = process };
        return LifecycleState is not (HostManagerSmartCoordinatorLifecycleState.Closing or HostManagerSmartCoordinatorLifecycleState.Closed)
            && runtimePlanProvider.Current.OptimizationMode.HardwarePlacementEnabled
            && !runtimePlanProvider.Current.Diagnostics.HostManagerSmartCoordinatorScoreOnlyEnabled
            && HostManagerProcessEffectValidationCyclePolicy.AllowsCycleEffect(
                processEffectValidationScopeAuthorityOwner.Capture(), HostManagerCycleEffectKind.NativeActionTransaction)
            && hostManagerSmartControlZones.CanRun(HostManagerSmartControlZoneIds.HardwarePlacement)
            && WantsGpuShim(process)
            && GpuPlacementTargets.Normalize(process.Policy.TargetGpu) == GpuPlacementTargets.Normalize(planned.Process.Policy.TargetGpu)
            && GpuPlacementRuntimeSwitchMethods.Normalize(process.Policy.PreferredRuntimeSwitchMethod)
                == GpuPlacementRuntimeSwitchMethods.Normalize(planned.Process.Policy.PreferredRuntimeSwitchMethod);
    }
}
