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
            [BlockUids.WindowsHardwareMetricSampler, BlockUids.WindowsResourceBreakdownSampler],
            provider => provider.GetRequiredService<HostMemoryProtectionService>()));
        // The parent owns the run decision; the feature publishes enablement, point and reserve facts.
        // All other children keep the existing demand/enablement behavior; no generic control code changes.
        services.AddSingleton(provider =>
        {
            var children = provider.GetServices<BusinessBlockRegistration>()
                .Where(item => item.ParentId == BlockUids.Program && item.Id != BlockUids.HostMemoryProtection)
                .Select(item => item.Id).ToArray();
            return new BusinessBlockDecisionRegistration(BlockUids.Program,
                children.Select(BusinessBlockScheduler.DemandFact).Concat(
                    [HostMemoryProtectionService.EnabledFact, HostMemoryProtectionService.PointFact, HostMemoryProtectionService.ReserveFact]).ToArray(),
                input => children.Where(id => input.Facts[BusinessBlockScheduler.DemandFact(id)].Value)
                    .Concat(input.Facts[HostMemoryProtectionService.EnabledFact].Value && input.Facts[HostMemoryProtectionService.PointFact].Value
                        && input.Facts[HostMemoryProtectionService.ReserveFact].Value
                        ? [BlockUids.HostMemoryProtection] : Array.Empty<int>()).ToArray());
        });
        return services;
    }
}
