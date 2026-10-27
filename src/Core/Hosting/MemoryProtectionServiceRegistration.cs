using BlockUids = ResourceManager.App.StateItems.BlockUids;
using ResourceManager.App.Application.MemoryProtection;
using ResourceManager.App.Application.RuntimeSpecialization.BusinessBlocks;
using ResourceManager.App.Infrastructure.MemoryProtection;
using ResourceManager.App.Infrastructure.RuntimeSpecialization.BusinessBlocks;

namespace ResourceManager.App.Hosting;

public static partial class ResourceManagerServiceCollectionExtensions
{
    private static IServiceCollection AddHostMemoryProtection(this IServiceCollection services)
    {
        services.AddSingleton<IHostMemoryProtectionSource, HostMemoryProtectionSource>();
        services.AddSingleton<IHostMemoryProtectionExecutor, WindowsHostMemoryProtectionExecutor>();
        services.AddSingleton<IHostMemoryProtectionReserve, HostMemoryDiskReserves>();
        services.AddSingleton<HostMemoryProtectionService>();
        services.AddSingleton(new BusinessBlockRegistration(BlockUids.HostMemoryProtection,
            provider => provider.GetRequiredService<HostMemoryProtectionService>()));
        return services;
    }
}
