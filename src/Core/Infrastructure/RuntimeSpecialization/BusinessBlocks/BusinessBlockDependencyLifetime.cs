using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using ResourceManager.App.Application.RuntimeSpecialization.BusinessBlocks;

namespace ResourceManager.App.Infrastructure.RuntimeSpecialization.BusinessBlocks;

/// <summary>Records terminal root shutdown before the original hosted-service stop turns.</summary>
public sealed class BusinessBlockDependencyLifetime(IServiceProvider services) : IHostedLifecycleService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = services.GetRequiredService<BusinessBlockScheduler>();
        return Task.CompletedTask;
    }

    public Task StoppingAsync(CancellationToken cancellationToken)
    {
        services.GetRequiredService<HostedBusinessBlockRegistry>().BeginHostStopping();
        var scheduler = services.GetRequiredService<BusinessBlockScheduler>();
        return scheduler.ShutdownAsync(new BusinessBlockReason(BusinessBlockRequestKind.Configuration,
            "generic-host", "host-shutdown", scheduler.CurrentPlanVersion));
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
