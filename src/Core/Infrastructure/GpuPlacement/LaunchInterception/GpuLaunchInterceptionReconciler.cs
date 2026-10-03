using ResourceManager.App.Application.GpuPlacement;
using ResourceManager.App.Application.Overlay;
using ResourceManager.App.Application.Software;
using ResourceManager.App.Domain.GpuPlacement;
using ResourceManager.App.Domain.Overlay;
using ResourceManager.App.Domain.Software;

namespace ResourceManager.App.Infrastructure.GpuPlacement;

public sealed class GpuLaunchInterceptionReconciler(
    IGpuPlacementPolicyStore policyStore,
    IPerformanceOverlaySettingsStore overlaySettingsStore,
    ISoftwareRegistryView softwareRegistry,
    IGpuLaunchInterceptionRegistry registry,
    IGpuSchedulingAvailability schedulingAvailability,
    ILogger<GpuLaunchInterceptionReconciler> logger) : BackgroundService
{
    private readonly SemaphoreSlim reconciliationGate = new(1, 1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await ReconcileAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Launch interception reconciliation failed.");
        }
    }

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
                var settings = await overlaySettingsStore.GetAsync(cancellationToken);
                if (settings.Software.Any(static item => item.Enabled
                    && item.Mode.Equals("injected", StringComparison.OrdinalIgnoreCase)))
                {
                    var software = await softwareRegistry.GetSoftwareAsync(cancellationToken);
                    paths = SelectOverlayExecutablePaths(settings, software);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Overlay launch rule selection failed; GPU rules will still be reconciled.");
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

    internal static IReadOnlyCollection<string> SelectOverlayExecutablePaths(
        PerformanceOverlaySettingsDocument settings,
        IReadOnlyList<SoftwareRecord> software)
    {
        var enabled = settings.Software
            .Where(static item => item.Enabled && item.Mode.Equals("injected", StringComparison.OrdinalIgnoreCase))
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
