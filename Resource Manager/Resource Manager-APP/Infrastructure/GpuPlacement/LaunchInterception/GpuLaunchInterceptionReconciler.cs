using ResourceManager.App.Application.GpuPlacement;

namespace ResourceManager.App.Infrastructure.GpuPlacement;

public sealed class GpuLaunchInterceptionReconciler(
    IGpuPlacementPolicyStore policyStore,
    IGpuLaunchInterceptionRegistry registry,
    ILogger<GpuLaunchInterceptionReconciler> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var document = await policyStore.GetAsync(stoppingToken);
            var statuses = registry.Reconcile(document);
            foreach (var status in statuses.Where(static status => status.Requested && !status.Registered))
            {
                logger.LogWarning(
                    "GPU startup interception for {ExecutablePath} is not active: {Status} {Message}",
                    status.ExecutablePath,
                    status.Status,
                    status.Message);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "GPU launch interception reconciliation failed.");
        }
    }
}
