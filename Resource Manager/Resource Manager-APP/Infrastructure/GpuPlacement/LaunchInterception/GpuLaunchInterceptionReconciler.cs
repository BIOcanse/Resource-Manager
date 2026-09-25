using ResourceManager.App.Application.GpuPlacement;

namespace ResourceManager.App.Infrastructure.GpuPlacement;

public sealed class GpuLaunchInterceptionReconciler(
    IGpuPlacementPolicyStore policyStore,
    IGpuLaunchInterceptionRegistry registry,
    IGpuSchedulingAvailability schedulingAvailability,
    ILogger<GpuLaunchInterceptionReconciler> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // 单显卡没有可迁移目标，不写启动拦截规则。
            var availability = await schedulingAvailability.EvaluateAsync(stoppingToken);
            if (!availability.Enabled)
            {
                logger.LogInformation(
                    "GPU launch interception has no migration target ({Domain}/{Code}); rules are not reconciled.",
                    availability.Reason?.Domain,
                    availability.Reason?.Code);
                return;
            }

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
