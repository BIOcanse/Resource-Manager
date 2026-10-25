using ResourceManager.App.Domain.ResourceBreakdown;

namespace ResourceManager.App.Application.ResourceBreakdown;

public interface IResourceResidualBreakdownProvider
{
    IReadOnlyList<ResourceProcessSegment> CreateResidualSegments(ResourceResidualBreakdownRequest request);
}

/// <summary>A held GPU VRAM output subscription needs residual evidence independently of reads.</summary>
public interface IGpuVramResidualObservationSource
{
    IDisposable AcquireSubscription(string owner, TimeSpan interval);
}

public sealed record ResourceResidualBreakdownRequest(
    string MetricId,
    double Value,
    double CapacityValue,
    double SystemPercent,
    IReadOnlySet<int> KnownProcessIds);
