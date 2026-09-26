using ResourceManager.App.Application.BrowserRuntimes;
using ResourceManager.App.Domain.BrowserRuntimes;
using ResourceManager.App.Infrastructure.Paths;
using ResourceManager.Shared.BrowserRuntimes;

namespace ResourceManager.App.Infrastructure.BrowserRuntimes;

public sealed class WindowsBrowserRuntimeCatalog(IHostEnvironment environment) : IBrowserRuntimeCatalog
{
    public const string InstallComponentId = "shared-webview2-runtime";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(5);
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
                && DateTimeOffset.UtcNow - cached.CapturedAt < CacheDuration)
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
