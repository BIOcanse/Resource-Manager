using ResourceManager.App.Domain.BrowserRuntimes;

namespace ResourceManager.App.Application.BrowserRuntimes;

public static class BrowserRuntimeCatalogIds
{
    public const string InstallComponentId = "shared-webview2-runtime";
}

public interface IBrowserRuntimeCatalog
{
    Task<BrowserRuntimeSnapshot> GetSnapshotAsync(
        bool forceRefresh,
        CancellationToken cancellationToken);
}
