using ResourceManager.App.Domain.Indexing;

namespace ResourceManager.App.Application.Indexing;

public interface ISoftwareFileIndex
{
    Task<SoftwareFileIndexSnapshot?> GetSnapshotAsync(
        string softwareId,
        CancellationToken cancellationToken,
        Func<string, string?, bool>? canReadPath = null);

    Task<IReadOnlyList<SoftwareFileSearchResult>> SearchAsync(
        string query,
        int limit,
        CancellationToken cancellationToken);

    Task<SoftwareFileIndexStatistics> GetStatisticsAsync(CancellationToken cancellationToken);
}
