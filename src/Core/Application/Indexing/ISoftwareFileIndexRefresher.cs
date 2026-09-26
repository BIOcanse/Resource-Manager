using ResourceManager.App.Domain.Indexing;

namespace ResourceManager.App.Application.Indexing;

public interface ISoftwareFileIndexRefresher
{
    Task<SoftwareFileIndexSnapshot> GetOrRefreshAsync(
        SoftwareFileIndexRequest request,
        TimeSpan maximumAge,
        bool forceRefresh,
        CancellationToken cancellationToken);
}
