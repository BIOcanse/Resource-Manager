using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using ResourceManager.App.Application.RuntimeSpecialization.BusinessBlocks;

namespace ResourceManager.App.Infrastructure.RuntimeSpecialization.BusinessBlocks;

/// <summary>Withdraws dependency leases after ordinary hosted consumers have stopped.</summary>
public sealed class BusinessBlockDependencyLifetime(IServiceProvider services) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken)
    {
        var controller = services.GetRequiredService<BusinessBlockController>();
        return controller.StopDependenciesAsync(
            new BusinessBlockReason(BusinessBlockRequestKind.Dependency, "host", "host-shutdown", controller.CurrentPlanVersion),
            cancellationToken);
    }
}
