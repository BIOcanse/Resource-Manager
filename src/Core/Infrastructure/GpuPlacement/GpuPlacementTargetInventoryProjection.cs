using ResourceManager.App.Domain.GpuPlacement;
using ResourceManager.App.Domain.Metrics;

namespace ResourceManager.App.Infrastructure.GpuPlacement;

internal static class GpuPlacementTargetInventoryProjection
{
    internal static GpuPlacementTargetInventory Create(
        HardwareMetricSnapshot? snapshot,
        IEnumerable<string?> referencedTargets)
    {
        var referenced = referencedTargets
            .Where(GpuPlacementTargets.IsExactGpuIndexTarget)
            .Select(static target => target!.Trim().ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (snapshot?.GpuInventory.IsCurrentComplete() != true)
        {
            const string reason = "inventory-unavailable";
            return new GpuPlacementTargetInventory(
                GpuPlacementTargetInventoryStates.Unavailable,
                referenced.Select(target => Missing(target, reason)).ToArray(),
                reason);
        }

        var names = snapshot.Gpus
            .GroupBy(static gpu => gpu.Index)
            .ToDictionary(
                static group => group.Key,
                static group => group.Select(static gpu => gpu.Name.Trim()).FirstOrDefault(static name => name.Length > 0));
        var available = snapshot.GpuInventory.Adapters
            .OrderBy(static adapter => adapter.Index)
            .Select(adapter =>
            {
                var target = $"gpu{adapter.Index}";
                names.TryGetValue(adapter.Index, out var name);
                return new GpuPlacementExactTargetOption(
                    target,
                    name,
                    true);
            })
            .ToList();
        var availableTargets = available
            .Select(static option => option.TargetGpu)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var target in referenced.Where(target => !availableTargets.Contains(target)))
        {
            available.Add(Missing(target, "target-missing"));
        }

        return new GpuPlacementTargetInventory(
            GpuPlacementTargetInventoryStates.Current,
            available,
            null);
    }

    private static GpuPlacementExactTargetOption Missing(string target, string reason) => new(
        target,
        null,
        false,
        reason);
}
