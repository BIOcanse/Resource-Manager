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
        services.AddSingleton(new BusinessBlockRegistration(HostMemoryProtectionService.BlockId,
            [BackendBlockIds.WindowsHardwareMetricSampler, BackendBlockIds.WindowsResourceBreakdownSampler],
            provider => provider.GetRequiredService<HostMemoryProtectionService>()) { Outputs = ["host-memory-protection"] });
        // The parent owns the run decision; the feature publishes enablement, point and reserve facts.
        // All other children keep the existing demand/enablement behavior; no generic control code changes.
        services.AddSingleton(provider =>
        {
            var children = provider.GetServices<BusinessBlockRegistration>()
                .Where(item => item.ParentId == BackendBlockGraph.Program && item.Id != HostMemoryProtectionService.BlockId)
                .Select(item => item.Id).ToArray();
            return new BusinessBlockDecisionRegistration(BackendBlockGraph.Program,
                children.Select(BusinessBlockScheduler.DemandFact).Concat(
                    [HostMemoryProtectionService.EnabledFact, HostMemoryProtectionService.PointFact, HostMemoryProtectionService.ReserveFact]).ToArray(),
                input => children.Where(id => input.Facts[BusinessBlockScheduler.DemandFact(id)].Value)
                    .Concat(input.Facts[HostMemoryProtectionService.EnabledFact].Value && input.Facts[HostMemoryProtectionService.PointFact].Value
                        && input.Facts[HostMemoryProtectionService.ReserveFact].Value
                        ? [HostMemoryProtectionService.BlockId] : Array.Empty<string>()).ToArray());
        });
        return services;
    }
}
