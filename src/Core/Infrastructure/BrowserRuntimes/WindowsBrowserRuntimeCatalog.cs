using ResourceManager.App.Application.BrowserRuntimes;
using ResourceManager.App.Application.Management;
using ResourceManager.App.Domain.BrowserRuntimes;
using ResourceManager.App.Infrastructure.Paths;
using ResourceManager.App.Infrastructure.Settings;
using ResourceManager.Shared.BrowserRuntimes;

namespace ResourceManager.App.Infrastructure.BrowserRuntimes;

public sealed class WindowsBrowserRuntimeCatalog(
    IHostEnvironment environment,
    IManagementInventoryRevisionOwner? inventoryRevisionOwner = null,
    ResourceManager.Shared.BusinessBlocks.State.StateAuthority? authority = null) : IBrowserRuntimeCatalog
{
    public const string InstallComponentId = BrowserRuntimeCatalogIds.InstallComponentId;
    private readonly object sync = new();
    private readonly string packageRoot = PackagePathResolver.ResolvePackageRoot(environment.ContentRootPath);
    private BrowserRuntimeSnapshot? cached;

    public Task<BrowserRuntimeSnapshot> GetSnapshotAsync(
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (sync)
        {
            if (!forceRefresh
                && cached is not null
                // Memory/CPU tier option browser_runtime_catalog_cache_seconds; 0 re-reads every time.
                && DateTimeOffset.UtcNow - cached.CapturedAt < MemoryCpuTierState.Read(authority).BrowserRuntimeSnapshot)
            {
                return Task.FromResult(cached);
            }

            var discovery = BrowserRuntimeDiscovery.Discover(packageRoot);
            cached = new BrowserRuntimeSnapshot(
                Map(discovery.SharedRuntime),
                Map(discovery.BrowserFallback),
                discovery.Candidates.Select(MapRequired).ToArray(),
                InstallComponentId,
                discovery.CapturedAt);
            inventoryRevisionOwner?.ObserveBrowserRuntimeSnapshot(cached);
            return Task.FromResult(cached);
        }
    }

    private static BrowserRuntimeEntry? Map(BrowserRuntimeCandidate? candidate)
        => candidate is null ? null : MapRequired(candidate);

    private static BrowserRuntimeEntry MapRequired(BrowserRuntimeCandidate candidate)
        => new(
            candidate.Id,
            candidate.Name,
            candidate.Kind,
            candidate.Version,
            candidate.ExecutablePath,
            candidate.RuntimeDirectory,
            candidate.Source,
            candidate.NativeWebView2Compatible,
            candidate.Selected);
}
