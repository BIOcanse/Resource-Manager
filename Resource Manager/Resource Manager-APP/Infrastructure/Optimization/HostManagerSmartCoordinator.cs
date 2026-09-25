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
    HostManagerProcessEffectValidationScopeAuthority processEffectValidationScopeAuthority)
    : BackgroundService,
        IHostManagerSmartCoordinator,
        IHostManagerProcessEffectValidationScopeControl,
        IHostManagerMemoryCleanupValidationEvidenceControl
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly HostManagerVersionedWakeDeadline schedulingWakeDeadline =
        new(timeProvider);
    private readonly HostManagerDurableTimeSource durableTimeSource = new(timeProvider);
    private NativeSmartCoordinatorWorkspace? nativeWorkspace;
    private HostManagerComputeScoringWorkspace? computeScoringWorkspace;
    private HostManagerMemoryModeWorkspace? memoryModeWorkspace;
    private string? lastSchedulingAuthorityFailure;
    private HostManagerSmartCoordinatorRuntimePlan? appliedNativeRuntimePlan;
    private NativePlacementCoordinatorSession? placementCoordinatorSession;
    private NativePlacementCoordinatorWorkspace? placementCoordinatorWorkspace;
    private PlacementCoordinatorRuntimePlan? appliedPlacementCoordinatorPlan;
    private ulong placementCoordinatorCycleEpoch;
    private long nativeCycleSequence;
    private ulong computeScoringSequence;
    private ulong nativeHostSessionIncarnation;
    private bool authoritativeAppliedFactsRequired = true;
    private bool legacyStatePrepared;
    private bool schedulerRunning;
    private int foregroundControlRequestCount;
    private readonly ResourceManagerSelfLocalResourceManager selfLocalResourceManagerOwner =
        selfLocalResourceManager ?? throw new ArgumentNullException(
            nameof(selfLocalResourceManager));
    private readonly IHostPublicResourceSelfManager hostPublicResourceSelfManagerOwner =
        hostPublicResourceSelfManager ?? throw new ArgumentNullException(
            nameof(hostPublicResourceSelfManager));
    private readonly HostPublicResourceNormalReleaseEvidenceTracker
        hostPublicResourceNormalReleaseEvidence = new();
    private readonly HostPublicResourceCapacitySampleFreshnessTracker
        hostPublicResourceCapacitySampleFreshness = new();
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
        await gate.WaitAsync(cancellationToken);
        try
        {
            RequireOperationAdmission();
            var state = await LoadRollbackStateAsync(cancellationToken);
            return CreateStatus(
                runtimePlanProvider.Current.OptimizationMode.Mode,
                ProjectNativeState(state),
                schedulerRunning,
                nativeWorkspace?.Snapshot.PendingCount ?? 0);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<HostManagerRollbackStateDocument> GetStateAsync(CancellationToken cancellationToken)
    {
        RequireOperationAdmission();
        await gate.WaitAsync(cancellationToken);
        try
        {
            RequireOperationAdmission();
            var state = await LoadRollbackStateAsync(cancellationToken);
            return ProjectNativeState(state);
        }
        finally
        {
            gate.Release();
        }
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
            return CreateStatus(
                normalizedMode,
                state,
                schedulerRunning,
                nativeWorkspace?.Snapshot.PendingCount ?? 0);
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
            status = CreateStatus(
                runtimePlanProvider.Current.OptimizationMode.Mode,
                state,
                schedulerRunning,
                nativeWorkspace?.Snapshot.PendingCount ?? 0);
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

            return CreateStatus(
                AppOptimizationModes.Normal,
                ProjectNativeState(restored),
                schedulerRunning,
                nativeWorkspace?.Snapshot.PendingCount ?? 0);
        }
        finally
        {
            gate.Release();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var samplingInterval = runtimePlanProvider.Current.HostManager.RequirePublished().SchedulerSamplingInterval;
        IDisposable? cpuCoreSubscription = null;
        IDisposable? hardwareSubscription = null;
        IDisposable? processComputeSubscription = null;
        IDisposable? processMemoryFinalAdmissionSubscription = null;
        string? subscribedHardwareRequestKey = null;
        var subscribedProcessMetricMask = SchedulingProcessMetricMask.None;
        schedulerRunning = runtimePlanProvider.Current.OptimizationMode.SchedulingEnabled;
        try
        {
            await Task.Yield();
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
                    var mode = runtimePlanProvider.Current.OptimizationMode;
                    schedulerRunning = mode.SchedulingEnabled;
                    var publicResourcesAvailable = HasAvailablePublicResourceLifecycle();
                    var cpuRequired = mode.MemorySchedulingEnabled || mode.CpuSchedulingEnabled;
                    if (!cpuRequired)
                    {
                        cpuCoreSubscription?.Dispose();
                        cpuCoreSubscription = null;
                    }
                    else
                    {
                        cpuCoreSubscription ??= cpuCoreResidencyReader.AcquireSubscription(
                            "host-manager-smart-coordinator:cpu-cores", samplingInterval);
                    }

                    var processMask = SchedulingProcessMetricMask.RuntimeState;
                    if (cpuRequired)
                    {
                        processMask |= SchedulingProcessMetricMask.CpuUsage;
                    }
                    if (mode.GpuSchedulingEnabled)
                    {
                        processMask |= SchedulingProcessMetricMask.GpuUsage
                            | SchedulingProcessMetricMask.GpuDedicatedMemory;
                    }
                    if (processComputeSubscription is not null
                        && (!mode.SchedulingEnabled || subscribedProcessMetricMask != processMask))
                    {
                        processComputeSubscription?.Dispose();
                        processComputeSubscription = null;
                    }
                    if (mode.SchedulingEnabled && processComputeSubscription is null)
                    {
                        processComputeSubscription = schedulingProcessFactSource.AcquireSubscription(
                            "host-manager-smart-coordinator:compute",
                            processMask,
                            samplingInterval);
                        subscribedProcessMetricMask = processMask;
                    }
                    if (!mode.MemorySchedulingEnabled)
                    {
                        processMemoryFinalAdmissionSubscription?.Dispose();
                        processMemoryFinalAdmissionSubscription = null;
                    }
                    else
                    {
                        processMemoryFinalAdmissionSubscription ??=
                            schedulingProcessFactSource.AcquireSubscription(
                                "host-manager-smart-coordinator:memory-final-admission",
                                SchedulingProcessMetricMask.MemoryUsage
                                    | SchedulingProcessMetricMask.RuntimeState,
                                samplingInterval);
                    }

                    var hardwareRequired = mode.SchedulingEnabled || publicResourcesAvailable;
                    var hardwareRequest = mode.SchedulingEnabled
                        ? CreateSmartSchedulingMetricRequest(mode, publicResourcesAvailable)
                        : CreatePublicResourceMetricRequest();
                    if (hardwareSubscription is not null
                        && (!hardwareRequired
                            || subscribedHardwareRequestKey != hardwareRequest.CacheKey))
                    {
                        hardwareSubscription.Dispose();
                        hardwareSubscription = null;
                    }
                    if (hardwareRequired && hardwareSubscription is null)
                    {
                        hardwareSubscription = metricSnapshotSource.AcquireSubscription(
                            "host-manager-smart-coordinator",
                            hardwareRequest,
                            samplingInterval);
                        subscribedHardwareRequestKey = hardwareRequest.CacheKey;
                    }
                    ObserveOuterLoop(
                        outerLoopContext,
                        HostManagerSmartCoordinatorOuterLoopPhase.DispatchStarted);
                    var published = await RunScheduledNativeCycleAsync(
                        outerLoopContext,
                        stoppingToken);
                    var currentMode = runtimePlanProvider.Current.OptimizationMode;
                    schedulerRunning = currentMode.SchedulingEnabled;
                    if (!currentMode.SchedulingEnabled
                        && await CanParkNormalSchedulingAsync(stoppingToken))
                    {
                        schedulingWakeDeadline.ClearScheduled(published.Version);
                    }
                    else if (currentMode.SchedulingEnabled && !mode.SchedulingEnabled)
                    {
                        schedulingWakeDeadline.PublishForeground(TimeSpan.Zero);
                    }
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
                var consumedWake = await schedulingWakeDeadline.WaitAsync(stoppingToken);
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
            processMemoryFinalAdmissionSubscription?.Dispose();
            processComputeSubscription?.Dispose();
            hardwareSubscription?.Dispose();
            cpuCoreSubscription?.Dispose();
        }
    }

    private async Task<bool> PrepareLegacyStateAsync(
        HostManagerCycleEffectAdmission effectAdmission,
        CancellationToken cancellationToken)
    {
        if (legacyStatePrepared)
        {
            return true;
        }

        var state = await LoadRollbackStateAsync(cancellationToken);
        if (GpuActionFacts.AvailablePlacementEffects(state.AppliedPlacements).Count != 0)
        {
            if (!effectAdmission.TryAcquire(
                    HostManagerCycleEffectKind.LegacyPlacementRestore,
                    out var permit))
            {
                return false;
            }
            permit.Require(HostManagerCycleEffectKind.LegacyPlacementRestore);
            EnsurePlacementCoordinatorWorkspace();
            state = await RestorePlacementStateCoreAsync(permit, cancellationToken);
        }

        if (GpuActionFacts.AvailablePlacementEffects(state.AppliedPlacements).Count != 0)
        {
            return false;
        }

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
