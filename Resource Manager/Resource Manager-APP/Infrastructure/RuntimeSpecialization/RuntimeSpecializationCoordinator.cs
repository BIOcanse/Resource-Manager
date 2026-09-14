using ResourceManager.App.Application.Monitoring;
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
    private CancellationTokenSource? changedWorkerCancellation;
    private Task? changedWorker;
    private long changedSequence;
    private long processedChangedSequence;
    private long version;

    public RuntimeSpecializationCoordinator(
        RuntimePlanCompiler compiler,
        IAppSettingsStore settingsStore,
        ICpuCorePerformanceOverrideStore cpuOverrides,
        IRuntimePlanProvider planProvider,
        IMonitoringSourceZoneRegistry monitoringSourceZoneRegistry,
        ILogger<RuntimeSpecializationCoordinator> logger)
    {
        this.compiler = compiler;
        this.settingsStore = settingsStore;
        this.cpuOverrides = cpuOverrides;
        this.planProvider = planProvider;
        this.monitoringSourceZoneRegistry = monitoringSourceZoneRegistry;
        this.logger = logger;
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

            var candidate = AppSettingsPatchApplier.Apply(
                previous.Settings,
                patch.Changes);
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
                    candidatePlan = await compiler.CompileAsync(
                        nextVersion,
                        reason,
                        candidate,
                        validationCancellationToken);
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
            return observed with
            {
                RuntimeApplicationDisposition = "savedNotApplied",
                RuntimeFailureCode = "settings-commit-ambiguous"
            };
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(
                exception,
                "Application settings candidate was rejected before durable commit.");
            return previous with
            {
                RuntimeApplicationDisposition = "rejectedBeforeCommit",
                RuntimeFailureCode = candidatePlan is null
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
            var publication = Publish(candidatePlan);
            return committed with
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
            };
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(
                exception,
                "Application settings committed, but runtime plan publication failed.");
            return committed with
            {
                RuntimeApplicationDisposition = "savedNotApplied",
                RuntimeCapabilityConstrainedPaths =
                    candidatePlan.RuntimeCapabilityConstrainedPaths,
                RuntimeFailureCode = "runtime-plan-publication-failed"
            };
        }
    }

    public async Task<RuntimePlanPublicationResult> RebuildAsync(
        string reason,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
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

        var workerCancellation = new CancellationTokenSource();
        changedWorkerCancellation = workerCancellation;
        monitoringSourceZoneRegistry.Changed += OnMonitoringSourceZonesChanged;
        await gate.WaitAsync(cancellationToken);
        try
        {
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
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        monitoringSourceZoneRegistry.Changed -= OnMonitoringSourceZonesChanged;
        var cancellation = Interlocked.Exchange(
            ref changedWorkerCancellation,
            null);
        var worker = Interlocked.Exchange(ref changedWorker, null);
        if (cancellation is null)
        {
            return;
        }

        cancellation.Cancel();
        SignalChangedWorker();
        if (worker is not null)
        {
            await worker.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        cancellation.Dispose();
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
        return Publish(plan);
    }

    private RuntimePlanPublicationResult Publish(CompiledRuntimePlan plan)
    {
        var publication = planProvider.Publish(plan);
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
