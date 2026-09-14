namespace ResourceManager.App.Hosting;

internal sealed class NonOwningHostedService<TService>(IServiceProvider provider) : BackgroundService
    where TService : class, IHostedService
{
    private TService? service;

    internal TService Service
        => service ??= provider.GetRequiredService<TService>();

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await Service.StartAsync(cancellationToken).ConfigureAwait(false);
        await base.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    // The host must observe the real worker task; DI still owns the service's disposal.
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
        => Service is BackgroundService backgroundService
            ? backgroundService.ExecuteTask ?? Task.CompletedTask
            : Task.CompletedTask;

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (service is not null)
            {
                await service.StopAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            await base.StopAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}

internal static class NonOwningHostedServiceRegistration
{
    internal static IServiceCollection AddHostedServiceAlias<TService>(
        this IServiceCollection services)
        where TService : class, IHostedService
    {
        services.AddSingleton<IHostedService>(static provider =>
            new NonOwningHostedService<TService>(provider));
        return services;
    }
}
