using ResourceManager.Shared.BusinessBlocks.State;
using BlockUids = ResourceManager.App.StateItems.BlockUids;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ResourceManager.App.Application.RuntimeSpecialization.BusinessBlocks;
using ResourceManager.App.Infrastructure.RuntimeSpecialization.BusinessBlocks;
using ResourceManager.ReadWrite.Windows.Processes.Etw;
using ResourceManager.App.Infrastructure.Telemetry.Etw;
using ResourceManager.App.StateItems;

namespace ResourceManager.App.Hosting;

public static partial class ResourceManagerServiceCollectionExtensions
{
    internal static IServiceCollection AddBusinessBlockController(this IServiceCollection services)
    {
        if (services.Any(value => value.ServiceType == typeof(BusinessBlockScheduler))) return services;
        services.AddOptions<BusinessBlockOptions>().BindConfiguration(BusinessBlockOptions.SectionName);
        services.TryAddSingleton<NativeControl>(BackendNativeControlFactory.Create);
        services.TryAddSingleton<BackendStateItems>(provider => new(provider.GetRequiredService<NativeControl>()));
        services.TryAddSingleton<StateAuthority>(provider => provider.GetRequiredService<BackendStateItems>().Authority);
        services.TryAddSingleton<NativeEtwSessionAvailability>();
        services.TryAddSingleton<BusinessBlockDiagnosticsRegistry>();
        services.TryAddSingleton<HostedBusinessBlockRegistry>();
        services.TryAddSingleton<BackendHostPermitFacts>();
        services.TryAddSingleton<BusinessBlockScheduler>(provider =>
        {
            _ = provider.GetRequiredService<BackendHostPermitFacts>();
            return new(provider, provider.GetServices<BusinessBlockRegistration>(),
                provider.GetRequiredService<NativeControl>(), provider.GetRequiredService<StateAuthority>());
        });
        services.TryAddSingleton<IBusinessBlockQuery>(provider => provider.GetRequiredService<BusinessBlockScheduler>());
        services.TryAddSingleton<IBusinessBlockDiagnostics>(provider => provider.GetRequiredService<BusinessBlockDiagnosticsRegistry>());
        services.TryAddSingleton<IEtwConsumerThreadObserver, BusinessBlockEtwThreadObserver>();
        services.AddSingleton<IHostedService, BusinessBlockDependencyLifetime>();
        services.TryAddSingleton<NativeEtwLifetimeService>();
        AddHostedBlockDeclaration<NativeEtwLifetimeService>(services, BlockUids.NativeEtwLifetimeService);
        services.AddSingleton(new HostedBusinessBlockAlias(BlockUids.NativeEtwLifetimeService));
        services.AddSingleton<IHostedService>(provider => new NonOwningHostedService<NativeEtwLifetimeService>(
            provider, BlockUids.NativeEtwLifetimeService, constructService: true));
        return services;
    }

    internal static IServiceCollection AddBackendDecisionStateBindings(this IServiceCollection services)
    {
        services.TryAddSingleton<BackendDecisionStateBindings>();
        services.TryAddSingleton<SubscriptionActivityFacts>();
        return services;
    }

    internal static IServiceCollection AddBusinessBlockHostedService<TService>(this IServiceCollection services,
        int id) where TService : class, IHostedService
    {
        services.TryAddSingleton<TService>();
        return services.AddHostedServiceAlias<TService>(id, constructService: true);
    }

    internal static IServiceCollection AddBusinessBlockHostedService<TService>(this IServiceCollection services,
        int id, Func<IServiceProvider, TService> factory) where TService : class, IHostedService
    {
        services.TryAddSingleton(factory);
        return services.AddHostedServiceAlias<TService>(id, constructService: true);
    }

    internal static void RegisterHostedBusinessBlock<TService>(IServiceCollection services,
        int id) where TService : class, IHostedService
    {
        services.AddBusinessBlockController();
        AddHostedBlockDeclaration<TService>(services, id);
    }

    private static void AddHostedBlockDeclaration<TService>(IServiceCollection services, int id)
        where TService : class, IHostedService
    {
        services.AddSingleton(new BusinessBlockRegistration(id,
            provider => provider.GetRequiredService<HostedBusinessBlockRegistry>().GetOrAdd(id, () => provider.GetRequiredService<TService>()))
            { HostedServiceType = typeof(TService) });
    }

    private static IServiceCollection AddEtwBusinessBlocks(this IServiceCollection services)
    {
        services.AddSingleton(new BusinessBlockRegistration(BlockUids.PresentFrameEtwSource,
            provider => provider.GetRequiredService<PresentFrameEtwSource>()));
        services.AddSingleton(new BusinessBlockRegistration(BlockUids.GpuAllocationCollector,
            provider => provider.GetRequiredService<GpuAllocationCollector>()));
        services.AddSingleton(new BusinessBlockRegistration(BlockUids.DxgkrnlVidMmEtwTelemetryZone,
            provider => provider.GetRequiredService<DxgkrnlVidMmEtwTelemetryZone>()));
        return services;
    }
}

internal sealed class BusinessBlockEtwThreadObserver(BusinessBlockDiagnosticsRegistry diagnostics) : IEtwConsumerThreadObserver
{
    public IDisposable RegisterCurrentThread(int blockId, string sessionName)
        => diagnostics.RegisterConsumerThread(blockId, sessionName);
    public void ReportFailure(int blockId, Exception error)
        => diagnostics.ReportFailure(blockId, error, "etw-consumer");
}
