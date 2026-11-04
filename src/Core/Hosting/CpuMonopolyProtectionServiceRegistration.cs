using ResourceManager.App.Application.CpuMonopolyProtection;
using ResourceManager.App.Application.RuntimeSpecialization.BusinessBlocks;
using ResourceManager.App.Infrastructure.CpuMonopolyProtection;
using ResourceManager.App.Infrastructure.RuntimeSpecialization.BusinessBlocks;
using ResourceManager.App.StateItems;

namespace ResourceManager.App.Hosting;

public static partial class ResourceManagerServiceCollectionExtensions
{
    private static IServiceCollection AddCpuMonopolyProtection(this IServiceCollection services)
    {
        services.AddSingleton<ICpuMonopolyProtectionSource, CpuMonopolyProtectionSource>();
        services.AddSingleton<ICpuMonopolyProtectionExecutor, WindowsCpuMonopolyProtectionExecutor>();
        services.AddSingleton<CpuMonopolyProtectionService>();
        services.AddSingleton(new BusinessBlockRegistration(BlockUids.CpuMonopolyProtection,
            provider => provider.GetRequiredService<CpuMonopolyProtectionService>()));
        return services;
    }
}
