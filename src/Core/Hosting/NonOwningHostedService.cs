using BlockUids = ResourceManager.App.StateItems.BlockUids;
using ResourceManager.App.Application.RuntimeSpecialization;
using ResourceManager.App.Application.RuntimeSpecialization.BusinessBlocks;
using ResourceManager.App.Infrastructure.RuntimeSpecialization.BusinessBlocks;

namespace ResourceManager.App.Hosting;

internal sealed class NonOwningHostedService<TService> : BackgroundService where TService : class, IHostedService
{
    private readonly IServiceProvider provider;
    private readonly int id;
    private TService? service;
    private HostedBusinessBlock.Session? hostSession;

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
        var scheduler = provider.GetRequiredService<BusinessBlockScheduler>();
        var block = Block;
        var session = block.PrepareStart(cancellationToken);
        hostSession = session;
        try
        {
            await scheduler.SetDemandAsync("host:" + id, id, Reason("host-start"), cancellationToken).ConfigureAwait(false);
            await session.Readiness.Task.ConfigureAwait(false);
            await base.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            block.SetHostStopTurn();
            await scheduler.SetDemandAsync("host:" + id, null, Reason("host-start-failed")).ConfigureAwait(false);
            try { await block.CurrentStopCompletion().ConfigureAwait(false); }
            catch { /* The original StartAsync exception belongs to this host turn. */ }
            throw;
        }
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var session = hostSession;
        if (session?.Execution is not { } execution) return;
        try { await execution.ConfigureAwait(false); }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested
            || session.StopNotification.IsCancellationRequested) { }
    }
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            // External demand can start this block before Host reaches this alias's StartAsync.
            var block = Block;
            block.SetHostStopTurn();
            // Withdrawal remains a fact even after the host stops waiting. Its
            // original token bounds this caller, not the block's real cleanup.
            var withdrawal = provider.GetRequiredService<BusinessBlockScheduler>().SetDemandAsync(
                "host:" + id, null, Reason("host-stop"));
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
        int id, int[] inputs, bool constructService = false,
        int parentId = BlockUids.Program) where TService : class, IHostedService
    {
        ResourceManagerServiceCollectionExtensions.RegisterHostedBusinessBlock<TService>(services, id, inputs, parentId);
        services.AddSingleton<IHostedService>(provider => new NonOwningHostedService<TService>(provider, id, constructService));
        return services;
    }

    // Used by independently owned registration files whose declaration is supplied by the composition root.
    internal static IServiceCollection AddHostedServiceAlias<TService>(this IServiceCollection services) where TService : class, IHostedService
    {
        var declaration = services.Select(value => value.ImplementationInstance).OfType<BusinessBlockRegistration>()
            .Single(value => value.HostedServiceType == typeof(TService));
        services.AddSingleton<IHostedService>(provider => new NonOwningHostedService<TService>(provider, declaration.Id));
        return services;
    }
}
