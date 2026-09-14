using ResourceManager.App.Domain.CpuTopology;
using ResourceManager.App.Domain.GpuPlacement;

namespace ResourceManager.App.Application.Optimization.Scheduling;

public static class CpuExclusiveBindingProjection
{
    public static CpuExclusiveBindingSnapshot Create(
        CpuTopologySnapshot topology,
        GpuPlacementPolicyDocument policyDocument,
        DateTimeOffset capturedAt)
    {
        ArgumentNullException.ThrowIfNull(topology);
        ArgumentNullException.ThrowIfNull(policyDocument);

        var bindings = (policyDocument.SoftwarePolicies ?? [])
            .Where(static policy =>
                policy.CpuManualExclusivePositionIds?.Count > 0
                || policy.CpuManualLockedPositionIds?.Count > 0)
            .GroupBy(static policy => policy.SoftwareId, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.OrderByDescending(policy => policy.UpdatedAt).First())
            .Select(policy => new CpuExclusiveBinding(
                policy.SoftwareId,
                policy.SoftwareName,
                NormalizePositionIds(policy.CpuManualExclusivePositionIds),
                CpuPositionIdResolver.ExpandToPhysicalCoreIds(
                    policy.CpuManualExclusivePositionIds,
                    topology),
                NormalizePositionIds(policy.CpuManualLockedPositionIds),
                CpuPositionIdResolver.ExpandToPhysicalCoreIds(
                    policy.CpuManualLockedPositionIds,
                    topology),
                policy.CpuExclusiveLocksAffinity,
                policy.AbsolutePerformanceModeEnabled,
                CpuMaximumOccupancyModes.Normalize(policy.CpuMaximumOccupancyMode),
                policy.UpdatedAt))
            .OrderBy(static binding => binding.SoftwareName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(static binding => binding.SoftwareId, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new CpuExclusiveBindingSnapshot(
            capturedAt,
            topology.CpuName,
            bindings);
    }

    private static IReadOnlyList<string> NormalizePositionIds(IEnumerable<string>? values)
    {
        return (values ?? [])
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
