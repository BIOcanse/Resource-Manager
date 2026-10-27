using ResourceManager.App.Application.KernelLeak;
using ResourceManager.App.Infrastructure.KernelLeak;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;

namespace ResourceManager.App.Hosting;

public static partial class ResourceManagerServiceCollectionExtensions
{
    internal static IServiceCollection AddKernelLeakObservation(this IServiceCollection services)
    {
        services.AddSingleton<IKernelLeakSampleSource, KernelLeakProcessSampleSource>();
        services.AddSingleton<IKernelLeakAttributionReader, WindowsKernelLeakAttributionReader>();
        services.AddSingleton<KernelLeakObserver>();
        services.AddSingleton<IKernelLeakReportSource>(provider => provider.GetRequiredService<KernelLeakObserver>());
        // Ordinary host lifetime; no new concurrent business block or block registration.
        // The observer waits for the compiled plan publication before taking its own sampling demand.
        services.AddSerialHostedServiceAlias<KernelLeakObserver>();
        return services;
    }
}
