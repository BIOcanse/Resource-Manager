using ResourceManager.App.Hosting.StartupCapabilities;
using ResourceManager.App.Infrastructure.FreezeReports;
using ResourceManager.App.Infrastructure.RuntimeSpecialization.BusinessBlocks;
using ResourceManager.App.StateItems;

namespace ResourceManager.App.Hosting;

public static partial class ResourceManagerServiceCollectionExtensions
{
    internal static IServiceCollection AddFreezeReports(this IServiceCollection services, StartupCapabilitySet capabilities)
    {
        services.AddSingleton<FreezeReportsHost>();
        if (capabilities.Allows(StartupCapability.MutablePersistence))
        {
            services.AddSingleton(new BusinessBlockRegistration(BlockUids.SystemEventReports,
                provider => new SystemEventReportsBlock(provider.GetRequiredService<FreezeReportsHost>())));
            services.AddSingleton(new BusinessBlockRegistration(BlockUids.FreezeRecorder,
                provider => new FreezeRecorderBlock(provider.GetRequiredService<FreezeReportsHost>())));
        }
        else
        {
            services.AddSingleton(new BusinessBlockDiagnosticRegistration(BlockUids.SystemEventReports));
            services.AddSingleton(new BusinessBlockDiagnosticRegistration(BlockUids.FreezeRecorder));
        }
        return services;
    }
}
