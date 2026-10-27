using ResourceManager.App.Domain.ResourceBreakdown;

namespace ResourceManager.App.Application.ResourceBreakdown;

public interface IResourceBreakdownSampler
{
    IReadOnlyList<ResourceManager.ReadWrite.ProcessFields.ProcessMetricFieldDefinition> ResourceFactFields => [];
    /// <summary>Single subscription fetch: registers named demand, waits for a
    /// new sampling attempt, and releases. It never answers with retained old data.</summary>
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
