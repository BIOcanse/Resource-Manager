using ResourceManager.App.Domain.ResourceBreakdown;

namespace ResourceManager.App.Endpoints;

public static partial class ResourceManagerEndpointRouteBuilderExtensions
{
    private static bool IsPhysicalPrivateMetric(string id)
        => id.Equals("memory.usage", StringComparison.OrdinalIgnoreCase)
            || id.Equals("process_all_private_working_set_actual", StringComparison.OrdinalIgnoreCase);

    private static ResourceBreakdownSampleRequest SelectPhysicalResidualDetails(
        ResourceBreakdownSampleRequest request, ResourceMonitorScope scope, IReadOnlySet<string> residualDetailMetricIds)
    {
        if (scope != ResourceMonitorScope.Details || !residualDetailMetricIds.Contains(ResourceBreakdownMetricIds.MemoryUsage)
            || !request.MetricIds.Any(IsPhysicalPrivateMetric))
            return request;
        // Preserve the requested counter facts. PFNs remain a separate dataset/item;
        // publishing a counter dataset cannot make the PFN item due.
        return request with
        {
            MetricIds = request.MetricIds.Append(PhysicalMemoryResidualFrame.MetricId)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
        };
    }
}
