using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Domain.Settings;
using ResourceManager.App.Application.Optimization.SmartControl;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Optimization.Transactions;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using ResourceManager.Adapter.LocalResources;

namespace ResourceManager.App.Infrastructure.Optimization;

public sealed partial class HostManagerSmartCoordinator
{
    private static readonly HostManagerCycleEffectKind[] NewEffectOrder =
    [
        HostManagerCycleEffectKind.NonAdaptedMemoryTransaction,
        HostManagerCycleEffectKind.NativeActionTransaction,
        HostManagerCycleEffectKind.PublicResourceLifecycle,
        HostManagerCycleEffectKind.AutomaticMemoryCleanup
    ];
    private int nextRealtimeNewEffectIndex;

    private async Task RunForegroundNativeCycleAsync(
        string trigger,
        CancellationToken cancellationToken)
        => await RunForegroundNativeCycleAsync(
            trigger,
            outerLoopContext: null,
            cancellationToken);

    private async Task RunForegroundNativeCycleAsync(
        string trigger,
        HostManagerSmartCoordinatorOuterLoopCycleContext? outerLoopContext,
        CancellationToken cancellationToken)
    {
        try
        {
            var delay = await RunNativeCycleCoreAsync(
                trigger,
                realtimeCycle: false,
                outerLoopContext,
                cancellationToken);
            var published = schedulingWakeDeadline.PublishForeground(delay);
            ObserveOuterLoop(
                outerLoopContext,
                HostManagerSmartCoordinatorOuterLoopPhase.NextDeadlinePublished,
                providerTimestamp: schedulingWakeDeadline.GetTimestamp(),
                providerTimestampFrequency: schedulingWakeDeadline.TimestampFrequency,
                publishedDeadline: published);
            ObserveOuterLoop(
                outerLoopContext,
                HostManagerSmartCoordinatorOuterLoopPhase.WrapperCompleted,
                outcome: "completed");
        }
        catch (OperationCanceledException)
        {
            ObserveOuterLoop(
                outerLoopContext,
                HostManagerSmartCoordinatorOuterLoopPhase.WrapperCompleted,
                outcome: "canceled");
            throw;
        }
        catch
        {
            ObserveOuterLoop(
                outerLoopContext,
                HostManagerSmartCoordinatorOuterLoopPhase.WrapperCompleted,
                outcome: "failed");
            throw;
        }
    }

    private async Task<HostManagerWakeDeadlineSnapshot> RunScheduledNativeCycleAsync(
        HostManagerSmartCoordinatorOuterLoopCycleContext? outerLoopContext,
        CancellationToken cancellationToken)
    {
        try
        {
            var delay = await RunNativeCycleCoreAsync(
                "scheduled",
                realtimeCycle: true,
                outerLoopContext,
                cancellationToken);
            var published = schedulingWakeDeadline.PublishScheduled(delay);
            ObserveOuterLoop(
                outerLoopContext,
                HostManagerSmartCoordinatorOuterLoopPhase.NextDeadlinePublished,
                providerTimestamp: schedulingWakeDeadline.GetTimestamp(),
                providerTimestampFrequency: schedulingWakeDeadline.TimestampFrequency,
                publishedDeadline: published);
            ObserveOuterLoop(
                outerLoopContext,
                HostManagerSmartCoordinatorOuterLoopPhase.WrapperCompleted,
                outcome: "completed");
            return published;
        }
        catch (OperationCanceledException)
        {
            ObserveOuterLoop(
                outerLoopContext,
                HostManagerSmartCoordinatorOuterLoopPhase.WrapperCompleted,
                outcome: "canceled");
            throw;
        }
        catch
        {
            ObserveOuterLoop(
                outerLoopContext,
                HostManagerSmartCoordinatorOuterLoopPhase.WrapperCompleted,
                outcome: "failed");
            throw;
        }
    }

