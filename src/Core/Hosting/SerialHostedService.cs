using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ResourceManager.App.Hosting;

/// <summary>Preserves the Host's ordinary serial lifetime without registering a concurrent business block.</summary>
internal sealed class SerialHostedService<TService>(IServiceProvider provider) : IHostedService
    where TService : class, IHostedService
{
    internal TService Service { get; } = provider.GetRequiredService<TService>();
    public Task StartAsync(CancellationToken cancellationToken) => Service.StartAsync(cancellationToken);
    public Task StopAsync(CancellationToken cancellationToken) => Service.StopAsync(cancellationToken);
    // DI owns the underlying singleton; the alias must not dispose it a second time.
}

internal static class SerialHostedServiceRegistration
{
    internal static IServiceCollection AddSerialHostedService<TService>(this IServiceCollection services)
        where TService : class, IHostedService
    {
        services.TryAddSingleton<TService>();
        return services.AddSerialHostedServiceAlias<TService>();
    }

    internal static IServiceCollection AddSerialHostedServiceAlias<TService>(this IServiceCollection services)
        where TService : class, IHostedService
    {
        services.AddSingleton<IHostedService, SerialHostedService<TService>>();
        return services;
    }
}
