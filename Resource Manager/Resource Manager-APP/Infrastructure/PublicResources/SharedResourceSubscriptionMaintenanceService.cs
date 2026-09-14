using ResourceManager.App.Application.PublicResources;
using ResourceManager.Adapter.SharedMemory;

namespace ResourceManager.App.Infrastructure.PublicResources;

internal sealed class SharedResourceSubscriptionMaintenanceService(
    ISharedResourceSubscriptionMaintenance maintenance,
    ILogger<SharedResourceSubscriptionMaintenanceService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var maintenanceIntervalMilliseconds = maintenance.MaintenanceIntervalMilliseconds;
            await Task.Delay(maintenanceIntervalMilliseconds, stoppingToken).ConfigureAwait(false);
            try
            {
                maintenance.Refresh(NativeSharedResourceSession.MonotonicNow());
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Shared-resource subscription maintenance failed.");
            }
        }
    }
}
