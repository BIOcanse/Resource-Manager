using System.Diagnostics;
using ResourceManager.App.Application.Adaptation;
using ResourceManager.App.Application.CpuTopology;
using ResourceManager.App.Application.Diagnostics;
using ResourceManager.App.Application.GpuPlacement;
using ResourceManager.App.Application.Metrics;
using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Application.Optimization.Scheduling;
using ResourceManager.App.Application.Optimization.SmartControl;
using ResourceManager.App.Application.Optimization.MemoryCleanup;
using ResourceManager.App.Application.PublicResources;
using ResourceManager.App.Application.ResourceBreakdown;
using ResourceManager.App.Application.RuntimeSpecialization;
using ResourceManager.App.Application.Settings;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Domain.Settings;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.GpuPlacement;
using ResourceManager.App.Infrastructure.GpuPlacement.WindowExecution;
using ResourceManager.App.Infrastructure.GpuPlacement.Preparation;
using ResourceManager.App.Infrastructure.Adaptation;
using ResourceManager.App.Infrastructure.Optimization.NativeScheduling;
using ResourceManager.App.Infrastructure.Optimization.Transactions;
using ResourceManager.App.Infrastructure.PublicResources;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;

namespace ResourceManager.App.Infrastructure.Optimization;

public sealed partial class HostManagerSmartCoordinator(
    IAppSettingsStore settingsStore,
    IMetricSnapshotObservationSource metricSnapshotSource,
    ISchedulingProcessFactObservationSource schedulingProcessFactSource,
    ICpuCoreResidencyReader cpuCoreResidencyReader,
    IHostManagerSmartControlZoneRegistry hostManagerSmartControlZones,
    IHostManagerRollbackStateStore stateStore,
    IOptimizationProtectionService protectionService,
    IRuntimePlanProvider runtimePlanProvider,
    IRuntimeSpecializationCoordinator runtimeSpecializationCoordinator,
    HostManagerSmartCoordinatorRuntime smartCoordinatorRuntime,
    HostManagerPlacementCoordinatorRuntime placementCoordinatorRuntime,
    IAutomaticMemoryCleanupPlanner automaticMemoryCleanupPlanner,
    HostManagerMemoryCleanupAttemptJournal memoryCleanupAttemptJournal,
    IProcessResourcePolicyWriter processPolicyWriter,
    HostManagerProcessPolicyTransaction processPolicyTransaction,
    HostManagerAdapterSchedulingTransaction adapterSchedulingTransaction,
    HostManagerNativeActionTransactionRuntime nativeActionTransactions,
    TimeProvider timeProvider,
    IWindowsGraphicsPreferenceStore graphicsPreferenceStore,
    D3d11ProxyShimRuntime gpuShimRuntime,
    WindowsGpuWindowActionRuntime gpuWindowRuntime,
    WindowsGpuCallbackPreparationRuntime gpuCallbackRuntime,
    IRunningGpuPlacementActionService runningGpuPlacementActions,
    IDebugDiagnosticLogWriter debugDiagnosticLogWriter,
    ILogger<HostManagerSmartCoordinator> logger,
    ResourceManagerSelfLocalResourceManager selfLocalResourceManager,
    IHostPublicResourceSelfManager hostPublicResourceSelfManager,
    HostManagerSchedulingAuthority schedulingAuthority,
    HostManagerMemoryModePolicyAuthority memoryModePolicyAuthority,
    HostManagerProcessEffectValidationScopeAuthority processEffectValidationScopeAuthority,
    RuntimeCpuLeaseStore cpuLeaseStore)
    : BackgroundService,
        IHostManagerSmartCoordinator,
        IHostManagerProcessEffectValidationScopeControl,
        IHostManagerMemoryCleanupValidationEvidenceControl,
        IRuntimeCpuLeaseService
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly HostManagerVersionedWakeDeadline schedulingWakeDeadline =
        (cpuLeaseStore ?? throw new ArgumentNullException(nameof(cpuLeaseStore))).WakeDeadline;
    private readonly HostManagerDurableTimeSource durableTimeSource = new(timeProvider);
    private NativeSmartCoordinatorWorkspace? nativeWorkspace;
    private NativeSmartCoordinatorWorkspace? pendingNativeWorkspaceClose;
    private HostManagerComputeScoringWorkspace? computeScoringWorkspace;
    private HostManagerMemoryModeWorkspace? memoryModeWorkspace;
    private string? lastSchedulingAuthorityFailure;
    private HostManagerSmartCoordinatorRuntimePlan? appliedNativeRuntimePlan;
    private NativePlacementCoordinatorSession? placementCoordinatorSession;
    private NativePlacementCoordinatorWorkspace? placementCoordinatorWorkspace;
    private PlacementCoordinatorRuntimePlan? appliedPlacementCoordinatorPlan;
    private ulong placementCoordinatorCycleEpoch;
    private long nativeCycleSequence;
    private ulong nativeHostSessionIncarnation;
    private bool authoritativeAppliedFactsRequired = true;
    private bool legacyStatePrepared;
    // 调度循环存活且上一周期没有失败；与模式无关。状态读取时再和当前模式、故障关闭合成，
    // 这样刚切换模式、循环还没跑到下一周期时，状态也不会沿用上一个模式的结论。
    private volatile bool schedulerRunning;
    private int foregroundControlRequestCount;
    private readonly ResourceManagerSelfLocalResourceManager selfLocalResourceManagerOwner =
        selfLocalResourceManager ?? throw new ArgumentNullException(
            nameof(selfLocalResourceManager));
    private readonly IHostPublicResourceSelfManager hostPublicResourceSelfManagerOwner =
        hostPublicResourceSelfManager ?? throw new ArgumentNullException(
            nameof(hostPublicResourceSelfManager));
    private readonly HostPublicResourceNormalReleaseEvidenceTracker
        hostPublicResourceNormalReleaseEvidence = new();
    private readonly HostManagerSchedulingAuthority schedulingAuthorityOwner =
        schedulingAuthority ?? throw new ArgumentNullException(nameof(schedulingAuthority));
    private readonly HostManagerMemoryModePolicyAuthority memoryModePolicyAuthorityOwner =
        memoryModePolicyAuthority ?? throw new ArgumentNullException(
            nameof(memoryModePolicyAuthority));
    private readonly HostManagerProcessEffectValidationScopeAuthority
        processEffectValidationScopeAuthorityOwner =
            processEffectValidationScopeAuthority ?? throw new ArgumentNullException(
                nameof(processEffectValidationScopeAuthority));
    private readonly HostManagerMemoryCleanupValidationEvidenceLedger
        memoryCleanupValidationEvidence = new(timeProvider);

    public async Task<HostManagerSmartCoordinatorStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        RequireOperationAdmission();
        cancellationToken.ThrowIfCancellationRequested();
        // Stores own their synchronization. Native data is the last published segment,
        // so observation never borrows a workspace or waits for the round gate.
        var state = await stateStore.LoadAsync(cancellationToken);
        RequireOperationAdmission();
        var observation = Volatile.Read(ref publishedObservation);
        return await CreateStatusAsync(
            runtimePlanProvider.Current.OptimizationMode.Mode,
            ProjectNativeState(state, observation),
            cancellationToken,
            observation);
    }

    public async Task<HostManagerRollbackStateDocument> GetStateAsync(CancellationToken cancellationToken)
    {
        RequireOperationAdmission();
        cancellationToken.ThrowIfCancellationRequested();
        var state = await stateStore.LoadAsync(cancellationToken);
        RequireOperationAdmission();
        return ProjectNativeState(state);
    }

    public async Task<HostManagerSmartCoordinatorStatus> SetModeAsync(
        string mode,
        CancellationToken cancellationToken)
    {
        using var _ = EnterForegroundControlRequest();
        var normalizedMode = HostManagerOptimizationModes.Normalize(mode);
        await gate.WaitAsync(cancellationToken);
        try
        {
            RequireOperationAdmission();
            if (normalizedMode == AppOptimizationModes.Normal)
                await runningGpuPlacementActions.RecoverExternalControlAsync(cancellationToken);
            var loaded = await settingsStore.LoadAsync(cancellationToken);
            var current = loaded.Settings;
            var next = current with
            {
                Performance = current.Performance with { OptimizationMode = normalizedMode }
            };
            var update = await runtimeSpecializationCoordinator.ApplyAppSettingsAsync(
                next,
                "smart-optimization-mode-saved",
                cancellationToken);
            if (!string.Equals(
                update.RuntimeApplicationDisposition,
                "committedAndApplied",
                StringComparison.Ordinal)
                && !string.Equals(
                    update.RuntimeApplicationDisposition,
                    "committedWithDeliveryFailures",
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"The optimization mode was not applied: {update.RuntimeFailureCode ?? update.RuntimeApplicationDisposition}.");
            }
            await RunForegroundNativeCycleAsync("mode-saved", cancellationToken);
            if (string.Equals(
                normalizedMode,
                AppOptimizationModes.Normal,
                StringComparison.Ordinal))
            {
                await RequireNoAppliedOwnershipAsync(cancellationToken);
            }
            schedulingWakeDeadline.PublishForeground(TimeSpan.Zero);
            var state = ProjectNativeState(await LoadRollbackStateAsync(cancellationToken));
            return await CreateStatusAsync(
                normalizedMode,
                state,
                cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<HostManagerSmartCoordinatorStatus> RunOnceAsync(CancellationToken cancellationToken)
    {
        var outerLoopContext = BeginOuterLoopAttempt(
            HostManagerSmartCoordinatorOuterLoopTrigger.Manual);
        ObserveOuterLoop(
            outerLoopContext,
            HostManagerSmartCoordinatorOuterLoopPhase.ManualRequest);
        using var _ = EnterForegroundControlRequest();
        ObserveOuterLoop(
            outerLoopContext,
            HostManagerSmartCoordinatorOuterLoopPhase.ForegroundAdmissionCompleted);
        ObserveOuterLoop(
            outerLoopContext,
            HostManagerSmartCoordinatorOuterLoopPhase.GateWaitStarted);
        await gate.WaitAsync(cancellationToken);
        AssignOuterLoopDispatchSequence(outerLoopContext);
        ObserveOuterLoop(
            outerLoopContext,
            HostManagerSmartCoordinatorOuterLoopPhase.GateAcquired);
        HostManagerSmartCoordinatorStatus status;
        try
        {
            RequireOperationAdmission();
            ObserveOuterLoop(
                outerLoopContext,
                HostManagerSmartCoordinatorOuterLoopPhase.DispatchStarted);
            await RunForegroundNativeCycleAsync(
                "manual",
                outerLoopContext,
                cancellationToken);
            var state = ProjectNativeState(await LoadRollbackStateAsync(cancellationToken));
            status = await CreateStatusAsync(
                runtimePlanProvider.Current.OptimizationMode.Mode,
                state,
                cancellationToken);
        }
        finally
        {
            ObserveOuterLoop(
                outerLoopContext,
                HostManagerSmartCoordinatorOuterLoopPhase.GateReleaseStarted);
            gate.Release();
            ObserveOuterLoop(
                outerLoopContext,
                HostManagerSmartCoordinatorOuterLoopPhase.GateReleased);
        }
        ObserveOuterLoop(
            outerLoopContext,
            HostManagerSmartCoordinatorOuterLoopPhase.ManualApiReturned);
        return status;
    }

    public async Task<HostManagerSmartCoordinatorStatus> RestoreNormalModeAsync(CancellationToken cancellationToken)
    {
        using var _ = EnterForegroundControlRequest();
        await gate.WaitAsync(cancellationToken);
        try
        {
            RequireOperationAdmission();
            // This explicit user restore also asks surviving external owners to finish cleanup, never to retry migration.
            await runningGpuPlacementActions.RecoverExternalControlAsync(cancellationToken);
            var loaded = await settingsStore.LoadAsync(cancellationToken);
            var current = loaded.Settings;
            if (!string.Equals(
                current.Performance.OptimizationMode,
                AppOptimizationModes.Normal,
                StringComparison.Ordinal))
            {
                var update = await runtimeSpecializationCoordinator.ApplyAppSettingsAsync(
                    current with
                    {
                        Performance = current.Performance with
                        {
                            OptimizationMode = AppOptimizationModes.Normal
                        }
                    },
                    "host-manager-restore-normal",
                    cancellationToken);
                if (!string.Equals(
                    update.RuntimeApplicationDisposition,
                    "committedAndApplied",
                    StringComparison.Ordinal)
                    && !string.Equals(
                        update.RuntimeApplicationDisposition,
                        "committedWithDeliveryFailures",
                        StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Normal mode was not restored: {update.RuntimeFailureCode ?? update.RuntimeApplicationDisposition}.");
                }
            }
            await RunForegroundNativeCycleAsync("restore-normal", cancellationToken);
            await RequireNoAppliedOwnershipAsync(cancellationToken);
            schedulingWakeDeadline.PublishForeground(TimeSpan.Zero);
            var restored = await LoadRollbackStateAsync(cancellationToken);

            return await CreateStatusAsync(
                AppOptimizationModes.Normal,
                ProjectNativeState(restored),
                cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        smartSubscriptions = new(cpuCoreResidencyReader, schedulingProcessFactSource, metricSnapshotSource, timeProvider);
        schedulerRunning = true;
        try
        {
            await Task.Yield();
            stoppingToken.ThrowIfCancellationRequested();
            using var rendererObservation = runningGpuPlacementActions.AcquireRendererObservation();
            await gate.WaitAsync(stoppingToken);
            try { await SynchronizeSmartSubscriptionsAsync(stoppingToken); }
            finally { gate.Release(); }
            if (smartSubscriptions.HasOutput)
                await smartSubscriptions.WaitAsync(schedulingWakeDeadline, stoppingToken);
            var outerLoopContext = BeginOuterLoopAttempt(
                HostManagerSmartCoordinatorOuterLoopTrigger.ScheduledBootstrap);
            while (!stoppingToken.IsCancellationRequested)
            {
                ObserveOuterLoop(
                    outerLoopContext,
                    HostManagerSmartCoordinatorOuterLoopPhase.GateWaitStarted);
                await gate.WaitAsync(stoppingToken);
                AssignOuterLoopDispatchSequence(outerLoopContext);
                ObserveOuterLoop(
                    outerLoopContext,
                    HostManagerSmartCoordinatorOuterLoopPhase.GateAcquired);
                try
                {
                    schedulerRunning = true;
                    await SynchronizeSmartSubscriptionsAsync(stoppingToken);
                    ObserveOuterLoop(
                        outerLoopContext,
                        HostManagerSmartCoordinatorOuterLoopPhase.DispatchStarted);
                    await RunScheduledNativeCycleAsync(
                        outerLoopContext,
                        stoppingToken);
                }
                catch (Exception error) when (!stoppingToken.IsCancellationRequested)
                {
                    schedulerRunning = false;
                    logger.LogError(error, "调度周期失败；保留已有事务记录，下一周期重新核对恢复状态。");
                    schedulingWakeDeadline.PublishScheduled(
                        runtimePlanProvider.Current.HostManager.RequirePublished().SchedulerSamplingInterval);
                }
                finally
                {
                    ObserveOuterLoop(
                        outerLoopContext,
                        HostManagerSmartCoordinatorOuterLoopPhase.GateReleaseStarted);
                    gate.Release();
                    ObserveOuterLoop(
                        outerLoopContext,
                        HostManagerSmartCoordinatorOuterLoopPhase.GateReleased);
                }

                var waitEnteredAtQpcTicks = Stopwatch.GetTimestamp();
                var waitEnteredAtProviderTimestamp =
                    schedulingWakeDeadline.GetTimestamp();
                var consumedWake = await smartSubscriptions.WaitAsync(schedulingWakeDeadline, stoppingToken);
                outerLoopContext = BeginOuterLoopAttempt(
                    HostManagerSmartCoordinatorOuterLoopTrigger.Scheduled);
                if (outerLoopContext is null)
                {
                    continue;
                }
                outerLoopContext.ConsumedWake = consumedWake;
                ObserveOuterLoop(
                    outerLoopContext,
                    HostManagerSmartCoordinatorOuterLoopPhase.WaitEntered,
                    qpcTicks: waitEnteredAtQpcTicks,
                    providerTimestamp: waitEnteredAtProviderTimestamp,
                    providerTimestampFrequency: schedulingWakeDeadline.TimestampFrequency);
                ObserveOuterLoop(
                    outerLoopContext,
                    HostManagerSmartCoordinatorOuterLoopPhase.WakeReturned,
                    providerTimestamp: schedulingWakeDeadline.GetTimestamp(),
                    providerTimestampFrequency: schedulingWakeDeadline.TimestampFrequency);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            schedulerRunning = false;
            await smartSubscriptions.DisposeAsync();
            smartSubscriptions = null;
        }
    }

    private async Task<bool> PrepareLegacyStateAsync(
        HostManagerCycleEffectAdmission effectAdmission,
        CancellationToken cancellationToken,
        bool gpuControlAvailable = true)
    {
        if (legacyStatePrepared)
        {
            return true;
        }

        var state = await LoadRollbackStateAsync(cancellationToken);
        bool HasAvailableLegacyEffects() => GpuActionFacts.AvailablePlacementEffects(state.AppliedPlacements)
            .Any(item => gpuControlAvailable || !string.Equals(item.ResourceKind,
                OptimizationResourceKinds.Gpu, StringComparison.OrdinalIgnoreCase));
        if (HasAvailableLegacyEffects())
        {
            if (!effectAdmission.TryAcquire(
                    HostManagerCycleEffectKind.LegacyPlacementRestore,
                    out var permit))
            {
                return false;
            }
            permit.Require(HostManagerCycleEffectKind.LegacyPlacementRestore);
            EnsurePlacementCoordinatorWorkspace();
            state = await RestorePlacementStateCoreAsync(permit, cancellationToken, gpuControlAvailable);
        }

        if (HasAvailableLegacyEffects())
        {
            return false;
        }

        // Unavailable GPU effects remain owned and are reconciled by the automatic placement cycle.
        legacyStatePrepared = true;
        authoritativeAppliedFactsRequired = true;
        return true;
    }

    private ForegroundControlRequestLease EnterForegroundControlRequest()
    {
        lock (lifecycleSync)
        {
            RequireOperationAdmission();
            if (foregroundControlRequestCount == 0)
                foregroundControlRequestsDrained = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Interlocked.Increment(ref foregroundControlRequestCount);
            return new ForegroundControlRequestLease(this);
        }
    }

    private void LeaveForegroundControlRequest()
    {
        lock (lifecycleSync)
        {
            if (Interlocked.Decrement(ref foregroundControlRequestCount) == 0)
            {
                foregroundControlRequestsDrained!.TrySetResult();
                foregroundControlRequestsDrained = null;
            }
        }
    }

    private sealed class ForegroundControlRequestLease(HostManagerSmartCoordinator coordinator) : IDisposable
    {
        private int disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
            {
                coordinator.LeaveForegroundControlRequest();
            }
        }
    }
}
