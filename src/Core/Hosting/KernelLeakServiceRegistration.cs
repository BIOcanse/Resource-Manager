using ResourceManager.App.Application.KernelLeak;
using ResourceManager.App.Infrastructure.KernelLeak;
using ResourceManager.App.Infrastructure.RuntimeSpecialization.BusinessBlocks;
using ResourceManager.App.StateItems;

namespace ResourceManager.App.Hosting;

public static partial class ResourceManagerServiceCollectionExtensions
{
    internal static IServiceCollection AddKernelLeakObservation(this IServiceCollection services)
    {
        services.AddSingleton<IKernelLeakSampleSource, KernelLeakProcessSampleSource>();
        services.AddSingleton<IKernelLeakAttributionReader, WindowsKernelLeakAttributionReader>();
        services.AddSingleton<KernelLeakObserver>();
        services.AddSingleton<IKernelLeakReportSource>(provider => provider.GetRequiredService<KernelLeakObserver>());
        services.AddSingleton(new BusinessBlockRegistration(BlockUids.KernelLeakObservation,
            provider => provider.GetRequiredService<KernelLeakObserver>()));
        // This serial host turn only tracks NotObserved report progress. Plan facts own enablement.
        services.AddSerialHostedServiceAlias<KernelLeakObserver>();
        return services;
    }
}
