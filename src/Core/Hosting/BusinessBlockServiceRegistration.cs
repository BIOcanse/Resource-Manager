using Microsoft.Extensions.DependencyInjection.Extensions;
using ResourceManager.App.Application.RuntimeSpecialization.BusinessBlocks;
using ResourceManager.App.Infrastructure.RuntimeSpecialization.BusinessBlocks;
using ResourceManager.ReadWrite.Windows.Processes.Etw;
using ResourceManager.App.Infrastructure.Telemetry.Etw;

namespace ResourceManager.App.Hosting;

public static partial class ResourceManagerServiceCollectionExtensions
{
    internal static IServiceCollection AddBusinessBlockController(this IServiceCollection services)
    {
        if (services.Any(value => value.ServiceType == typeof(BusinessBlockScheduler))) return services;
        services.AddOptions<BusinessBlockOptions>().BindConfiguration(BusinessBlockOptions.SectionName);
        services.TryAddSingleton<BusinessBlockController>(provider => new(
            provider.GetRequiredService<ILogger<BusinessBlockController>>(),
            provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<BusinessBlockOptions>>().Value.HistoryLimit));
        services.TryAddSingleton<BusinessBlockDiagnosticsRegistry>();
        services.TryAddSingleton<HostedBusinessBlockRegistry>();
        services.TryAddSingleton<BusinessBlockScheduler>();
        services.TryAddSingleton<IBusinessBlockQuery>(provider => provider.GetRequiredService<BusinessBlockScheduler>());
        services.TryAddSingleton<IBusinessBlockDiagnostics>(provider => provider.GetRequiredService<BusinessBlockDiagnosticsRegistry>());
        services.TryAddSingleton<IEtwConsumerThreadObserver, BusinessBlockEtwThreadObserver>();
        services.AddSingleton<IHostedService, BusinessBlockDependencyLifetime>();
        services.TryAddSingleton<NativeEtwLifetimeService>();
        AddHostedBlockDeclaration<NativeEtwLifetimeService>(services, BackendBlockIds.NativeEtwLifetimeService, [], []);
        services.AddSingleton<IHostedService>(provider => new NonOwningHostedService<NativeEtwLifetimeService>(
            provider, BackendBlockIds.NativeEtwLifetimeService, constructService: true));
        return services;
    }

    internal static IServiceCollection AddBusinessBlockHostedService<TService>(this IServiceCollection services,
        string id, string[] inputs, string[] outputs, string parentId = BackendBlockGraph.Program) where TService : class, IHostedService
    {
        services.TryAddSingleton<TService>();
        return services.AddHostedServiceAlias<TService>(id, inputs, outputs, constructService: true, parentId: parentId);
    }

    internal static IServiceCollection AddBusinessBlockHostedService<TService>(this IServiceCollection services,
        string id, string[] inputs, string[] outputs, Func<IServiceProvider, TService> factory,
        string parentId = BackendBlockGraph.Program) where TService : class, IHostedService
    {
        services.TryAddSingleton(factory);
        return services.AddHostedServiceAlias<TService>(id, inputs, outputs, constructService: true, parentId: parentId);
    }

    internal static void RegisterHostedBusinessBlock<TService>(IServiceCollection services,
        string id, string[] inputs, string[] outputs, string parentId = BackendBlockGraph.Program) where TService : class, IHostedService
    {
        services.AddBusinessBlockController();
        AddHostedBlockDeclaration<TService>(services, id, inputs, outputs, parentId);
    }

    private static void AddHostedBlockDeclaration<TService>(IServiceCollection services,
        string id, string[] inputs, string[] outputs, string parentId = BackendBlockGraph.Program) where TService : class, IHostedService
    {
        services.AddSingleton(new BusinessBlockRegistration(id, inputs,
            provider => provider.GetRequiredService<HostedBusinessBlockRegistry>().GetOrAdd(id, () => provider.GetRequiredService<TService>()))
            { Outputs = outputs, HostedServiceType = typeof(TService), ParentId = parentId });
    }

    private static IServiceCollection AddEtwBusinessBlocks(this IServiceCollection services)
    {
        services.AddSingleton(new BusinessBlockRegistration(PresentFrameEtwSource.BusinessBlockId, [],
            provider => provider.GetRequiredService<PresentFrameEtwSource>())
            { ParentId = BackendBlockIds.NativeEtwLifetimeService, Outputs = ["frame-timing", "graphics-api"] });
        services.AddSingleton(new BusinessBlockRegistration(GpuAllocationCollector.BusinessBlockId, [],
            provider => provider.GetRequiredService<GpuAllocationCollector>())
            { ParentId = BackendBlockIds.NativeEtwLifetimeService, Outputs = ["gpu-allocation"] });
        services.AddSingleton(new BusinessBlockRegistration(DxgkrnlVidMmEtwTelemetryZone.BusinessBlockId, [],
            provider => provider.GetRequiredService<DxgkrnlVidMmEtwTelemetryZone>())
            { ParentId = BackendBlockIds.NativeEtwLifetimeService, Outputs = ["gpu-vram-residual"] });
        var native = new[]
        {
            (Id: PresentFrameEtwSource.BusinessBlockId, Fact: PresentFrameEtwSource.AvailabilityFact),
            (Id: GpuAllocationCollector.BusinessBlockId, Fact: GpuAllocationCollector.AvailabilityFact),
            (Id: DxgkrnlVidMmEtwTelemetryZone.BusinessBlockId, Fact: DxgkrnlVidMmEtwTelemetryZone.AvailabilityFact)
        };
        services.AddSingleton(new BusinessBlockDecisionRegistration(BackendBlockIds.NativeEtwLifetimeService,
            native.SelectMany(value => new[] { BusinessBlockScheduler.DemandFact(value.Id), value.Fact }).ToArray(),
            input => native.Where(value => input.Facts[BusinessBlockScheduler.DemandFact(value.Id)].Value
                && input.Facts[value.Fact].Value).Select(value => value.Id).ToArray()));
        return services;
    }
}

internal sealed class BusinessBlockEtwThreadObserver(BusinessBlockDiagnosticsRegistry diagnostics) : IEtwConsumerThreadObserver
{
    public IDisposable RegisterCurrentThread(string blockId, string sessionName)
        => diagnostics.RegisterConsumerThread(Map(blockId), sessionName);
    public void ReportFailure(string blockId, Exception error)
        => diagnostics.ReportFailure(Map(blockId), error, "etw-consumer");
    private static string Map(string blockId) => blockId switch
    {
        "kernel-etw" => BackendBlockIds.KernelEtwSessionBroker,
        "vidmm-etw" => DxgkrnlVidMmEtwTelemetryZone.BusinessBlockId,
        "present-etw" => PresentFrameEtwSource.BusinessBlockId,
        "gpu-allocation-etw" => GpuAllocationCollector.BusinessBlockId,
        _ => throw new ArgumentOutOfRangeException(nameof(blockId), blockId, "Undeclared ETW business block.")
    };
}
