using ResourceManager.App.Application.GpuPlacement;
using ResourceManager.Shared.Packages;
using ResourceManager.App.Application.Software;
using ResourceManager.App.Domain.GpuPlacement;
using ResourceManager.App.Domain.Software;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;

namespace ResourceManager.App.Infrastructure.GpuPlacement;

public sealed class GpuLaunchInterceptionReconciler(
    IGpuPlacementPolicyStore policyStore,
    IStartupModuleSettingsStore moduleSettingsStore,
    PackageRuntimeCatalog moduleCatalog,
    ISoftwareRegistryView softwareRegistry,
    IGpuLaunchInterceptionRegistry registry,
    IGpuSchedulingAvailability schedulingAvailability,
    ILogger<GpuLaunchInterceptionReconciler> logger,
    RuntimePlanProvider plans) : IHostedService
{
    private readonly SemaphoreSlim reconciliationGate = new(1, 1);
    private string? attemptedIdentityConfiguration;
    private readonly object publicationSync = new();
    private PublicationRun? publicationRun;
    private Task stopping = Task.CompletedTask;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (publicationSync)
        {
            if (publicationRun is not null || !stopping.IsCompleted)
                throw new InvalidOperationException("Launch interception publication subscription is already started.");
            var run = new PublicationRun();
            run.Changed = plan => OnPlanPublished(run, plan);
            publicationRun = run;
            plans.Published += run.Changed;
            // Event-derived current value: cover publication before subscription,
            // without depending on the order of the host's serial start turns.
            OnPlanPublished(run, plans.Current);
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        lock (publicationSync)
        {
            if (publicationRun is not { } run) return stopping;
            publicationRun = null;
            plans.Published -= run.Changed;
            run.Pending = null;
            return stopping = StopRunAsync(run);
        }
    }

    private static async Task StopRunAsync(PublicationRun run)
    {
        try
        {
            await run.Cancellation.CancelAsync().ConfigureAwait(false);
            await run.Work.ConfigureAwait(false);
        }
        finally { run.Cancellation.Dispose(); }
    }

    internal Task PublicationWork
    {
        get { lock (publicationSync) return publicationRun?.Work ?? Task.CompletedTask; }
    }

    private void OnPlanPublished(PublicationRun run, CompiledRuntimePlan plan)
    {
        lock (publicationSync)
        {
            if (!ReferenceEquals(publicationRun, run) || !plan.HostManager.IsPublished
                || plan.Version < run.LatestVersion) return;
            run.LatestVersion = plan.Version;
            // A capacity-one pending slot coalesces current-value publications.
            if (run.Pending is null || plan.Version >= run.Pending.Version) run.Pending = plan;
            if (run.Draining) return;
            run.Draining = true;
            run.Work = Task.Run(() => DrainPublicationsAsync(run), CancellationToken.None);
        }
    }

    private async Task DrainPublicationsAsync(PublicationRun run)
    {
        try
        {
            // This drains supplied work only. No task waits when the slot is empty,
            // and a failed input is not re-enqueued.
            while (true)
            {
                CompiledRuntimePlan plan;
                lock (publicationSync)
                {
                    if (!ReferenceEquals(publicationRun, run) || run.Pending is not { } pending)
                    {
                        run.Draining = false;
                        return;
                    }
                    plan = pending;
                    run.Pending = null;
                }
                await ReconcilePlanInputAsync(plan, run.Cancellation.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (run.Cancellation.IsCancellationRequested)
        { lock (publicationSync) run.Draining = false; }
    }

    private sealed class PublicationRun
    {
        internal readonly CancellationTokenSource Cancellation = new();
        internal Action<CompiledRuntimePlan> Changed = null!;
        internal CompiledRuntimePlan? Pending;
        internal bool Draining;
        internal long LatestVersion;
        internal Task Work = Task.CompletedTask;
    }

    private async Task ReconcilePlanInputAsync(CompiledRuntimePlan plan, CancellationToken cancellationToken)
    {
        if (!plan.HostManager.IsPublished) return;
        await reconciliationGate.WaitAsync(cancellationToken);
        try
        {
            var input = plan.HostManager.SoftwareIdentityCatalog.ConfigurationSha256;
            if (attemptedIdentityConfiguration == input) return;
            try
            {
                await ReconcileCoreAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception)
            {
                // ReconcileCoreAsync reports the original failure at its boundary.
            }
            // A failure is reported once for this input, without a background retry.
            // Explicit settings commands can still invoke ReconcileAsync.
            attemptedIdentityConfiguration = input;
        }
        finally
        {
            reconciliationGate.Release();
        }
    }

    public async Task<IReadOnlyList<GpuLaunchInterceptionStatus>> ReconcileAsync(CancellationToken cancellationToken)
    {
        await reconciliationGate.WaitAsync(cancellationToken);
        try
        {
            return await ReconcileCoreAsync(cancellationToken);
        }
        finally
        {
            reconciliationGate.Release();
        }
    }

    private async Task<IReadOnlyList<GpuLaunchInterceptionStatus>> ReconcileCoreAsync(CancellationToken cancellationToken)
    {
        bool gpuAvailable;
        try
        {
            var availability = await schedulingAvailability.EvaluateAsync(cancellationToken);
            gpuAvailable = availability.Enabled;
            if (!gpuAvailable)
            {
                logger.LogInformation(
                    "GPU launch interception has no migration target ({Domain}/{Code}); GPU rules are not reconciled.",
                    availability.Reason?.Domain,
                    availability.Reason?.Code);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "GPU scheduling availability evaluation failed; existing owned rules are retained.");
            throw;
        }

        GpuPlacementPolicyDocument document;
        IReadOnlyCollection<string> paths;
        try
        {
            document = await policyStore.GetAsync(cancellationToken);
            var settings = await moduleSettingsStore.GetAsync(cancellationToken);
            var software = await softwareRegistry.GetSoftwareAsync(cancellationToken);
            paths = SelectModuleExecutablePaths(settings, software, moduleCatalog);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Startup module rule selection failed; existing owned rules are retained.");
            throw;
        }
        IReadOnlyList<GpuLaunchInterceptionStatus> statuses;
        try
        {
            statuses = registry.Reconcile(
                gpuAvailable ? document : document with { ProcessPolicies = [] }, paths);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Launch interception registry reconciliation failed.");
            throw;
        }
        foreach (var status in statuses.Where(static status => status.Requested && !status.Registered))
        {
            logger.LogWarning(
                "Startup interception for {ExecutablePath} is not active: {Status} {Message}",
                status.ExecutablePath,
                status.Status,
                status.Message);
        }
        return statuses;
    }

    internal static IReadOnlyCollection<string> SelectModuleExecutablePaths(
        StartupModuleSettingsDocument settings,
        IReadOnlyList<SoftwareRecord> software, PackageRuntimeCatalog catalog)
    {
        var enabled = settings.Software
            .Where(item => item.ModuleIds.Count > 0 && item.ModuleIds.All(id => catalog.TryGetStartupModule(id, out _)))
            .Select(static item => item.SoftwareId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return software
            .Where(item => enabled.Contains(item.Id))
            .SelectMany(static item => item.ExecutablePaths ?? [])
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
