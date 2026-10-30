using ResourceManager.App.Infrastructure.Adaptation;
using System.Collections.Immutable;
using ResourceManager.App.Application.Metrics;
using ResourceManager.App.Application.Software;
using ResourceManager.Adapter;
using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Application.ResourceBreakdown;
using ResourceManager.App.Application.SystemHealth;
using ResourceManager.App.Application.SoftwareIssues;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Domain.SystemHealth;
using ResourceManager.App.Domain.Software;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;

namespace ResourceManager.App.Infrastructure.Optimization.Reports;

internal sealed partial class HostManagerReportCoordinatorOwner(
    RuntimePlanProvider planProvider,
    HostManagerReportCoordinatorRuntime deployment,
    HostManagerRuntimeIdentity runtimeIdentity,
    INativeReportCoordinatorPersistenceStore persistenceStore,
    IMetricSnapshotObservationSource metricSource,
    IResourceBreakdownObservationSource resourceSource,
    ISystemInterruptSnapshotSource interruptSource,
    IOptimizationProtectionService protectionService,
    ILogger<HostManagerReportCoordinatorOwner> logger,
    Func<ulong, IComputeScratch> createScratch,
    ISchedulingProcessFactObservationSource processSource,
    ISoftwareRegistryView softwareRegistry,
    Func<CompiledHostManagerReportCoordinatorPlan, ulong, ulong,
        Func<ulong, IComputeScratch>, NativeReportCoordinatorWorkspace>? createWorkspace = null)
    : BackgroundService,
        IHostManagerReportService,
        IOptimizationSoftwareIssueSignalSource,
        IAsyncDisposable
{
    private const int SampleIntervalSeconds = 10;
    private static readonly TimeSpan SampleInterval =
        TimeSpan.FromSeconds(SampleIntervalSeconds);

    private readonly SemaphoreSlim executionGate = new(1, 1);
    private readonly HostManagerStrayProcessReportFactProjector strayProjector = new();
    private readonly SemaphoreSlim planSignal = new(0, 1);
    private NativeReportCoordinatorWorkspace? workspace;
    private NativeReportCoordinatorWorkspace? pendingWorkspaceClose;
    private readonly WorkspaceHolder workspaceHolds = new();
    private WorkspaceHold? runHold;
    private int retiring;
    private bool disposed;
    private CompiledHostManagerReportCoordinatorPlan? appliedPlan;
    private CoordinatorState? coordinatorState;
    private HostManagerReportReadModel readModel = HostManagerReportReadModel.Empty;
    private readonly Dictionary<ulong, HostManagerReportTargetDescriptor>
        targetCatalog = [];
    private IDisposable? metricSubscription;
    private IDisposable? resourceSubscription;
    private IDisposable? interruptSubscription;
    private IDisposable? processSubscription;
    private ulong incarnation;
    private ulong operationEpoch;
    private ulong planEpoch;
    private ulong feedbackEpoch;
    private ulong importGeneration;
    private ulong lastCommandMonotonicMilliseconds;
    private long lastCommandUtcMilliseconds;
    private volatile bool subscribed;

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        var hold = AcquireWorkspaceHold(cancellationToken);
        var starting = false;
        var retainHold = false;
        try
        {
            await executionGate.WaitAsync(cancellationToken);
            try
            {
                ThrowIfRetiring();
                if (runHold is not null) return;
                starting = true;
                subscribed = true;
                using var publication = planProvider.AcquirePublicationLease();
                await ApplyPlanCoreAsync(
                    publication.Plan.HostManager.RequirePublished(),
                    cancellationToken);
                ThrowIfRetiring();
                UpdateObservationSubscriptions(RequireAppliedPlan());
                runHold = hold;
                await base.StartAsync(cancellationToken);
                retainHold = true;
            }
            finally
            {
                executionGate.Release();
            }

        }
        catch (Exception startupFailure)
        {
            var failures = new List<Exception> { startupFailure };
            if (starting)
            {
                ReleaseObservationSubscriptions(failures);
                EndObservationRun();
                Interlocked.CompareExchange(ref runHold, null, hold);
            }
            try { await hold.DisposeAsync(); }
            catch (Exception failure) { failures.Add(failure); }
            ThrowFailures(failures);
        }
        finally
        {
            if (!retainHold) await hold.DisposeAsync();
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        EndObservationRun();
        var failures = new List<Exception>();
        try
        {
            await base.StopAsync(cancellationToken);
        }
        catch (Exception stopFailure)
        {
            failures.Add(stopFailure);
        }
        // .NET 10 may cancel the scheduled BackgroundService task before it
        // invokes ExecuteAsync, so that method's finally never owns cleanup.
        // A canceled Stop with an unfinished task must leave its run hold alone.
        if (ExecuteTask is { IsCompleted: true })
        {
            WorkspaceHold? remainingHold = null;
            await executionGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                if (ExecuteTask is { IsCompleted: true })
                    remainingHold = Interlocked.Exchange(ref runHold, null);
            }
            finally { executionGate.Release(); }
            if (remainingHold is not null)
            {
                try { await remainingHold.DisposeAsync().ConfigureAwait(false); }
                catch (Exception closeFailure) { failures.Add(closeFailure); }
            }
        }
        ReleaseObservationSubscriptions(failures);
        if (failures.Count > 0)
        {
            ThrowFailures(failures);
        }
    }

    public async Task<OptimizationReportOverview> GetReportsAsync(
        CancellationToken cancellationToken)
    {
        var protectedTargets =
            await protectionService.GetProtectedTargetsAsync(cancellationToken);
        var current = Volatile.Read(ref readModel);
        return new OptimizationReportOverview(
            current.CapturedAt,
            current.Reports,
            current.TrustedTargets,
            protectedTargets,
            current.Status with
            {
                ProtectedCount = protectedTargets.Count
            });
    }

    public async Task<OptimizationReportOverview> RefreshReportsAsync(
        CancellationToken cancellationToken)
    {
        await using var hold = AcquireWorkspaceHold(cancellationToken);
        await EvaluateLatestAsync(cancellationToken);
        return await GetReportsAsync(cancellationToken);
    }

    public async Task<int> ClearHistoryAsync(CancellationToken cancellationToken)
    {
        await using var hold = AcquireWorkspaceHold(cancellationToken);
        await executionGate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfRetiring();
            using var publication = planProvider.AcquirePublicationLease();
            await ApplyPlanCoreAsync(publication.Plan.HostManager.RequirePublished(), cancellationToken);
            var activeWorkspace = RequireWorkspace();
            var plan = RequireAppliedPlan();
            var state = RequireCoordinatorState();
            var stamp = NextCommandStamp(DateTimeOffset.UtcNow);
            var input = new NativeReportClearHistoryInput
            {
                AbiVersion = NativeReportCoordinatorAbi.Version,
                StructSize = SizeOf<NativeReportClearHistoryInput>(),
                ConfigurationGeneration = plan.ConfigurationGeneration,
                OperationEpoch = stamp.OperationEpoch,
                CommandMonotonicMilliseconds = stamp.MonotonicMilliseconds,
                CommandUtcMilliseconds = stamp.UtcMilliseconds,
                ValidMask = (ulong)NativeReportClearHistoryValidity.Required
            };
            RequireStatus(activeWorkspace.Session.ClearHistory(in input, out var removedCount), "clear history command");
            await SettleAndPublishAsync(activeWorkspace, plan, state, cancellationToken);
            return checked((int)removedCount);
        }
        finally
        {
            executionGate.Release();
        }
    }

    public Task<OptimizationReportItem?> GetReportAsync(
        string reportId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(reportId);
        var current = Volatile.Read(ref readModel);
        current.ReportById.TryGetValue(reportId, out var report);
        return Task.FromResult(report);
    }

    public Task<TrustedOptimizationTarget?> DismissReportAsync(
        string reportId,
        CancellationToken cancellationToken)
        => TrustReportAsync(reportId, cancellationToken);

    public async Task<TrustedOptimizationTarget?> TrustReportAsync(
        string reportId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reportId);
        await using var hold = AcquireWorkspaceHold(cancellationToken);
        await executionGate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfRetiring();
            using var publication = planProvider.AcquirePublicationLease();
            await ApplyPlanCoreAsync(publication.Plan.HostManager.RequirePublished(), cancellationToken);
            var current = Volatile.Read(ref readModel);
            if (!current.NativeReportById.TryGetValue(reportId, out var report))
            {
                return null;
            }
            var activeWorkspace = RequireWorkspace();
            var plan = RequireAppliedPlan();
            var state = RequireCoordinatorState();
            var stamp = NextCommandStamp(DateTimeOffset.UtcNow);
            var input = new NativeReportTrustCommandInput
            {
                AbiVersion = NativeReportCoordinatorAbi.Version,
                StructSize = SizeOf<NativeReportTrustCommandInput>(),
                ConfigurationGeneration = plan.ConfigurationGeneration,
                OperationEpoch = stamp.OperationEpoch,
                CommandMonotonicMilliseconds = stamp.MonotonicMilliseconds,
                CommandUtcMilliseconds = stamp.UtcMilliseconds,
                TargetHandle = report.TargetHandle,
                FamilyHandle = report.FamilyHandle,
                PayloadHandle = report.PayloadHandle,
                ValidMask = (ulong)NativeReportTrustCommandValidity.Required,
                CommandKind = (uint)NativeReportTrustCommandKind.Add
            };
            RequireStatus(
                activeWorkspace.Session.CommandTrust(in input),
                "trust command");
            await SettleAndPublishAsync(
                activeWorkspace,
                plan,
                state,
                cancellationToken);
            var updated = Volatile.Read(ref readModel);
            return updated.TrustedTargets.FirstOrDefault(item =>
                item.Id.Equals(
                    HostManagerReportPresentation.TrustId(
                        report.TargetHandle,
                        report.FamilyHandle),
                    StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            executionGate.Release();
        }
    }

    public Task<IReadOnlyList<TrustedOptimizationTarget>> GetTrustedTargetsAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<TrustedOptimizationTarget>>(
            Volatile.Read(ref readModel).TrustedTargets);
    }

    public Task<IReadOnlyList<TrustedOptimizationTarget>> RefreshTrustedTargetsAsync(
        CancellationToken cancellationToken)
        => GetTrustedTargetsAsync(cancellationToken);

    public async Task<bool> RemoveTrustedTargetAsync(
        string trustId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(trustId);
        await using var hold = AcquireWorkspaceHold(cancellationToken);
        await executionGate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfRetiring();
            using var publication = planProvider.AcquirePublicationLease();
            await ApplyPlanCoreAsync(publication.Plan.HostManager.RequirePublished(), cancellationToken);
            var current = Volatile.Read(ref readModel);
            if (!current.NativeTrustById.TryGetValue(trustId, out var trust))
            {
                return false;
            }
            var activeWorkspace = RequireWorkspace();
            var plan = RequireAppliedPlan();
            var state = RequireCoordinatorState();
            var stamp = NextCommandStamp(DateTimeOffset.UtcNow);
            var input = new NativeReportTrustCommandInput
            {
                AbiVersion = NativeReportCoordinatorAbi.Version,
                StructSize = SizeOf<NativeReportTrustCommandInput>(),
                ConfigurationGeneration = plan.ConfigurationGeneration,
                OperationEpoch = stamp.OperationEpoch,
                CommandMonotonicMilliseconds = stamp.MonotonicMilliseconds,
                CommandUtcMilliseconds = stamp.UtcMilliseconds,
                TargetHandle = trust.TargetHandle,
                FamilyHandle = trust.FamilyHandle,
                PayloadHandle = trust.PayloadHandle,
                ValidMask = (ulong)NativeReportTrustCommandValidity.Required,
                CommandKind = (uint)NativeReportTrustCommandKind.Remove
            };
            RequireStatus(
                activeWorkspace.Session.CommandTrust(in input),
                "trust removal");
            await SettleAndPublishAsync(
                activeWorkspace,
                plan,
                state,
                cancellationToken);
            return true;
        }
        finally
        {
            executionGate.Release();
        }
    }

    public OptimizationRecorderStatus GetStatus()
    {
        var status = Volatile.Read(ref readModel).Status;
        return status with
        {
            ProtectedCount = protectionService.ProtectedTargetCount
        };
    }

    public OptimizationSoftwareIssueSnapshot ReadSoftwareIssueSnapshot()
    {
        var current = Volatile.Read(ref readModel);
        return new OptimizationSoftwareIssueSnapshot(
            current.CapturedAt,
            current.SoftwareIssueSignals);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await EvaluateLatestAsync(stoppingToken);
                }
                catch (ObjectDisposedException) when (Volatile.Read(ref retiring) != 0)
                {
                    break;
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (ReportSourceObservationException)
                {
                    // ObserveSource owns source failure/recovery transitions.
                }
                catch (Exception exception)
                {
                    logger.LogError(
                        exception,
                        "Host Manager report coordinator cycle failed; the last committed snapshot remains published.");
                }

                await planSignal.WaitAsync(SampleInterval, stoppingToken);
            }
        }
        finally
        {
            var hold = Interlocked.Exchange(ref runHold, null);
            if (hold is not null) await hold.DisposeAsync();
        }
    }

    private void OnPlanPublished(CompiledRuntimePlan _)
    {
        // Publication delivery is finite and joined, including asynchronous
        // persistence. It cannot inherit a publisher's synchronization context.
        try { Task.Run(ApplyPublishedPlanAsync).GetAwaiter().GetResult(); }
        finally { SignalPlanChange(); }
    }

    private async Task ApplyPublishedPlanAsync()
    {
        await executionGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref retiring) != 0 || !workspaceHolds.HasReferences) return;
            using var publication = planProvider.AcquirePublicationLease();
            await ApplyPlanCoreAsync(publication.Plan.HostManager.RequirePublished(), CancellationToken.None);
        }
        catch (Exception) when (Volatile.Read(ref retiring) == 0)
        {
            RecordEvaluationFailure();
            throw;
        }
        finally { executionGate.Release(); }
    }

    private void EndObservationRun()
    {
        subscribed = false;
    }

    private void SignalPlanChange()
    {
        try
        {
            planSignal.Release();
        }
        catch (SemaphoreFullException)
        {
        }
    }

    private async Task EvaluateLatestAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await executionGate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfRetiring();
            using var publication = planProvider.AcquirePublicationLease();
            await ApplyPlanCoreAsync(
                publication.Plan.HostManager.RequirePublished(),
                cancellationToken);
            var activeWorkspace = RequireWorkspace();
            var plan = RequireAppliedPlan();
            var state = RequireCoordinatorState();
            var inputs = GetObservationInputs(plan);
            if (subscribed) UpdateObservationSubscriptions(plan);
            var hardware = inputs.NeedsMetrics ? metricSource.ReadLatest(inputs.Metrics) : null;
            var resources = inputs.NeedsResources ? resourceSource.ReadLatest(inputs.Resources) : EmptyResources;
            var interrupts = inputs.NeedsInterrupts ? interruptSource.Read() : HostManagerReportReadModel.Empty.InterruptSnapshot;
            var processFacts = inputs.ProcessMetrics == SchedulingProcessMetricMask.None ? null
                : processSource.ReadLatest(new SchedulingProcessFactRequest(
                    inputs.ProcessMetrics, hardware?.GpuInventory ?? new SchedulingGpuInventorySnapshot(
                        SamplingObservationStatus.NotRequested, 0, 0, 0, 0, 0, 0, [])));
            var backgroundIds = BackgroundSoftwareIds(processFacts);
            var protectedIds = OptimizationProtectionTable.SoftwareProtectedFromReports(
                await protectionService.GetProtectedTargetsAsync(cancellationToken));
            var batch = HostManagerReportFactProjector.Project(
                plan,
                hardware,
                interrupts,
                resources, backgroundIds, protectedIds, strayProjector, processFacts);
            foreach (var target in batch.Targets)
            {
                targetCatalog[target.Key] = target.Value;
            }
            ReportSourceObservationException? sourceFailure = null;
            foreach (var source in batch.Sources)
            {
                if (state.ShouldObserveSource(source))
                {
                    try { ObserveSource(activeWorkspace, plan, state, source); }
                    catch (ReportSourceObservationException failure) { sourceFailure ??= failure; }
                }
            }
            if (sourceFailure is not null) RecordEvaluationFailure();
            // Capacity failures must not block retention maintenance or its
            // persisted deletions, otherwise a smaller new population cannot
            // recover after the old observations expire.
            await SettleAndPublishAsync(
                activeWorkspace,
                plan,
                state,
                cancellationToken,
                interrupts,
                evaluationCompleted: sourceFailure is null);
            if (sourceFailure is not null) throw sourceFailure;
        }
        catch (Exception exception) when (exception is not OperationCanceledException && Volatile.Read(ref retiring) == 0)
        {
            RecordEvaluationFailure();
            throw;
        }
        finally
        {
            executionGate.Release();
        }
    }

    private void RecordEvaluationFailure()
    {
        var current = Volatile.Read(ref readModel);
        var unavailableRules = coordinatorState is { } state && appliedPlan is { } plan
            ? UnavailableRules(state, plan) : current.Status.UnavailableRules;
        Volatile.Write(ref readModel, current with
        {
            Status = current.Status with
            {
                LastEvaluationAt = DateTimeOffset.UtcNow,
                EvaluationState = "failed",
                UnavailableRules = unavailableRules,
                AvailableRuleCount = current.Status.ConfiguredRuleCount - unavailableRules.Count
            }
        });
    }

    internal static IReadOnlySet<string>? BackgroundSoftwareIds(SchedulingProcessFactSnapshot? snapshot)
    {
        if (snapshot is null || snapshot.InventoryStatus != SamplingObservationStatus.Current
            || !snapshot.TryGetCurrentDataset(SchedulingProcessMetricMask.RuntimeState, out _))
            return null;
        return snapshot.Processes.GroupBy(item => item.SoftwareId, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.All(process => process.ValidMetricMask.HasFlag(SchedulingProcessMetricMask.RuntimeState)
                && process.RuntimeState is HostManagerRuntimeStates.BackgroundProcess or HostManagerRuntimeStates.TrayOnly
                && !process.ForegroundFocused && !process.HasVisibleWindow))
            .Select(group => group.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private async Task RestoreRegisteredTargetsAsync(CancellationToken cancellationToken)
    {
        foreach (var target in await persistenceStore.LoadTargetsAsync(cancellationToken))
            targetCatalog.TryAdd(target.TargetHandle, target);
        foreach (var software in await softwareRegistry.GetSoftwareAsync(cancellationToken))
        {
            var handle = AdapterResourceKey.FromString($"report:software:{software.Id}");
            targetCatalog.TryAdd(handle, new HostManagerReportTargetDescriptor(handle,
                software.Id, software.Name, software.Kind, SoftwareDisplayKinds.Project(software.Kind), [], []));
        }
    }

    private void ReleaseObservationSubscriptions(ICollection<Exception> failures)
    {
        subscribedInputs = null;
        var processes = Interlocked.Exchange(ref processSubscription, null);
        CaptureDisposalFailure(processes, failures);
        var interrupts = Interlocked.Exchange(ref interruptSubscription, null);
        var resources = Interlocked.Exchange(ref resourceSubscription, null);
        var metrics = Interlocked.Exchange(ref metricSubscription, null);
        CaptureDisposalFailure(interrupts, failures);
        CaptureDisposalFailure(resources, failures);
        CaptureDisposalFailure(metrics, failures);
    }

    private NativeReportCoordinatorWorkspace RequireWorkspace()
        => workspace
            ?? throw new InvalidOperationException(
                "The Host Manager report coordinator workspace is not ready.");

    private CompiledHostManagerReportCoordinatorPlan RequireAppliedPlan()
        => appliedPlan
            ?? throw new InvalidOperationException(
                "The Host Manager report coordinator plan is not applied.");

    private CoordinatorState RequireCoordinatorState()
        => coordinatorState
            ?? throw new InvalidOperationException(
                "The Host Manager report coordinator state is not ready.");
}

internal sealed record HostManagerReportReadModel(
    DateTimeOffset CapturedAt,
    IReadOnlyList<OptimizationReportItem> Reports,
    IReadOnlyList<TrustedOptimizationTarget> TrustedTargets,
    OptimizationRecorderStatus Status,
    IReadOnlyList<OptimizationSoftwareIssueSignal> SoftwareIssueSignals,
    SystemInterruptSnapshot InterruptSnapshot,
    ImmutableDictionary<string, OptimizationReportItem> ReportById,
    ImmutableDictionary<string, NativeReportOutput> NativeReportById,
    ImmutableDictionary<string, NativeReportPersistenceOperation> NativeTrustById)
{
    internal static HostManagerReportReadModel Empty { get; } = new(
        DateTimeOffset.UnixEpoch,
        [],
        [],
        new OptimizationRecorderStatus(
            null,
            null,
            0,
            0,
            0,
            0,
            0,
            10),
        [],
        SystemInterruptSnapshot.Unavailable(
            SystemInterruptProviderStates.NotStarted,
            "System interrupt sampling has not published a snapshot."),
        ImmutableDictionary<string, OptimizationReportItem>.Empty
            .WithComparers(StringComparer.OrdinalIgnoreCase),
        ImmutableDictionary<string, NativeReportOutput>.Empty
            .WithComparers(StringComparer.OrdinalIgnoreCase),
        ImmutableDictionary<string, NativeReportPersistenceOperation>.Empty
            .WithComparers(StringComparer.OrdinalIgnoreCase));
}