    private async Task<HostManagerNativeCycleSegmentResult> RunNativeCycleSegmentAsync(
        string trigger,
        bool realtimeCycle,
        HostManagerSmartCoordinatorOuterLoopCycleContext? outerLoopContext,
        CancellationToken cancellationToken,
        List<HostManagerAutomaticGpuPlacement> firstUse)
    {
        ObserveOuterLoop(
            outerLoopContext,
            HostManagerSmartCoordinatorOuterLoopPhase.CoreEntered);
        using var publicationLease =
            runtimePlanProvider.AcquirePublicationLease();
        var desired = smartCoordinatorRuntime.CaptureDesired(publicationLease);
        var processEffectValidationScope =
            processEffectValidationScopeAuthorityOwner.Capture();
        var effectAdmission = HostManagerCycleEffectAdmission.CreateForValidation(
            desired.RuntimePlan.Diagnostics.HostManagerSmartCoordinatorScoreOnlyEnabled,
            processEffectValidationScope);
        using var cycleDiagnostics = HostManagerSmartCoordinatorCycleDiagnostics.Create(
            this,
            desired.RuntimePlan,
            trigger,
            realtimeCycle,
            effectAdmission.IsScoreOnly,
            outerLoopContext,
            cancellationToken);
        ObserveOuterLoop(
            outerLoopContext,
            HostManagerSmartCoordinatorOuterLoopPhase.ProfilerCreated,
            diagnosticCycleSequence: cycleDiagnostics?.CycleSequence,
            producerInstanceId: cycleDiagnostics?.ProducerInstanceId);
        if (runningGpuPlacementActions.HasUnreleasedExternalControl || !TrySettleGpuCallbackPreparation() || !TrySettleGpuWindowExecution() || !await TrySettleGpuActionCheckpointAsync())
        {
            cycleDiagnostics?.Defer("window-checkpoint-not-settled");
            return new(TimeSpan.FromMilliseconds(
                desired.SmartCoordinator.HotPublish.ReservationTimeoutMilliseconds));
        }
        if (effectAdmission.TryAcquire(
                HostManagerCycleEffectKind.SelfLedgerMaintenance,
                out var selfLedgerPermit))
        {
            await RunSelfLocalResourceManagerTickAsync(
                selfLedgerPermit,
                desired,
                cancellationToken);
        }
        cycleDiagnostics?.Mark("self-ledger");
        var state = await LoadRollbackStateAsync(cancellationToken);
        if (effectAdmission.TryAcquire(
                HostManagerCycleEffectKind.NativeWorkspaceLifecycle,
                out var workspacePermit))
        {
            state = await EnsureNativeWorkspaceAsync(
                workspacePermit,
                desired,
                state,
                cancellationToken);
        }
        else if (!CanReuseNativeWorkspaceForScoreOnly(desired, state))
        {
            cycleDiagnostics?.Defer("native-workspace-unavailable-for-score-only");
            return new(TimeSpan.FromMilliseconds(
                desired.SmartCoordinator.HotPublish.ReservationTimeoutMilliseconds));
        }
        cycleDiagnostics?.Mark("workspace");
        var workspace = nativeWorkspace
            ?? throw new InvalidOperationException("The Host Manager smart coordinator workspace was not created.");
        var maximumActionsThisCycle = ResolveMaximumActionsThisCycle(
            realtimeCycle,
            workspace.Capacity.ActionCapacity,
            desired.SmartCoordinator.HotPublish.MaximumActionsPerRealtimeTick);
        effectAdmission.InitializeBudgets(
            maximumActionsThisCycle,
            maximumActionsThisCycle);
        var recovery = await RecoverNativeTransactionsAsync(
            effectAdmission,
            cancellationToken);
        cycleDiagnostics?.Mark("transaction-recovery");
        if (recovery.IsBlocked)
        {
            cycleDiagnostics?.Defer("transaction-recovery-blocked");
            return new(TimeSpan.FromMilliseconds(
                desired.SmartCoordinator.HotPublish.ReservationTimeoutMilliseconds));
        }
        if (recovery.Changed)
        {
            authoritativeAppliedFactsRequired = true;
        }
        if (!await ReconcileGpuRemoteCallsAsync(effectAdmission, cancellationToken))
        {
            cycleDiagnostics?.Defer("gpu-call-settlement-not-saved");
            return new(TimeSpan.FromMilliseconds(desired.SmartCoordinator.HotPublish.ReservationTimeoutMilliseconds));
        }
        if (!await PrepareLegacyStateAsync(effectAdmission, cancellationToken))
        {
            cycleDiagnostics?.Defer("legacy-state-preparation-blocked");
            return new(TimeSpan.FromMilliseconds(
                desired.SmartCoordinator.HotPublish.ReservationTimeoutMilliseconds));
        }
        cycleDiagnostics?.Mark("legacy-state");
        if (!await ReconcileExitedGpuWindowActionsAsync(effectAdmission, cancellationToken))
        {
            cycleDiagnostics?.Defer("window-exit-settlement-not-saved");
            return new(TimeSpan.FromMilliseconds(
                desired.SmartCoordinator.HotPublish.ReservationTimeoutMilliseconds));
        }
        var runtimePlan = desired.RuntimePlan;
        var modePlan = runtimePlan.OptimizationMode;
        if (!modePlan.SchedulingEnabled)
        {
            hostPublicResourceNormalReleaseEvidence.Stop();
            ClearNonAdaptedMemoryModeProjection();
            schedulingAuthorityOwner.PublishUnavailable(
                NextComputeScoringGeneration(),
                HostManagerSchedulingPlanBinding.Create(desired),
                durableTimeSource.NextUtc(),
                "optimization-mode-normal");
        }
        var scoreOnlyEnabled = effectAdmission.IsScoreOnly;
        HostManagerTransactionJournalAdmission? executionAdmission = null;
        if (!scoreOnlyEnabled
            && (!nativeActionTransactions.TryAcquireExecutionAdmission(out executionAdmission)
                || executionAdmission is null))
        {
            cycleDiagnostics?.Defer("transaction-execution-admission-unavailable");
            return new(TimeSpan.FromMilliseconds(
                desired.SmartCoordinator.HotPublish.ReservationTimeoutMilliseconds));
        }
        await using var executionAdmissionOwner = executionAdmission;
        if (!scoreOnlyEnabled
            && string.Equals(
                modePlan.Mode,
                AppOptimizationModes.Normal,
                StringComparison.Ordinal)
            && !await RestoreOwnedEffectsForNormalModeAsync(
                effectAdmission,
                processEffectValidationScope,
                executionAdmission
                    ?? throw new InvalidOperationException(
                        "Normal-mode ownership restoration has no journal admission."),
                maximumActionsThisCycle,
                cancellationToken))
        {
            cycleDiagnostics?.Defer("normal-mode-ownership-restoration-incomplete");
            return new(TimeSpan.FromMilliseconds(
                desired.SmartCoordinator.HotPublish.ReservationTimeoutMilliseconds));
        }
        if (!modePlan.SchedulingEnabled)
        {
            RunNormalModePublicResourceLifecycle(effectAdmission, desired);
            cycleDiagnostics?.Complete();
            return new(runtimePlan.HostManager.SchedulerSamplingInterval, effectAdmission);
        }
        var schedulingPlanBinding = HostManagerSchedulingPlanBinding.Create(desired);
        var sampleCapture = await CaptureHostManagerSampleAsync(
            desired.RuntimePlan,
            cancellationToken);
        if (sampleCapture.Sample is not { } sample)
        {
            var unavailableReason = sampleCapture.UnavailableReason
                ?? "sampling-snapshot-unavailable";
            PublishSchedulingAuthorityUnavailable(
                NextComputeScoringGeneration(),
                schedulingPlanBinding,
                durableTimeSource.NextUtc(),
                unavailableReason);
            cycleDiagnostics?.Defer(unavailableReason);
            return new(TimeSpan.FromMilliseconds(
                desired.SmartCoordinator.HotPublish.ReservationTimeoutMilliseconds));
        }
        cycleDiagnostics?.CaptureSample(sample);
        cycleDiagnostics?.Mark("sampling");
        var computeScoring = RunSchedulingAuthority(
            desired.SmartCoordinator,
            desired.RuntimePlan.HostManager.CpuScoring!,
            modePlan,
            schedulingPlanBinding,
            sample);
        cycleDiagnostics?.CaptureScoring(computeScoring);
        var policyExecutionEnabled = hostManagerSmartControlZones.CanRun(
            HostManagerSmartControlZoneIds.PolicyExecution);
        var hardwareExecutionAvailable = hostManagerSmartControlZones.CanRun(
            HostManagerSmartControlZoneIds.HardwarePlacement);
        var cpuPlacementEnabled = modePlan.CpuPlacementEnabled
            && desired.RuntimePlan.CpuPlacementTopology is not null
            && hardwareExecutionAvailable;
        var gpuPlacementEnabled = modePlan.GpuPlacementEnabled
            && hardwareExecutionAvailable;
        cycleDiagnostics?.CaptureGuards(
            policyExecutionEnabled,
            cpuPlacementEnabled || gpuPlacementEnabled);
        var protectionLevels = await ResolveNativeProtectionLevelsAsync(cancellationToken);
        cycleDiagnostics?.Mark("scheduling-authority");
        var ownershipSnapshot = effectAdmission.TryAcquire(
            HostManagerCycleEffectKind.ExitedOwnershipReconciliation,
            out var ownershipPermit)
            ? await ReconcileExitedProcessOwnershipAsync(
                ownershipPermit,
                sample.ProcessFacts,
                executionAdmission
                    ?? throw new InvalidOperationException(
                        "The ownership reconciliation effect has no journal execution admission."),
                cancellationToken)
            : await nativeActionTransactions.AppliedOwnership.ReadSnapshotAsync(
                cancellationToken);
        cycleDiagnostics?.Mark("ownership-reconciliation");
        var sequence = unchecked((ulong)Interlocked.Increment(ref nativeCycleSequence));
        if (sequence == 0)
        {
            throw new InvalidOperationException("The Host Manager smart coordinator cycle sequence wrapped to zero.");
        }
        ObserveOuterLoop(
            outerLoopContext,
            HostManagerSmartCoordinatorOuterLoopPhase.NativeSequenceAssigned,
            nativeCycleSequence: sequence);
        var firstEffectIndex = realtimeCycle ? nextRealtimeNewEffectIndex : 0;
        var externalCycle = CreateNativeExternalEffectCycle(
            effectAdmission,
            processEffectValidationScope,
            sample,
            modePlan,
            desired.RuntimePlan,
            desired.HostPlan,
            desired.RuntimePlan.Version,
            desired.PublicationSequence,
            sequence,
            cancellationToken);
        try
        {
            for (var offset = 0; offset < NewEffectOrder.Length; offset++)
            {
                var effectKind = NewEffectOrder[
                    (firstEffectIndex + offset) % NewEffectOrder.Length];
                switch (effectKind)
                {
                    case HostManagerCycleEffectKind.NonAdaptedMemoryTransaction:
                        var memoryTransactions = default(NonAdaptedMemoryTransactionBatchResult);
                        if (effectAdmission.TryAcquire(effectKind, out var memoryTransactionPermit))
                        {
                            memoryTransactions = await ApplyNonAdaptedMemoryModeTransactionsAsync(
                                memoryTransactionPermit,
                                processEffectValidationScope,
                                schedulingPlanBinding,
                                sample,
                                CreateAppliedOwnershipFactIndex(ownershipSnapshot),
                                executionAdmission
                                    ?? throw new InvalidOperationException(
                                        "The memory transaction effect has no journal execution admission."),
                                maximumActionsThisCycle,
                                policyExecutionEnabled && modePlan.NonAdaptedMemoryPriorityEnabled,
                                cancellationToken);
                        }
                        cycleDiagnostics?.CaptureMemoryTransactions(memoryTransactions.TransactionCount);
                        cycleDiagnostics?.Mark("memory-transactions");
                        if (memoryTransactions.IsBlocked)
                        {
                            cycleDiagnostics?.Defer("memory-transaction-blocked");
                            return new(TimeSpan.FromMilliseconds(
                                desired.SmartCoordinator.HotPublish.ReservationTimeoutMilliseconds));
                        }
                        if (memoryTransactions.TransactionCount != 0)
                        {
                            ownershipSnapshot = await nativeActionTransactions.AppliedOwnership
                                .ReadSnapshotAsync(cancellationToken);
                        }
                        break;
                    case HostManagerCycleEffectKind.NativeActionTransaction:
                        if (!await RunNativePlanningAndActionsAsync(
                                effectAdmission,
                                processEffectValidationScope,
                                desired,
                                sample,
                                computeScoring,
                                CreateAppliedOwnershipFactIndex(ownershipSnapshot),
                                protectionLevels,
                                policyExecutionEnabled,
                                cpuPlacementEnabled,
                                gpuPlacementEnabled,
                                executionAdmission,
                                sequence,
                                trigger,
                                cycleDiagnostics,
                                cancellationToken,
                                firstUse))
                        {
                            return new(TimeSpan.FromMilliseconds(
                                desired.SmartCoordinator.HotPublish.ReservationTimeoutMilliseconds));
                        }
                        if (!scoreOnlyEnabled)
                        {
                            ownershipSnapshot = await nativeActionTransactions.AppliedOwnership
                                .ReadSnapshotAsync(cancellationToken);
                        }
                        break;
                    case HostManagerCycleEffectKind.PublicResourceLifecycle:
                    case HostManagerCycleEffectKind.AutomaticMemoryCleanup:
                        await HostManagerNativeExternalEffectDispatcher.RunAsync(
                            effectAdmission,
                            modePlan.AutomaticMemoryCleanupEnabled,
                            policyExecutionEnabled,
                            effectKind,
                            externalCycle.Sink);
                        cycleDiagnostics?.Mark(effectKind == HostManagerCycleEffectKind.PublicResourceLifecycle
                            ? "public-resources"
                            : "memory-cleanup");
                        if (effectKind == HostManagerCycleEffectKind.AutomaticMemoryCleanup
                            && externalCycle.Sink.MemoryCleanupResult.HasUnresolvedCurrentEffects)
                        {
                            cycleDiagnostics?.Defer("memory-cleanup-unresolved-effects");
                            return new(TimeSpan.FromMilliseconds(
                                desired.SmartCoordinator.HotPublish.ReservationTimeoutMilliseconds));
                        }
                        break;
                }
            }
        }
        finally
        {
            hostPublicResourceNormalReleaseEvidence.CompleteCycle(
                externalCycle.ReleaseCycle,
                externalCycle.Sink.MemoryCleanupResult);
        }

        if (realtimeCycle && !scoreOnlyEnabled)
        {
            nextRealtimeNewEffectIndex = (firstEffectIndex + 1) % NewEffectOrder.Length;
        }
        cycleDiagnostics?.Complete();

        return new(TimeSpan.FromMilliseconds(workspace.Snapshot.WakeAfterMilliseconds), effectAdmission);
    }

