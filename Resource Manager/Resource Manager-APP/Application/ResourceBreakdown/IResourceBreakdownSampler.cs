using ResourceManager.App.Domain.ResourceBreakdown;

namespace ResourceManager.App.Application.ResourceBreakdown;

public interface IResourceBreakdownSampler
{
    Task<ResourceBreakdownSnapshot> GetSnapshotAsync(
        ResourceBreakdownSampleRequest request,
        CancellationToken cancellationToken);

    Task<ResourceBreakdownSnapshot> GetSnapshotAsync(
        IReadOnlyList<string> metricIds,
        IReadOnlyDictionary<string, string> scaleModes,
        CancellationToken cancellationToken);

    Task<ResourceBreakdownSnapshot> CaptureSnapshotAsync(
        ResourceBreakdownSampleRequest request,
        CancellationToken cancellationToken);
}
