using ResourceManager.App.Domain.ResourceBreakdown;

namespace ResourceManager.App.Endpoints;

public static partial class ResourceManagerEndpointRouteBuilderExtensions
{
    private static bool IsPhysicalPrivateMetric(string id)
        => id.Equals("memory.usage", StringComparison.OrdinalIgnoreCase)
            || id.Equals("process_all_private_working_set_actual", StringComparison.OrdinalIgnoreCase);

    private static ResourceBreakdownSampleRequest SelectPhysicalResidualDetails(
        ResourceBreakdownSampleRequest request, ResourceMonitorScope scope, IReadOnlySet<string> softwareIds)
    {
        if (scope != ResourceMonitorScope.Details || !softwareIds.Any(static id => id.Equals("resource-residual:memory.usage", StringComparison.OrdinalIgnoreCase))
            || !request.MetricIds.Any(IsPhysicalPrivateMetric))
            return request;
        // Its own logical subscription: a bar/basic-metric publication cannot trigger a walk or a details read.
        return request with
        {
            MetricIds = request.MetricIds.Where(static id => !IsPhysicalPrivateMetric(id))
                .Append(PhysicalMemoryResidualFrame.MetricId).ToArray(),
            ProcessMemoryFactIds = request.ProcessMemoryFactIds.Where(static id => !id.Equals("process_all_private_working_set_actual", StringComparison.OrdinalIgnoreCase)).ToArray()
        };
    }
}
