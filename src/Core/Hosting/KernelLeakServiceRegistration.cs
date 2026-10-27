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
        services.AddSingleton<IKernelLeakReportSource>(provider => new KernelLeakObserver(
            provider.GetRequiredService<IKernelLeakSampleSource>(),
            provider.GetRequiredService<IKernelLeakAttributionReader>(),
            () => provider.GetRequiredService<RuntimePlanProvider>().Current.HostManager.RequirePublished().KernelLeak
                ?? throw new InvalidOperationException("Kernel leak observation plan is unavailable."),
            provider.GetRequiredService<ILogger<KernelLeakObserver>>()));
        return services;
    }
}