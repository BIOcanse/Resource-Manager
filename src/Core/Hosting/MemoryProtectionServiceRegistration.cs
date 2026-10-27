using ResourceManager.App.StateItems;
using ResourceManager.Shared.BusinessBlocks.State;
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
                children.Select(id => new StateItemKey(StateItemUids.BlockDemand, id)).Concat(
                    [new(StateItemUids.HostMemoryProtectionEnabled), new(StateItemUids.MemoryProtectionNormalRunningPointExists), new(StateItemUids.MemoryProtectionReserveBuilt)]).ToArray(),
                input => children.Where(id => input.Read<bool>(new(StateItemUids.BlockDemand, id)).GetValueOrDefault(false))
                    .Concat(input.Read<bool>(StateItemUids.HostMemoryProtectionEnabled).GetValueOrDefault(false) && input.Read<bool>(StateItemUids.MemoryProtectionNormalRunningPointExists).GetValueOrDefault(false)
                        && input.Read<bool>(StateItemUids.MemoryProtectionReserveBuilt).GetValueOrDefault(false)
                        ? [BlockUids.HostMemoryProtection] : Array.Empty<int>()).ToArray());
        });
        return services;
    }
}
