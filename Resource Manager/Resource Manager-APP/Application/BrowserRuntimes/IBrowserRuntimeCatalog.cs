using ResourceManager.App.Domain.BrowserRuntimes;

namespace ResourceManager.App.Application.BrowserRuntimes;

public interface IBrowserRuntimeCatalog
{
    Task<BrowserRuntimeSnapshot> GetSnapshotAsync(
        bool forceRefresh,
        CancellationToken cancellationToken);
}
