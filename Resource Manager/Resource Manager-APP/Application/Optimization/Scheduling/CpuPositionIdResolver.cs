using ResourceManager.App.Domain.CpuTopology;

namespace ResourceManager.App.Application.Optimization.Scheduling;

public static class CpuPositionIdResolver
{
    public static IReadOnlyList<string> ExpandToPhysicalCoreIds(
        IEnumerable<string>? positionIds,
        CpuTopologySnapshot topology)
    {
        ArgumentNullException.ThrowIfNull(topology);

        var requested = (positionIds ?? [])
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .Select(static id => id.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (requested.Length == 0)
        {
            return [];
        }

        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in requested)
        {
            var ccd = topology.Ccds.FirstOrDefault(ccd => ccd.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (ccd is not null)
            {
                foreach (var coreIndex in ccd.PhysicalCoreIndexes)
                {
                    var core = topology.PhysicalCores.FirstOrDefault(core => core.Index == coreIndex);
                    if (core is not null && !string.IsNullOrWhiteSpace(core.Id))
                    {
                        result.Add(core.Id);
                    }
                }

                continue;
            }

            var physicalCore = topology.PhysicalCores.FirstOrDefault(core => core.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (physicalCore is not null && !string.IsNullOrWhiteSpace(physicalCore.Id))
            {
                result.Add(physicalCore.Id);
            }
        }

        return result
            .OrderBy(id => topology.PhysicalCores.FirstOrDefault(core => core.Id.Equals(id, StringComparison.OrdinalIgnoreCase))?.Index ?? int.MaxValue)
            .ThenBy(static id => id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
