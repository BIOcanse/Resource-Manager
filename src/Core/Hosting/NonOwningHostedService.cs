using ResourceManager.App.Application.RuntimeSpecialization;
using ResourceManager.App.Application.RuntimeSpecialization.BusinessBlocks;
using ResourceManager.App.Infrastructure.RuntimeSpecialization.BusinessBlocks;

namespace ResourceManager.App.Hosting;

internal sealed class NonOwningHostedService<TService>(IServiceProvider provider) : BackgroundService
    where TService : class, IHostedService
{
    private TService? service;

    internal TService Service
        => service ??= provider.GetRequiredService<TService>();

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        _ = Service;
        await provider.GetRequiredService<BusinessBlockController>().StartAsync(
            typeof(TService).Name, Reason("host-start"), cancellationToken).ConfigureAwait(false);
        await base.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    // The host must observe the real worker task; DI still owns the service's disposal.
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (Service is not BackgroundService backgroundService || backgroundService.ExecuteTask is not { } execution) return;
        try { await execution.ConfigureAwait(false); }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested
            || provider.GetRequiredService<BusinessBlockController>().IsStopRequested(typeof(TService).Name)) { }
        catch (Exception error)
        {
            provider.GetRequiredService<BusinessBlockController>().ReportFailure(typeof(TService).Name, error, "hosted-worker");
            throw;
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (service is not null)
            {
                await provider.GetRequiredService<BusinessBlockController>().StopAsync(
                    typeof(TService).Name, Reason("host-stop"), cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            await base.StopAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private BusinessBlockReason Reason(string operation) => new(
        BusinessBlockRequestKind.Configuration, "generic-host", $"hosting.{typeof(TService).Name}.{operation}",
        provider.GetService<IRuntimePlanProvider>()?.Current.Version ?? 0);
}

internal static class NonOwningHostedServiceRegistration
{
    internal static IServiceCollection AddHostedServiceAlias<TService>(
        this IServiceCollection services)
        where TService : class, IHostedService
    {
        ResourceManagerServiceCollectionExtensions.RegisterHostedBusinessBlock<TService>(services);
        services.AddSingleton<IHostedService>(static provider =>
            new NonOwningHostedService<TService>(provider));
        return services;
    }
}
