using ResourceManager.App.Application.RuntimeSpecialization;
using ResourceManager.App.Application.RuntimeSpecialization.BusinessBlocks;
using ResourceManager.App.Infrastructure.RuntimeSpecialization.BusinessBlocks;

namespace ResourceManager.App.Hosting;

internal sealed class NonOwningHostedService<TService> : BackgroundService where TService : class, IHostedService
{
    private readonly IServiceProvider provider;
    private readonly int id;
    private TService? service;

    private HostedBusinessBlock Block => provider.GetRequiredService<HostedBusinessBlockRegistry>()
        .GetOrAdd(id, () => Service);

    internal NonOwningHostedService(IServiceProvider provider, int id, bool constructService = false)
    {
        this.provider = provider;
        this.id = id;
        if (constructService) service = provider.GetRequiredService<TService>();
    }
    internal TService Service => service ??= provider.GetRequiredService<TService>();
    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        _ = Service;
        _ = provider.GetRequiredService<BusinessBlockScheduler>();
        var block = Block;
        _ = block.PrepareStart(cancellationToken);
        try
        {
            await provider.GetRequiredService<BackendHostPermitFacts>().SetAsync(id, true, Reason("host-start")).ConfigureAwait(false);
            await base.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            block.SetHostStopTurn();
            await provider.GetRequiredService<BackendHostPermitFacts>().SetAsync(id, false, Reason("host-start-failed")).ConfigureAwait(false);
            try { await block.CurrentStopCompletion().ConfigureAwait(false); }
            catch { /* The original StartAsync exception belongs to this host turn. */ }
            throw;
        }
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Block.ExecutionFailure.WaitAsync(stoppingToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            var block = Block;
            block.SetHostStopTurn();
            var withdrawal = provider.GetRequiredService<BackendHostPermitFacts>().SetAsync(id, false, Reason("host-stop"));
            try
            {
                await withdrawal.WaitAsync(cancellationToken).ConfigureAwait(false);
                await block.CurrentStopCompletion().WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        }
        finally { await base.StopAsync(cancellationToken).ConfigureAwait(false); }
    }
    private BusinessBlockReason Reason(string operation) => new(BusinessBlockRequestKind.Configuration,
        "generic-host", $"hosting.{id}.{operation}", provider.GetService<IRuntimePlanProvider>()?.Current.Version ?? 0);
}

internal static class NonOwningHostedServiceRegistration
{
    internal static IServiceCollection AddHostedServiceAlias<TService>(this IServiceCollection services,
        int id, Func<TService, Task?> executionTask, bool forwardRunFailureToHost,
        Func<TService, bool>? resourceFailureHandled = null, bool constructService = false) where TService : class, IHostedService
    {
        services.AddHostedServiceAlias<TService>(id, constructService);
        return WithExecution(services, executionTask, forwardRunFailureToHost, resourceFailureHandled);
    }

    internal static IServiceCollection AddHostedServiceAlias<TService>(this IServiceCollection services,
        Func<TService, Task?> executionTask, bool forwardRunFailureToHost,
        Func<TService, bool>? resourceFailureHandled = null, bool constructService = false) where TService : class, IHostedService
    {
        services.AddHostedServiceAlias<TService>(constructService);
        return WithExecution(services, executionTask, forwardRunFailureToHost, resourceFailureHandled);
    }

    private static IServiceCollection WithExecution<TService>(IServiceCollection services,
        Func<TService, Task?> executionTask, bool forwardRunFailureToHost,
        Func<TService, bool>? resourceFailureHandled) where TService : class, IHostedService
    {
        ArgumentNullException.ThrowIfNull(executionTask);
        var descriptor = services.Single(value => value.ImplementationInstance is BusinessBlockRegistration registration
            && registration.HostedServiceType == typeof(TService));
        var declaration = (BusinessBlockRegistration)descriptor.ImplementationInstance!;
        services[services.IndexOf(descriptor)] = ServiceDescriptor.Singleton(declaration with
        {
            HostedExecution = new(service => executionTask((TService)service), forwardRunFailureToHost,
                resourceFailureHandled is null ? null : service => resourceFailureHandled((TService)service))
        });
        return services;
    }

    internal static IServiceCollection AddHostedServiceAlias<TService>(this IServiceCollection services,
        int id, bool constructService = false) where TService : class, IHostedService
    {
        ResourceManagerServiceCollectionExtensions.RegisterHostedBusinessBlock<TService>(services, id);
        services.AddSingleton(new HostedBusinessBlockAlias(id));
        services.AddSingleton<IHostedService>(provider => new NonOwningHostedService<TService>(provider, id, constructService));
        return services;
    }

    // Used by independently owned registration files whose declaration is supplied by the composition root.
    internal static IServiceCollection AddHostedServiceAlias<TService>(this IServiceCollection services,
        bool constructService = false) where TService : class, IHostedService
    {
        var declaration = services.Select(value => value.ImplementationInstance).OfType<BusinessBlockRegistration>()
            .Single(value => value.HostedServiceType == typeof(TService));
        services.AddSingleton(new HostedBusinessBlockAlias(declaration.Id));
        services.AddSingleton<IHostedService>(provider => new NonOwningHostedService<TService>(provider, declaration.Id, constructService));
        return services;
    }
}

/// <summary>Composition metadata for host admission only; it carries no control topology.</summary>
internal sealed record HostedBusinessBlockAlias(int Id);
