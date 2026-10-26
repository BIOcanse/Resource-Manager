using ResourceManager.App.Application.GpuPlacement;
using ResourceManager.Shared.Packages;
using ResourceManager.App.Application.Software;
using ResourceManager.App.Domain.GpuPlacement;
using ResourceManager.App.Domain.Software;

namespace ResourceManager.App.Infrastructure.GpuPlacement;

public sealed class GpuLaunchInterceptionReconciler(
    IGpuPlacementPolicyStore policyStore,
    IStartupModuleSettingsStore moduleSettingsStore,
    PackageRuntimeCatalog moduleCatalog,
    ISoftwareRegistryView softwareRegistry,
    IGpuLaunchInterceptionRegistry registry,
    IGpuSchedulingAvailability schedulingAvailability,
    ILogger<GpuLaunchInterceptionReconciler> logger) : IHostedService
{
    private readonly SemaphoreSlim reconciliationGate = new(1, 1);

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await ReconcileAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Launch interception reconciliation failed.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task<IReadOnlyList<GpuLaunchInterceptionStatus>> ReconcileAsync(CancellationToken cancellationToken)
    {
        await reconciliationGate.WaitAsync(cancellationToken);
        try
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
                logger.LogWarning(ex, "GPU scheduling availability evaluation failed; GPU rules are not reconciled.");
                gpuAvailable = false;
            }

            var document = await policyStore.GetAsync(cancellationToken);
            IReadOnlyCollection<string> paths = [];
            try
            {
                var settings = await moduleSettingsStore.GetAsync(cancellationToken);
                var software = await softwareRegistry.GetSoftwareAsync(cancellationToken);
                paths = SelectModuleExecutablePaths(settings, software, moduleCatalog);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Startup module rule selection failed; GPU rules will still be reconciled.");
            }
            var statuses = registry.Reconcile(
                gpuAvailable ? document : document with { ProcessPolicies = [] }, paths);
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
        finally
        {
            reconciliationGate.Release();
        }
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
