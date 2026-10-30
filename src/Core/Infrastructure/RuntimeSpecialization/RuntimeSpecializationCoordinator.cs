using ResourceManager.App.Application.Monitoring;
using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Application.CpuTopology;
using ResourceManager.App.Application.RuntimeSpecialization;
using ResourceManager.App.Application.Settings;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Domain.Settings;
using ResourceManager.App.Infrastructure.Settings;

namespace ResourceManager.App.Infrastructure.RuntimeSpecialization;

public sealed partial class RuntimeSpecializationCoordinator : IHostedService, IRuntimeSpecializationCoordinator
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly SemaphoreSlim changedWake = new(0, 1);
    private readonly RuntimePlanCompiler compiler;
    private readonly IAppSettingsStore settingsStore;
    private readonly ICpuCorePerformanceOverrideStore cpuOverrides;
    private readonly IRuntimePlanProvider planProvider;
    private readonly IMonitoringSourceZoneRegistry monitoringSourceZoneRegistry;
    private readonly ILogger<RuntimeSpecializationCoordinator> logger;
    private readonly LegacyDashboardFileMigration? legacySettingsMigration;
    private readonly ResourceManager.App.Infrastructure.MemoryProtection.HostMemoryProtectionService? memoryProtection;
    private readonly IHostManagerRollbackStateStore? smartRollbackStateStore;
    private CancellationTokenSource? changedWorkerCancellation;
    private Task? changedWorker;
    private Task? executionTask;

    internal Task? ExecutionTask => Volatile.Read(ref executionTask);
    private long changedSequence;
    private long processedChangedSequence;
    private long version;
    private AppSettingsUpdateResult? currentSettingsApplication;

    public AppSettingsUpdateResult ProjectAppSettingsRuntimeState(AppSettingsUpdateResult stored)
        => AppSettingsRuntimeState.Project(stored, Volatile.Read(ref currentSettingsApplication));

    private AppSettingsUpdateResult RecordSettingsApplication(AppSettingsUpdateResult result)
    {
        Volatile.Write(ref currentSettingsApplication, result);
        return result;
    }

    public RuntimeSpecializationCoordinator(
        RuntimePlanCompiler compiler,
        IAppSettingsStore settingsStore,
        ICpuCorePerformanceOverrideStore cpuOverrides,
        IRuntimePlanProvider planProvider,
        IMonitoringSourceZoneRegistry monitoringSourceZoneRegistry,
        ILogger<RuntimeSpecializationCoordinator> logger,
        LegacyDashboardFileMigration? legacySettingsMigration = null,
        ResourceManager.App.Infrastructure.MemoryProtection.HostMemoryProtectionService? memoryProtection = null,
        IHostManagerRollbackStateStore? smartRollbackStateStore = null)
    {
        this.compiler = compiler;
        this.settingsStore = settingsStore;
        this.cpuOverrides = cpuOverrides;
        this.planProvider = planProvider;
        this.monitoringSourceZoneRegistry = monitoringSourceZoneRegistry;
        this.logger = logger;
        this.legacySettingsMigration = legacySettingsMigration;
        this.memoryProtection = memoryProtection;
        this.smartRollbackStateStore = smartRollbackStateStore;
    }

    public async Task<AppSettingsUpdateResult> ApplyAppSettingsAsync(
        AppSettings settings,
        string reason,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        await gate.WaitAsync(cancellationToken);
        try
        {
            var previous = await settingsStore.LoadAsync(cancellationToken);
            return await ApplyAppSettingsUnderGateAsync(
                settings,
                reason,
                previous,
                cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<AppSettingsUpdateResult> ApplyAppSettingsPatchAsync(
        AppSettingsPatchRequest patch,
        string reason,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(patch);
        if (string.IsNullOrWhiteSpace(patch.ExpectedRevision))
        {
            throw new AppSettingsPatchException(
                "An exact application settings revision is required.");
        }

        await gate.WaitAsync(cancellationToken);
        try
        {
            var previous = await settingsStore.LoadAsync(cancellationToken);
            if (!string.Equals(
                    patch.ExpectedRevision,
                    previous.Revision,
                    StringComparison.OrdinalIgnoreCase))
            {
                return previous with
                {
                    RuntimeApplicationDisposition = "revisionConflict",
                    RuntimeFailureCode = "settings-revision-conflict"
                };
            }

            AppSettings candidate;
            try { candidate = AppSettingsPatchApplier.Apply(previous.Settings, patch.Changes); }
            catch (AppSettingsPatchException error) when (error.FieldPath is not null)
            {
                return previous with { RuntimeApplicationDisposition = "rejectedBeforeCommit",
                    RuntimeFailureCode = "invalid-settings-value-type", RuntimeFailurePath = error.FieldPath };
            }
            return await ApplyAppSettingsUnderGateAsync(
                candidate,
                reason,
                previous,
                cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<AppSettingsUpdateResult> ApplyAppSettingsUnderGateAsync(
        AppSettings settings,
        string reason,
        AppSettingsUpdateResult previous,
        CancellationToken cancellationToken)
    {
        if (memoryProtection?.ValidateSettings(settings.MemoryProtection ?? new()) is { } memoryReason)
            return previous with { RuntimeApplicationDisposition = "rejectedBeforeCommit", RuntimeFailureCode = memoryReason };
        if (MemoryCpuTierResolver.Validate(settings.MemoryCpuTier) is { } tierRejection)
            return previous with { RuntimeApplicationDisposition = "rejectedBeforeCommit",
                RuntimeFailureCode = tierRejection.Code, RuntimeFailurePath = tierRejection.Path };
        if (TargetedRecordingProfileShape.Validate(settings.TargetedRecording) is { } recordingReason)
            return previous with { RuntimeApplicationDisposition = "rejectedBeforeCommit", RuntimeFailureCode = recordingReason };
        if (!AppSettingsHotkeySafetyValidator.AreEnabledDestructiveHotkeysSafe(settings))
        {
            return previous with
            {
                RuntimeApplicationDisposition = "rejectedBeforeCommit",
                RuntimeFailureCode = "unsafe-destructive-hotkey"
            };
        }

        var nextVersion = Interlocked.Increment(ref version);
        CompiledRuntimePlan? candidatePlan = null;
        AppSettingsUpdateResult committed;
        try
        {
            committed = await settingsStore.SaveValidatedAsync(
                settings,
                async (candidate, validationCancellationToken) =>
                {
                    candidatePlan = ResourceManager.App.Infrastructure.RuntimeSpecialization.FreedomPoints.FreedomPointPlanApplication.Prepare(
                        planProvider.Current, await compiler.CompileAsync(
                            nextVersion,
                            reason,
                            candidate,
                            validationCancellationToken));
                },
                cancellationToken);
        }
        catch (AppSettingsCommitAmbiguousException exception)
        {
            logger.LogError(
                exception,
                "Application settings commit became ambiguous before runtime plan publication.");
            AppSettingsUpdateResult observed;
            try
            {
                observed = await settingsStore.LoadAsync(CancellationToken.None);
            }
            catch
            {
                observed = previous;
            }
            return RecordSettingsApplication(observed with
            {
                RuntimeApplicationDisposition = "savedNotApplied",
                RuntimeFailureCode = "settings-commit-ambiguous"
            });
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(
                exception,
                "Application settings candidate was rejected before durable commit.");
            return previous with
            {
                RuntimeApplicationDisposition = "rejectedBeforeCommit",
                RuntimeFailureCode = exception is ReportRuleSettingException invalidRule
                    ? invalidRule.FailureCode
                    : candidatePlan is null
                        ? "runtime-plan-compile-failed"
                        : "settings-commit-failed"
            };
        }

        if (candidatePlan is null)
        {
            throw new InvalidOperationException(
                "The validated application settings candidate has no compiled runtime plan.");
        }

        try
        {
            await SynchronizeSmartRecordHistorySettingAsync(CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Settings committed, but smart record history could not be persisted.");
            return RecordSettingsApplication(committed with
            {
                RuntimeApplicationDisposition = "savedNotApplied",
                RuntimeFailureCode = "smart-record-history-persistence-failed"
            });
        }

        try
        {
            var publication = await PublishAsync(candidatePlan);
            return RecordSettingsApplication(committed with
            {
                RuntimeApplicationDisposition =
                    AppSettingsRuntimeApplicationDisposition.ResolvePublished(
                        publication.HasDeliveryFailures,
                        publication.Plan.RuntimeCapabilityConstrainedPaths),
                RuntimePlanVersion = publication.Plan.Version,
                RuntimePublicationSequence = publication.PublicationSequence,
                RuntimeDeliveryFailureCount = publication.DeliveryFailures.Count,
                RuntimeCapabilityConstrainedPaths =
                    publication.Plan.RuntimeCapabilityConstrainedPaths
            });
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(
                exception,
                "Application settings committed, but runtime plan publication failed.");
            return RecordSettingsApplication(committed with
            {
                RuntimeApplicationDisposition = "savedNotApplied",
                RuntimeCapabilityConstrainedPaths =
                    candidatePlan.RuntimeCapabilityConstrainedPaths,
                RuntimeFailureCode = "runtime-plan-publication-failed"
            });
        }
    }

    public async Task<RuntimePlanPublicationResult> RebuildAsync(
        string reason,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            await SynchronizeSmartRecordHistorySettingAsync(cancellationToken);
            return await CompileAndPublishAsync(reason, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (changedWorkerCancellation is not null)
        {
            throw new InvalidOperationException(
                "The runtime specialization coordinator is already started.");
        }

        await gate.WaitAsync(cancellationToken);
        var workerCancellation = new CancellationTokenSource();
        changedWorkerCancellation = workerCancellation;
        monitoringSourceZoneRegistry.Changed += OnMonitoringSourceZonesChanged;
        try
        {
            if (legacySettingsMigration is not null) await legacySettingsMigration.RunAsync(cancellationToken);
            await CompileAndPublishAsync("startup", cancellationToken);
        }
        catch
        {
            monitoringSourceZoneRegistry.Changed -= OnMonitoringSourceZonesChanged;
            changedWorkerCancellation = null;
            workerCancellation.Dispose();
            throw;
        }
        finally
        {
            gate.Release();
        }
        changedWorker = Task.Run(
            () => RunMonitoringSourceChangePumpAsync(workerCancellation.Token),
            CancellationToken.None);
        Volatile.Write(ref executionTask, changedWorker);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        monitoringSourceZoneRegistry.Changed -= OnMonitoringSourceZonesChanged;
        var cancellation = changedWorkerCancellation;
        var worker = changedWorker;
        if (cancellation is null)
        {
            return;
        }

        await DrainChangedWorkerAsync(cancellation, worker).WaitAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task DrainChangedWorkerAsync(CancellationTokenSource cancellation, Task? worker)
    {
        Exception? failure = null;
        try
        {
            try { await cancellation.CancelAsync().ConfigureAwait(false); }
            catch (ObjectDisposedException) { }
            catch (Exception error) { failure = error; }
            SignalChangedWorker();
            try { if (worker is not null) await worker.ConfigureAwait(false); }
            catch (Exception error) { failure = error; }
        }
        finally
        {
            Interlocked.CompareExchange(ref changedWorker, null, worker);
            if (ReferenceEquals(Interlocked.CompareExchange(
                    ref changedWorkerCancellation, null, cancellation), cancellation))
            {
                cancellation.Dispose();
            }
        }
        if (failure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private void OnMonitoringSourceZonesChanged()
    {
        _ = Interlocked.Increment(ref changedSequence);
        SignalChangedWorker();
    }

    private void SignalChangedWorker()
    {
        try
        {
            changedWake.Release();
        }
        catch (SemaphoreFullException)
        {
            // A wake is already pending; the sequence retains the latest change.
        }
    }

    private async Task RunMonitoringSourceChangePumpAsync(
        CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await changedWake.WaitAsync(stoppingToken).ConfigureAwait(false);
                while (!stoppingToken.IsCancellationRequested)
                {
                    var targetSequence = Volatile.Read(ref changedSequence);
                    if (targetSequence == Volatile.Read(ref processedChangedSequence))
                    {
                        break;
                    }

                    try
                    {
                        await RebuildAsync(
                                "monitoring-source-zones-changed",
                                stoppingToken)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (
                        stoppingToken.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception exception)
                    {
                        logger.LogError(
                            exception,
                            "Runtime specialization rebuild failed for monitoring source change sequence {Sequence}.",
                            targetSequence);
                    }
                    Volatile.Write(ref processedChangedSequence, targetSequence);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task<RuntimePlanPublicationResult> CompileAndPublishAsync(
        string reason,
        CancellationToken cancellationToken)
    {
        var nextVersion = Interlocked.Increment(ref version);
        var plan = await compiler.CompileAsync(nextVersion, reason, cancellationToken);
        var publication = await PublishAsync(ResourceManager.App.Infrastructure.RuntimeSpecialization.FreedomPoints.FreedomPointPlanApplication.Prepare(
            planProvider.Current, plan));
        var stored = await settingsStore.LoadReadOnlyAsync(CancellationToken.None);
        RecordSettingsApplication(stored with
        {
            RuntimeApplicationDisposition = AppSettingsRuntimeApplicationDisposition.ResolvePublished(
                publication.HasDeliveryFailures, publication.Plan.RuntimeCapabilityConstrainedPaths),
            RuntimePlanVersion = publication.Plan.Version,
            RuntimePublicationSequence = publication.PublicationSequence,
            RuntimeDeliveryFailureCount = publication.DeliveryFailures.Count,
            RuntimeCapabilityConstrainedPaths = publication.Plan.RuntimeCapabilityConstrainedPaths
        });
        return publication;
    }

    private async Task<RuntimePlanPublicationResult> PublishAsync(CompiledRuntimePlan plan)
    {
        var publication = planProvider.Publish(plan);
        if (memoryProtection is not null) await memoryProtection.ApplySettingsAsync(plan.MemoryProtection);
        foreach (var failure in publication.DeliveryFailures)
        {
            logger.LogError(
                "Runtime plan {Version} publication {PublicationSequence} committed, but delivery to {Subscriber} failed with {ExceptionType}: {Message}",
                plan.Version,
                publication.PublicationSequence,
                failure.Subscriber,
                failure.ExceptionType,
                failure.Message);
        }
        logger.LogDebug(
            "Runtime specialization plan {Version} publication {PublicationSequence} rebuilt for {Reason} with Host digest {PlanDigest} and {DeliveryFailureCount} delivery failures.",
            plan.Version,
            publication.PublicationSequence,
            plan.Reason,
            plan.HostManager.PlanSha256,
            publication.DeliveryFailures.Count);
        return publication;
    }
}