    private async Task RunSelfLocalResourceManagerTickAsync(
        HostManagerCycleEffectPermit permit,
        HostManagerSmartCoordinatorRuntimePlan desired,
        CancellationToken cancellationToken)
    {
        permit.Require(HostManagerCycleEffectKind.SelfLedgerMaintenance);
        try
        {
            var mode = HostManagerSelfMemoryModeProjection.Resolve(
                desired.RuntimePlan.OptimizationMode.ResourceManagerSelfMemoryActionsEnabled,
                HostManagerSchedulingPlanBinding.Create(desired),
                schedulingAuthorityOwner.Capture());
            selfLocalResourceManagerOwner.SetDesiredMemoryMode(mode);
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Resource Manager self memory-mode projection failed closed to Normal for this cycle.");
            try
            {
                selfLocalResourceManagerOwner.SetDesiredMemoryMode(
                    LocalResourceSoftwareMemoryMode.Normal);
            }
            catch (Exception fallbackException)
            {
                logger.LogWarning(
                    fallbackException,
                    "Resource Manager self memory-mode Normal fallback could not be published; capacity maintenance will still run.");
            }
        }

        try
        {
            var result = await selfLocalResourceManagerOwner.TickAsync(
                cancellationToken);
            if (result.Manager.CapacityActionCount != 0)
            {
                var releasedSlots = result.Manager.Tables.Sum(
                    static table => table.ReleasedSlotCount);
                logger.LogInformation(
                    "Resource Manager self ledger capacity maintenance attempted {ActionCount} actions, released {ReleasedSlotCount} entries, and left {RemainingSlots} free slots.",
                    result.Manager.CapacityActionCount,
                    releasedSlots,
                    result.Capacity.FreeCount);
            }
            if (result.Manager.UncertainExecutions.Count != 0)
            {
                logger.LogWarning(
                    "Resource Manager self ledger added {NewPendingCount} uncertain executions; {PendingCount} remain unresolved.",
                    result.Manager.UncertainExecutions.Count,
                    result.PendingUncertainExecutionCount);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Resource Manager self ledger maintenance failed closed for this cycle.");
        }
    }

    internal static uint ResolveMaximumActionsThisCycle(
        bool realtimeCycle,
        uint actionCapacity,
        int maximumActionsPerRealtimeTick)
    {
        if (actionCapacity == 0
            || maximumActionsPerRealtimeTick <= 0
            || maximumActionsPerRealtimeTick > actionCapacity)
        {
            throw new InvalidDataException(
                "The Host Manager action capacity and realtime action limit are inconsistent.");
        }

        return realtimeCycle
            ? checked((uint)maximumActionsPerRealtimeTick)
            : actionCapacity;
    }

    private async Task<HostManagerRollbackStateDocument> EnsureNativeWorkspaceAsync(
        HostManagerCycleEffectPermit permit,
        HostManagerSmartCoordinatorRuntimePlan desired,
        HostManagerRollbackStateDocument state,
        CancellationToken cancellationToken)
    {
        permit.Require(HostManagerCycleEffectKind.NativeWorkspaceLifecycle);
        var needsNewIncarnation = nativeWorkspace is null
            || appliedNativeRuntimePlan is null
            || appliedNativeRuntimePlan.SmartCoordinator.Recreate != desired.SmartCoordinator.Recreate;
        if (needsNewIncarnation)
        {
            state = await ReserveRollbackSessionAsync(
                cancellationToken);
            var nextIncarnation = state.NativeHostSessionIncarnation;
            EnsureNativeWorkspace(desired);
            nativeHostSessionIncarnation = nextIncarnation;
            return state;
        }

        if (nativeHostSessionIncarnation == 0
            || state.NativeHostSessionIncarnation != nativeHostSessionIncarnation)
        {
            throw new InvalidDataException(
                "The Host Manager native workspace lost its durable host session incarnation.");
        }

        EnsureNativeWorkspace(desired);
        return state;
    }

    private bool CanReuseNativeWorkspaceForScoreOnly(
        HostManagerSmartCoordinatorRuntimePlan desired,
        HostManagerRollbackStateDocument state)
    {
        if (nativeWorkspace is null || appliedNativeRuntimePlan is null)
        {
            return false;
        }

        if (nativeHostSessionIncarnation == 0
            || state.NativeHostSessionIncarnation != nativeHostSessionIncarnation)
        {
            throw new InvalidDataException(
                "The Host Manager native workspace lost its durable host session incarnation.");
        }

        var applied = appliedNativeRuntimePlan;
        if (applied.Configuration.Generation != desired.Configuration.Generation)
        {
            return false;
        }
        if (!string.Equals(
                applied.SmartCoordinator.ConfigurationSha256,
                desired.SmartCoordinator.ConfigurationSha256,
                StringComparison.Ordinal)
            || applied.SmartCoordinator.Build != desired.SmartCoordinator.Build)
        {
            throw new InvalidOperationException(
                "The Host Manager smart coordinator configuration drifted without a profile revision change.");
        }

        return true;
    }

    private void EnsureNativeWorkspace(HostManagerSmartCoordinatorRuntimePlan desired)
    {
        if (nativeWorkspace is null)
        {
            var attempt = smartCoordinatorRuntime.BeginInitialCreate(desired.HostPlan);
            var configuration = desired.Configuration;
            NativeSmartCoordinatorWorkspace? created = null;
            try
            {
                created = new NativeSmartCoordinatorWorkspace(in configuration);
            }
            catch (Exception exception)
            {
                SettleSmartCoordinatorFailed(
                    attempt,
                    $"native-smart-coordinator:{exception.GetType().Name}");
                throw;
            }

            try
            {
                HostManagerDeploymentAttemptSettlementGuard.RequireApplied(
                    smartCoordinatorRuntime.CompleteSucceeded(attempt),
                    HostManagerModuleKind.SmartCoordinator);
            }
            catch
            {
                created.Dispose();
                throw;
            }
            nativeWorkspace = created;
            appliedNativeRuntimePlan = desired;
            authoritativeAppliedFactsRequired = true;
            return;
        }

        var applied = appliedNativeRuntimePlan
            ?? throw new InvalidOperationException("The applied smart coordinator runtime identity is missing.");
        if (applied.Configuration.Generation == desired.Configuration.Generation)
        {
            if (!string.Equals(
                    applied.SmartCoordinator.ConfigurationSha256,
                    desired.SmartCoordinator.ConfigurationSha256,
                    StringComparison.Ordinal)
                || applied.SmartCoordinator.Build != desired.SmartCoordinator.Build)
            {
                throw new InvalidOperationException(
                    "The Host Manager smart coordinator configuration drifted without a profile revision change.");
            }

            return;
        }

        if (desired.Configuration.Generation < applied.Configuration.Generation)
        {
            throw new InvalidOperationException(
                $"The Host Manager smart coordinator generation must increase: applied {applied.Configuration.Generation}, desired {desired.Configuration.Generation}.");
        }

        if (nativeWorkspace.Snapshot.Flags.HasFlag(NativeSmartCoordinatorSnapshotFlags.AwaitingFeedback))
        {
            throw new InvalidOperationException(
                "The Host Manager smart coordinator cannot change generation while feedback is outstanding.");
        }

        if (applied.SmartCoordinator.Recreate == desired.SmartCoordinator.Recreate)
        {
            if (!smartCoordinatorRuntime.CanApplyHot(desired.HostPlan))
            {
                throw new InvalidOperationException(
                    "The Host Manager deployment state rejected the smart coordinator hot generation.");
            }

            var attempt = smartCoordinatorRuntime.BeginHotPublish(desired.HostPlan);
            try
            {
                var configuration = desired.Configuration;
                RequireNativeStatus(nativeWorkspace.Reconfigure(in configuration), "reconfigure");
            }
            catch (Exception exception)
            {
                SettleSmartCoordinatorFailed(
                    attempt,
                    $"native-smart-coordinator:{exception.GetType().Name}");
                throw;
            }

            try
            {
                HostManagerDeploymentAttemptSettlementGuard.RequireApplied(
                    smartCoordinatorRuntime.CompleteSucceeded(attempt),
                    HostManagerModuleKind.SmartCoordinator);
            }
            catch
            {
                var previousConfiguration = applied.Configuration;
                RequireNativeStatus(
                    nativeWorkspace.Reconfigure(in previousConfiguration),
                    "rollback-stale-hot-publish");
                throw;
            }
            appliedNativeRuntimePlan = desired;
            return;
        }

        var recreateAttempt = smartCoordinatorRuntime.BeginHostRecreateAndHotPublish(desired.HostPlan);
        NativeSmartCoordinatorWorkspace replacement;
        try
        {
            ReachTransitionPoint(
                HostManagerSmartCoordinatorTransitionPoint.BeforeReplacementCreate);
            var configuration = desired.Configuration;
            replacement = new NativeSmartCoordinatorWorkspace(in configuration);
            ReachTransitionPoint(
                HostManagerSmartCoordinatorTransitionPoint.AfterReplacementCreate);
        }
        catch (Exception exception)
        {
            SettleSmartCoordinatorFailed(
                recreateAttempt,
                $"native-smart-coordinator:{exception.GetType().Name}");
            throw;
        }

        try
        {
            ReachTransitionPoint(
                HostManagerSmartCoordinatorTransitionPoint.BeforeDeploymentSettlement);
            HostManagerDeploymentAttemptSettlementGuard.RequireApplied(
                smartCoordinatorRuntime.CompleteSucceeded(recreateAttempt),
                HostManagerModuleKind.SmartCoordinator);
            ReachTransitionPoint(
                HostManagerSmartCoordinatorTransitionPoint.AfterDeploymentSettlement);
        }
        catch
        {
            replacement.Dispose();
            throw;
        }
        ReachTransitionPoint(HostManagerSmartCoordinatorTransitionPoint.BeforePointerCutover);
        var previous = nativeWorkspace;
        nativeWorkspace = replacement;
        appliedNativeRuntimePlan = desired;
        authoritativeAppliedFactsRequired = true;
        ReachTransitionPoint(HostManagerSmartCoordinatorTransitionPoint.AfterPointerCutover);
        previous.Dispose();
    }

    private void SettleSmartCoordinatorFailed(
        HostManagerDeploymentAttemptToken attempt,
        string failureCode)
        => HostManagerDeploymentAttemptSettlementGuard.RequireFailed(
            smartCoordinatorRuntime.CompleteFailed(attempt, failureCode, null),
            HostManagerModuleKind.SmartCoordinator);

    private HostManagerRollbackStateDocument ProjectNativeState(HostManagerRollbackStateDocument state)
    {
        if (nativeWorkspace is null)
        {
            return state;
        }

        var snapshot = nativeWorkspace.Snapshot;
        return state with
        {
            LastRunAt = snapshot.ObservedAtMilliseconds == 0
                ? state.LastRunAt
                : DateTimeOffset.FromUnixTimeMilliseconds(snapshot.ObservedAtMilliseconds),
            Message = $"Host Manager Zig coordinator revision {snapshot.StateRevision}; "
                + $"pending {snapshot.PendingCount}, inflight {snapshot.InflightCount}, "
                + $"reasons 0x{(ulong)snapshot.ReasonMask:X}."
        };
    }

    private static void RequireNativeStatus(
        NativeSmartCoordinatorStatus status,
        string operation)
    {
        if (status != NativeSmartCoordinatorStatus.Ok)
        {
            throw new InvalidDataException(
                $"Host Manager smart coordinator {operation} failed with status {(int)status} ({status}).");
        }
    }

    private static void RequireNativePlanStatus(
        NativeSmartCoordinatorStatus status,
        NativeSmartCoordinatorWorkspace workspace,
        uint inputCount,
        in NativeSmartCoordinatorSnapshot previousSnapshot)
    {
        if (status == NativeSmartCoordinatorStatus.Ok)
        {
            return;
        }

        var processCount = 0U;
        var softwareKeys = new HashSet<ulong>();
        var gpuStates = new HashSet<(ulong TargetKey, uint ProcessId,
            ulong ProcessStartKey, uint DeviceIndex)>();
        foreach (var row in workspace.InputRows[..checked((int)inputCount)])
        {
            if (row.ValidMask.HasFlag(
                    NativeSmartCoordinatorInputValidity.ProcessIdentity)
                && row.MetricKind == NativeSmartCoordinatorMetricKind.None)
            {
                processCount++;
            }
            if (row.ValidMask.HasFlag(
                    NativeSmartCoordinatorInputValidity.SoftwareIdentity))
            {
                softwareKeys.Add(row.SoftwareKey);
            }
            if (row.MetricKind is NativeSmartCoordinatorMetricKind.GpuUsagePercent
                    or NativeSmartCoordinatorMetricKind.VramUsagePercent)
            {
                gpuStates.Add((
                    row.TargetKey,
                    row.ProcessId,
                    row.ProcessStartKey,
                    row.DeviceIndex));
            }
        }

        var capacity = workspace.Capacity;
        throw new InvalidDataException(
            $"Host Manager smart coordinator plan failed with status {(int)status} ({status}); "
            + $"projected input/process/software/gpu counts {inputCount}/{processCount}/{softwareKeys.Count}/{gpuStates.Count}; "
            + $"capacities input/process/software/gpu/action/feedback/atomic "
            + $"{capacity.InputRowCapacity}/{capacity.ProcessCapacity}/{capacity.SoftwareCapacity}/{capacity.GpuStateCapacity}/{capacity.ActionCapacity}/{capacity.FeedbackCapacity}/{capacity.AtomicGroupCapacity}; "
            + $"previous snapshot process/software/rows/pending/inflight/actions "
            + $"{previousSnapshot.ProcessCount}/{previousSnapshot.SoftwareCount}/{previousSnapshot.SnapshotRowCount}/{previousSnapshot.PendingCount}/{previousSnapshot.InflightCount}/{previousSnapshot.ActionCount}.");
    }

    internal static unsafe void ValidateNativeSnapshot(
        NativeSmartCoordinatorSnapshot snapshot,
        NativeSmartCoordinatorCapacity capacity,
        ulong expectedConfigurationGeneration,
        ulong expectedCycleSequence,
        long expectedObservedAtMilliseconds,
        long expectedScheduleBaseAtMilliseconds,
        ulong expectedPlanEpoch,
        uint expectedMaximumActionsThisCycle,
        bool requireNoActions,
        bool expectedScoreOnly)
    {
        var expectedNextWake = expectedScheduleBaseAtMilliseconds > long.MaxValue - snapshot.WakeAfterMilliseconds
            ? long.MaxValue
            : expectedScheduleBaseAtMilliseconds + snapshot.WakeAfterMilliseconds;
        var invalidFactsFlag = snapshot.Flags.HasFlag(NativeSmartCoordinatorSnapshotFlags.HasInvalidFacts);
        var awaitingFeedbackFlag = snapshot.Flags.HasFlag(NativeSmartCoordinatorSnapshotFlags.AwaitingFeedback);
        var scoreOnlyFlag = snapshot.Flags.HasFlag(NativeSmartCoordinatorSnapshotFlags.ScoreOnly);
        var rowCount = checked((ulong)snapshot.ProcessCount + snapshot.SoftwareCount);
        var maximumPlanActions = Math.Min(rowCount, expectedMaximumActionsThisCycle);
        var maximumPending = checked((ulong)snapshot.ProcessCount + (2UL * snapshot.SoftwareCount));
        if (snapshot.AbiVersion != NativeSmartCoordinatorAbi.Version
            || snapshot.StructSize != NativeSmartCoordinatorSession.SizeOf<NativeSmartCoordinatorSnapshot>()
            || snapshot.SnapshotRowStructSize != NativeSmartCoordinatorSession.SizeOf<NativeSmartCoordinatorSnapshotRow>()
            || (snapshot.Flags & ~NativeSmartCoordinatorSnapshotFlags.Known) != 0
            || (snapshot.ReasonMask & ~NativeSmartCoordinatorReason.Known) != 0
            || snapshot.ConfigurationGeneration != expectedConfigurationGeneration
            || snapshot.ConfigurationGeneration != capacity.ConfigurationGeneration
            || snapshot.CycleSequence != expectedCycleSequence
            || snapshot.PlanEpoch == 0
            || (expectedPlanEpoch != 0 && snapshot.PlanEpoch != expectedPlanEpoch)
            || snapshot.StateRevision == 0
            || snapshot.ObservedAtMilliseconds != expectedObservedAtMilliseconds
            || snapshot.NextWakeAtMilliseconds != expectedNextWake
            || expectedMaximumActionsThisCycle > capacity.ActionCapacity
            || snapshot.ActionCount > capacity.ActionCapacity
            || snapshot.ProcessCount > capacity.ProcessCapacity
            || snapshot.SoftwareCount > capacity.SoftwareCapacity
            || snapshot.InflightCount > capacity.FeedbackCapacity
            || snapshot.SnapshotRowCount > capacity.SnapshotRowCapacity
            || snapshot.SnapshotRowCount != rowCount
            || snapshot.PendingCount > maximumPending
            || invalidFactsFlag != (snapshot.InvalidFactCount != 0)
            || awaitingFeedbackFlag != (snapshot.InflightCount != 0)
            || scoreOnlyFlag != expectedScoreOnly
            || (requireNoActions && snapshot.ActionCount != 0)
            || (!requireNoActions && snapshot.ActionCount > maximumPlanActions)
            || snapshot.Reserved[0] != 0
            || snapshot.Reserved[1] != 0)
        {
            throw new InvalidDataException(
                "The Host Manager smart coordinator returned an invalid snapshot envelope.");
        }
    }

    internal static void ValidateNativeSnapshotRows(
        ReadOnlySpan<NativeSmartCoordinatorSnapshotRow> rows,
        NativeSmartCoordinatorSnapshot snapshot)
    {
        if (rows.Length != snapshot.SnapshotRowCount)
        {
            throw new InvalidDataException(
                "The Host Manager smart coordinator snapshot row span does not match its envelope.");
        }

        var processIdentities = new HashSet<(ulong TargetKey, uint ProcessId, ulong ProcessStartKey)>();
        var softwareIdentities = new HashSet<ulong>();
        uint pendingCount = 0;
        uint minimumInflightCount = 0;
        uint maximumInflightCount = 0;
        var processFlags = NativeSmartCoordinatorSnapshotRowFlags.ProcessOwned
            | NativeSmartCoordinatorSnapshotRowFlags.ProcessInflight
            | NativeSmartCoordinatorSnapshotRowFlags.FreezeReady
            | NativeSmartCoordinatorSnapshotRowFlags.CriticalFact;
        var softwareFlags = NativeSmartCoordinatorSnapshotRowFlags.CpuOwned
            | NativeSmartCoordinatorSnapshotRowFlags.GpuOwned
            | NativeSmartCoordinatorSnapshotRowFlags.CpuInflight
            | NativeSmartCoordinatorSnapshotRowFlags.GpuInflight
            | NativeSmartCoordinatorSnapshotRowFlags.CriticalFact;
        var softwareValidity = NativeSmartCoordinatorInputValidity.SoftwareIdentity
            | NativeSmartCoordinatorInputValidity.SoftwareKind
            | NativeSmartCoordinatorInputValidity.SurfaceFacts
            | NativeSmartCoordinatorInputValidity.BaseScore
            | NativeSmartCoordinatorInputValidity.Eligibility
            | NativeSmartCoordinatorInputValidity.CpuCapabilities
            | NativeSmartCoordinatorInputValidity.GpuCapabilities
            | NativeSmartCoordinatorInputValidity.AppliedCpuGrade
            | NativeSmartCoordinatorInputValidity.AppliedGpuGrade;

        for (var index = 0; index < rows.Length; index++)
        {
            var row = rows[index];
            if (row.StructSize != NativeSmartCoordinatorSession.SizeOf<NativeSmartCoordinatorSnapshotRow>()
                || (row.Flags & ~NativeSmartCoordinatorSnapshotRowFlags.Known) != 0
                || (row.ValidMask & ~NativeSmartCoordinatorInputValidity.Known) != 0
                || (row.ReasonMask & ~NativeSmartCoordinatorReason.Known) != 0
                || !Enum.IsDefined(row.RowKind)
                || !Enum.IsDefined(row.SoftwareKind)
                || !Enum.IsDefined(row.RuntimeState)
                || row.ProtectionLevel > 2
                || !Enum.IsDefined(row.DesiredProcessGrade)
                || !Enum.IsDefined(row.AppliedProcessGrade)
                || !Enum.IsDefined(row.PendingProcessGrade)
                || !Enum.IsDefined(row.DesiredCpuGrade)
                || !Enum.IsDefined(row.AppliedCpuGrade)
                || !Enum.IsDefined(row.PendingCpuGrade)
                || !Enum.IsDefined(row.DesiredGpuGrade)
                || !Enum.IsDefined(row.AppliedGpuGrade)
                || !Enum.IsDefined(row.PendingGpuGrade)
                || !IsNativeCapabilityMask(row.CpuCapabilityMask)
                || !IsNativeCapabilityMask(row.GpuCapabilityMask)
                || !IsFiniteNonNegative(row.BaseScore)
                || !IsFiniteNonNegative(row.CpuScore)
                || !IsFiniteNonNegative(row.CpuOccupancyPercent)
                || row.CpuOccupancyPercent > 100
                || row.ReservedProcessScore0 != 0
                || row.ReservedProcessRatio0 != 0
                || !IsFiniteNonNegative(row.AdapterCpuScore)
                || !IsFiniteNonNegative(row.AdapterGpuScore)
                || row.LastSeenCycle == 0
                || row.LastSeenCycle > snapshot.CycleSequence
                || row.Reserved0 != 0
                || row.Reserved1 != 0)
            {
                throw new InvalidDataException(
                    $"Host Manager smart coordinator snapshot row {index} failed its POD contract.");
            }

            if (row.RowKind == NativeSmartCoordinatorSnapshotRowKind.Process)
            {
                var hasSoftware = row.ValidMask.HasFlag(NativeSmartCoordinatorInputValidity.SoftwareIdentity);
                if ((uint)index >= snapshot.ProcessCount
                    || !row.ValidMask.HasFlag(NativeSmartCoordinatorInputValidity.ProcessIdentity)
                    || row.ValidMask.HasFlag(NativeSmartCoordinatorInputValidity.Metric)
                    || row.TargetKey == 0
                    || row.ProcessId == 0
                    || row.ProcessStartKey == 0
                    || hasSoftware != (row.SoftwareKey != 0)
                    || (!hasSoftware
                        && (row.ValidMask & (NativeSmartCoordinatorInputValidity.AppliedCpuGrade
                            | NativeSmartCoordinatorInputValidity.AppliedGpuGrade)) != 0)
                    || (row.Flags & ~processFlags) != 0
                    || row.CpuPendingCount != 0
                    || row.GpuPendingCount != 0
                    || row.AdapterCpuScore != 0
                    || row.AdapterGpuScore != 0
                    || row.DesiredCpuGrade != NativeSmartCoordinatorAdapterGrade.Normal
                    || row.AppliedCpuGrade != NativeSmartCoordinatorAdapterGrade.Normal
                    || row.PendingCpuGrade != NativeSmartCoordinatorAdapterGrade.Normal
                    || row.DesiredGpuGrade != NativeSmartCoordinatorAdapterGrade.Normal
                    || row.AppliedGpuGrade != NativeSmartCoordinatorAdapterGrade.Normal
                    || row.PendingGpuGrade != NativeSmartCoordinatorAdapterGrade.Normal
                    || !processIdentities.Add((row.TargetKey, row.ProcessId, row.ProcessStartKey)))
                {
                    throw new InvalidDataException(
                        $"Host Manager smart coordinator process snapshot row {index} has an invalid shape.");
                }

                if (row.ProcessPendingCount != 0)
                {
                    pendingCount++;
                }
                if (row.Flags.HasFlag(NativeSmartCoordinatorSnapshotRowFlags.ProcessInflight))
                {
                    minimumInflightCount++;
                    maximumInflightCount++;
                }
            }
            else
            {
                var cpuInflight = row.Flags.HasFlag(NativeSmartCoordinatorSnapshotRowFlags.CpuInflight);
                var gpuInflight = row.Flags.HasFlag(NativeSmartCoordinatorSnapshotRowFlags.GpuInflight);
                if ((uint)index < snapshot.ProcessCount
                    || (row.ValidMask & NativeSmartCoordinatorInputValidity.SoftwareIdentity) == 0
                    || (row.ValidMask & ~softwareValidity) != 0
                    || row.TargetKey == 0
                    || row.TargetKey != row.SoftwareKey
                    || row.ProcessId != 0
                    || row.ProcessStartKey != 0
                    || (row.Flags & ~softwareFlags) != 0
                    || row.ProcessPendingCount != 0
                    || row.GameStartedAtMilliseconds != 0
                    || row.ProtectionLevel != 0
                    || row.CpuOccupancyPercent != 0
                    || row.DesiredProcessGrade != NativeSmartCoordinatorProcessGrade.Normal
                    || row.AppliedProcessGrade != NativeSmartCoordinatorProcessGrade.Normal
                    || row.PendingProcessGrade != NativeSmartCoordinatorProcessGrade.Normal
                    || !softwareIdentities.Add(row.SoftwareKey))
                {
                    throw new InvalidDataException(
                        $"Host Manager smart coordinator software snapshot row {index} has an invalid shape.");
                }

                if (row.CpuPendingCount != 0)
                {
                    pendingCount++;
                }
                if (row.GpuPendingCount != 0)
                {
                    pendingCount++;
                }
                if (cpuInflight || gpuInflight)
                {
                    minimumInflightCount++;
                }
                if (cpuInflight)
                {
                    maximumInflightCount++;
                }
                if (gpuInflight)
                {
                    maximumInflightCount++;
                }
            }
        }

        if (processIdentities.Count != snapshot.ProcessCount
            || softwareIdentities.Count != snapshot.SoftwareCount
            || pendingCount != snapshot.PendingCount
            || snapshot.InflightCount < minimumInflightCount
            || snapshot.InflightCount > maximumInflightCount)
        {
            throw new InvalidDataException(
                "The Host Manager smart coordinator snapshot row aggregates do not match their envelope.");
        }
    }

    private static bool IsNativeCapabilityMask(byte mask)
        => (mask & 0xF0) == 0 && (mask == 0 || (mask & (1 << (byte)NativeSmartCoordinatorAdapterGrade.Normal)) != 0);

    private static bool IsFiniteNonNegative(double value)
        => double.IsFinite(value) && value >= 0;

    private static ulong NextNativeStateRevision(ulong revision)
    {
        var next = unchecked(revision + 1);
        return next == 0 ? 1 : next;
    }
}
