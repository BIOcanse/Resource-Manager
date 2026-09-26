using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Domain.ResourceTable;

namespace ResourceManager.App.Domain.RuntimeSpecialization;

public sealed record CompiledMonitoringPlan(
    IReadOnlyList<string> DashboardMetricIds,
    MetricSampleRequest DashboardMetricRequest,
    IReadOnlyList<string> ResourceBarMetricIds,
    IReadOnlyDictionary<string, string> ResourceBarScaleModes,
    IReadOnlyList<string> SoftwareTableColumnIds,
    IReadOnlyList<string> ProcessTableColumnIds,
    IReadOnlyList<string> RegisteredSourceZoneIds,
    CompiledFirmwareProviderPlan FirmwareProviders)
{
    public static CompiledMonitoringPlan Default { get; } = new(
        ["cpu.usage", "memory.usage"],
        MetricSampleRequest.ForIds(["cpu.usage", "memory.usage"]),
        [ResourceBreakdownMetricIds.CpuUsage, ResourceBreakdownMetricIds.MemoryUsage],
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [ResourceBreakdownMetricIds.CpuUsage] = ResourceBreakdownScaleModes.Capacity,
            [ResourceBreakdownMetricIds.MemoryUsage] = ResourceBreakdownScaleModes.Capacity
        },
        [
            ResourceTableColumnIds.Name,
            ResourceTableColumnIds.Status,
            ResourceTableColumnIds.Cpu,
            ResourceTableColumnIds.Memory,
            ResourceTableColumnIds.Disk,
            ResourceTableColumnIds.Network
        ],
        [
            ResourceTableColumnIds.Name,
            ResourceTableColumnIds.ProcessId,
            ResourceTableColumnIds.Status,
            ResourceTableColumnIds.User,
            ResourceTableColumnIds.Architecture,
            ResourceTableColumnIds.Cpu,
            ResourceTableColumnIds.Memory,
            ResourceTableColumnIds.Disk,
            ResourceTableColumnIds.Network
        ],
        [],
        CompiledFirmwareProviderPlan.Default);

    public IReadOnlyList<string> ResolveTableColumnIds(string viewMode)
    {
        return viewMode.Equals(ResourceTableViewModes.Process, StringComparison.OrdinalIgnoreCase)
            ? ProcessTableColumnIds
            : SoftwareTableColumnIds;
    }
}
