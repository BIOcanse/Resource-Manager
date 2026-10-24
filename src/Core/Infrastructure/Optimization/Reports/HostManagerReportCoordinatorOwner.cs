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
    ISoftwareRegistryView softwareRegistry)
    : BackgroundService,
        IHostManagerReportService,
        IOptimizationSoftwareIssueSignalSource,
        IAsyncDisposable
{
    private const int SampleIntervalSeconds = 10;
    private static readonly MetricSampleRequest ReportMetricRequest =
        MetricSampleRequest.ForIds(
        [
            SamplingDatasetIds.SystemGpuInventory,
            "cpu.temperature",
            "cpu.usage",
            "cpu.frequencyPercent"
        ]);
    private static readonly ResourceBreakdownSampleRequest ReportResourceRequest =
        ResourceBreakdownSampleRequest.ForResourceTable(
            [ResourceBreakdownMetricIds.MemoryUsage, "process_all_command_line_actual"],
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [ResourceBreakdownMetricIds.MemoryUsage] =
                    ResourceBreakdownScaleModes.Capacity
            });
    private static readonly TimeSpan SampleInterval =
        TimeSpan.FromSeconds(SampleIntervalSeconds);

    private readonly SemaphoreSlim executionGate = new(1, 1);
    private readonly HostManagerStrayProcessReportFactProjector strayProjector = new();
    private const SchedulingProcessMetricMask ReportProcessMetrics = SchedulingProcessMetricMask.RuntimeState
        | SchedulingProcessMetricMask.CpuUsage | SchedulingProcessMetricMask.GpuUsage;
    private readonly SemaphoreSlim planSignal = new(0, 1);
    private NativeReportCoordinatorWorkspace? workspace;
    private NativeReportCoordinatorWorkspace? pendingWorkspaceClose;
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
    private bool subscribed;

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        planProvider.Published += OnPlanPublished;
        subscribed = true;
        try
        {
            await executionGate.WaitAsync(cancellationToken);
            try
            {
                ThrowIfRetiring();
                using var publication = planProvider.AcquirePublicationLease();
                await ApplyPlanCoreAsync(
                    publication.Plan.HostManager.RequirePublished(),
                    cancellationToken);
                ThrowIfRetiring();
                AcquireObservationSubscriptions();
                await base.StartAsync(cancellationToken);
            }
            finally
            {
                executionGate.Release();
            }

        }
        catch (Exception startupFailure)
        {
            var failures = new List<Exception> { startupFailure };
            ReleaseObservationSubscriptions(failures);
            UnsubscribeFromPlanProvider();
            ThrowFailures(failures);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        UnsubscribeFromPlanProvider();
        var failures = new List<Exception>();
        try
        {
            await base.StopAsync(cancellationToken);
        }
        catch (Exception stopFailure)
        {
            failures.Add(stopFailure);
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
        await EvaluateLatestAsync(cancellationToken);
        return await GetReportsAsync(cancellationToken);
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
        await executionGate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfRetiring();
            await SettlePendingAsync(cancellationToken);
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
        await executionGate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfRetiring();
            await SettlePendingAsync(cancellationToken);
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
            catch (Exception exception)
            {
                logger.LogError(
                    exception,
                    "Host Manager report coordinator cycle failed; the last committed snapshot remains published.");
            }

            await planSignal.WaitAsync(SampleInterval, stoppingToken);
        }
    }

    private void OnPlanPublished(CompiledRuntimePlan _)
        => SignalPlanChange();

    private void UnsubscribeFromPlanProvider()
    {
        if (!subscribed)
        {
            return;
        }

        planProvider.Published -= OnPlanPublished;
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
            var hardware = metricSource.ReadLatest(ReportMetricRequest);
            var resources = resourceSource.ReadLatest(ReportResourceRequest);
            var interrupts = interruptSource.Read();
            var processFacts = processSource.ReadLatest(new SchedulingProcessFactRequest(
                ReportProcessMetrics, hardware?.GpuInventory ?? new SchedulingGpuInventorySnapshot(
                    SamplingObservationStatus.NotRequested, 0, 0, 0, 0, 0, 0, [])));
            var backgroundIds = BackgroundSoftwareIds(processFacts);
            var protectedIds = (await protectionService.GetProtectedTargetsAsync(cancellationToken))
                .Where(item => item.State == OptimizationProtectionStates.Active && item.SoftwareId is not null)
                .Select(item => item.SoftwareId!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var batch = HostManagerReportFactProjector.Project(
                plan,
                hardware,
                interrupts,
                resources, backgroundIds, protectedIds, strayProjector, processFacts);
            foreach (var target in batch.Targets)
            {
                targetCatalog[target.Key] = target.Value;
            }
            foreach (var source in batch.Sources)
            {
                if (state.ShouldObserveSource(source))
                {
                    ObserveSource(activeWorkspace, plan, state, source);
                }
            }
            await SettleAndPublishAsync(
                activeWorkspace,
                plan,
                state,
                cancellationToken,
                interrupts,
                evaluationCompleted: true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException && Volatile.Read(ref retiring) == 0)
        {
            var current = Volatile.Read(ref readModel);
            Volatile.Write(ref readModel, current with
            {
                Status = current.Status with
                {
                    LastEvaluationAt = DateTimeOffset.UtcNow,
                    EvaluationState = "failed",
                    EvaluationFailureReason = $"{exception.GetType().Name}: {exception.Message}"
                }
            });
            throw;
        }
        finally
        {
            executionGate.Release();
        }
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
            targetCatalog[target.TargetHandle] = target;
        foreach (var software in await softwareRegistry.GetSoftwareAsync(cancellationToken))
        {
            var handle = AdapterResourceKey.FromString($"report:software:{software.Id}");
            targetCatalog[handle] = new HostManagerReportTargetDescriptor(handle,
                software.Id, software.Name, software.Kind, software.DisplayKind, [], []);
        }
    }

    private void AcquireObservationSubscriptions()
    {
        if (processSubscription is not null
            || metricSubscription is not null
            || resourceSubscription is not null
            || interruptSubscription is not null)
        {
            throw new InvalidOperationException(
                "Report observation subscriptions are already active.");
        }

        try
        {
            processSubscription = processSource.AcquireSubscription(
                "host-manager.reports.runtime-state",
                ReportProcessMetrics, SampleInterval);
            metricSubscription = metricSource.AcquireSubscription(
                "host-manager.reports.hardware",
                ReportMetricRequest,
                SampleInterval);
            resourceSubscription = resourceSource.AcquireSubscription(
                "host-manager.reports.software-memory",
                ReportResourceRequest,
                SampleInterval);
            interruptSubscription = interruptSource.AcquireSubscription(
                "host-manager.reports.system-interrupts",
                SampleInterval);
        }
        catch (Exception acquisitionFailure)
        {
            var failures = new List<Exception> { acquisitionFailure };
            ReleaseObservationSubscriptions(failures);
            ThrowFailures(failures);
        }
    }

    private void ReleaseObservationSubscriptions(ICollection<Exception> failures)
    {
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
