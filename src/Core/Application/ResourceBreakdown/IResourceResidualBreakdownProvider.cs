using ResourceManager.App.Domain.ResourceBreakdown;

namespace ResourceManager.App.Application.ResourceBreakdown;

public interface IResourceResidualBreakdownProvider
{
    IReadOnlyList<ResourceProcessSegment> CreateResidualSegments(ResourceResidualBreakdownRequest request);
}

/// <summary>Explicit capture invoked only by a due physical-private-memory sample, never a read.</summary>
public interface IResourceResidualCaptureSource
{
    IResourceResidualBreakdownProvider CaptureResiduals(CancellationToken cancellationToken);
}

public sealed record ResourceResidualBreakdownRequest(
    string MetricId,
    double Value,
    double CapacityValue,
    double SystemPercent,
    IReadOnlySet<int> KnownProcessIds);
