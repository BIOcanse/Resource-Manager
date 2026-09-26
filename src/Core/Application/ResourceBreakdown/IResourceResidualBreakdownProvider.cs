using ResourceManager.App.Domain.ResourceBreakdown;

namespace ResourceManager.App.Application.ResourceBreakdown;

public interface IResourceResidualBreakdownProvider
{
    IReadOnlyList<ResourceProcessSegment> CreateResidualSegments(ResourceResidualBreakdownRequest request);
}

public sealed record ResourceResidualBreakdownRequest(
    string MetricId,
    string Label,
    double Value,
    double CapacityValue,
    double SystemPercent,
    IReadOnlySet<int> KnownProcessIds);
