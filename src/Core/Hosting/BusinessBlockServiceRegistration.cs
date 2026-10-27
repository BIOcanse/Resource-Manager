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
        services.TryAddSingleton<BusinessBlockController>(provider => new(
            provider.GetRequiredService<ILogger<BusinessBlockController>>(),
            provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<BusinessBlockOptions>>().Value.HistoryLimit,
#if RM_STATE_NAMES
            StateNames.Name));
#else
            static _ => null));
#endif
        services.TryAddSingleton<BackendStateItems>();
        services.TryAddSingleton<StateAuthority>(provider => provider.GetRequiredService<BackendStateItems>().Authority);
        services.TryAddSingleton<NativeEtwSessionAvailability>();
        services.TryAddSingleton<BusinessBlockDiagnosticsRegistry>();
        services.TryAddSingleton<HostedBusinessBlockRegistry>();
        services.TryAddSingleton<BusinessBlockScheduler>();
        services.TryAddSingleton<IBusinessBlockQuery>(provider => provider.GetRequiredService<BusinessBlockScheduler>());
        services.TryAddSingleton<IBusinessBlockDiagnostics>(provider => provider.GetRequiredService<BusinessBlockDiagnosticsRegistry>());
        services.TryAddSingleton<IEtwConsumerThreadObserver, BusinessBlockEtwThreadObserver>();
        services.AddSingleton<IHostedService, BusinessBlockDependencyLifetime>();
        services.TryAddSingleton<NativeEtwLifetimeService>();
        AddHostedBlockDeclaration<NativeEtwLifetimeService>(services, BlockUids.NativeEtwLifetimeService, []);
        services.AddSingleton<IHostedService>(provider => new NonOwningHostedService<NativeEtwLifetimeService>(
            provider, BlockUids.NativeEtwLifetimeService, constructService: true));
        return services;
    }

    internal static IServiceCollection AddBusinessBlockHostedService<TService>(this IServiceCollection services,
        int id, int[] inputs, int parentId = BlockUids.Program) where TService : class, IHostedService
    {
        services.TryAddSingleton<TService>();
        return services.AddHostedServiceAlias<TService>(id, inputs, constructService: true, parentId: parentId);
    }

    internal static IServiceCollection AddBusinessBlockHostedService<TService>(this IServiceCollection services,
        int id, int[] inputs, Func<IServiceProvider, TService> factory,
        int parentId = BlockUids.Program) where TService : class, IHostedService
    {
        services.TryAddSingleton(factory);
        return services.AddHostedServiceAlias<TService>(id, inputs, constructService: true, parentId: parentId);
    }

    internal static void RegisterHostedBusinessBlock<TService>(IServiceCollection services,
        int id, int[] inputs, int parentId = BlockUids.Program) where TService : class, IHostedService
    {
        services.AddBusinessBlockController();
        AddHostedBlockDeclaration<TService>(services, id, inputs, parentId);
    }

    private static void AddHostedBlockDeclaration<TService>(IServiceCollection services,
        int id, int[] inputs, int parentId = BlockUids.Program) where TService : class, IHostedService
    {
        services.AddSingleton(new BusinessBlockRegistration(id, inputs,
            provider => provider.GetRequiredService<HostedBusinessBlockRegistry>().GetOrAdd(id, () => provider.GetRequiredService<TService>()))
            { HostedServiceType = typeof(TService), ParentId = parentId });
    }

    private static IServiceCollection AddEtwBusinessBlocks(this IServiceCollection services)
    {
        services.AddSingleton(new BusinessBlockRegistration(BlockUids.PresentFrameEtwSource, [],
            provider => provider.GetRequiredService<PresentFrameEtwSource>())
            { ParentId = BlockUids.NativeEtwLifetimeService });
        services.AddSingleton(new BusinessBlockRegistration(BlockUids.GpuAllocationCollector, [],
            provider => provider.GetRequiredService<GpuAllocationCollector>())
            { ParentId = BlockUids.NativeEtwLifetimeService });
        services.AddSingleton(new BusinessBlockRegistration(BlockUids.DxgkrnlVidMmEtwTelemetryZone, [],
            provider => provider.GetRequiredService<DxgkrnlVidMmEtwTelemetryZone>())
            { ParentId = BlockUids.NativeEtwLifetimeService });
        var native = new[]
        {
            (Id: BlockUids.PresentFrameEtwSource, Item: StateItemUids.PresentNativeSessionAvailable),
            (Id: BlockUids.GpuAllocationCollector, Item: StateItemUids.GpuAllocationNativeSessionAvailable),
            (Id: BlockUids.DxgkrnlVidMmEtwTelemetryZone, Item: StateItemUids.VidMmNativeSessionAvailable)
        };
        services.AddSingleton(provider =>
        {
            // The publisher exists before any reader starts; it is independent of block lifetimes.
            _ = provider.GetRequiredService<NativeEtwSessionAvailability>();
            return new BusinessBlockDecisionRegistration(BlockUids.NativeEtwLifetimeService,
                native.SelectMany(value => new StateItemKey[] { new(StateItemUids.BlockDemand, value.Id), value.Item })
                    .Concat([new(StateItemUids.PresentUnfrozen), new(StateItemUids.VidMmUnfrozen)]).ToArray(),
                input => native.Where(value => input.Read<bool>(new(StateItemUids.BlockDemand, value.Id)).GetValueOrDefault(false)
                    && input.Read<bool>(value.Item).GetValueOrDefault(false)
                    && (value.Id == BlockUids.GpuAllocationCollector || IsUnfrozen(input.Read<bool>(value.Id == BlockUids.PresentFrameEtwSource
                        ? StateItemUids.PresentUnfrozen : StateItemUids.VidMmUnfrozen))))
                    .Select(value => value.Id).ToArray());
        });
        return services;
    }

    // The lazy mode owner has not published before its first construction; its initial mode is unfrozen.
    // A stopped owner or failed read does not permit its child.
    private static bool IsUnfrozen(StateRead<bool> mode)
        => mode.HasValue ? mode.Value : mode.NoValueReason == NoValueReason.NotPublished;
}

internal sealed class BusinessBlockEtwThreadObserver(BusinessBlockDiagnosticsRegistry diagnostics) : IEtwConsumerThreadObserver
{
    public IDisposable RegisterCurrentThread(int blockId, string sessionName)
        => diagnostics.RegisterConsumerThread(blockId, sessionName);
    public void ReportFailure(int blockId, Exception error)
        => diagnostics.ReportFailure(blockId, error, "etw-consumer");
}
