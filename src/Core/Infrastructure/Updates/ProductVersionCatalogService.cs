using System.Text.Json;
using ResourceManager.App.Domain.Updates;
using ResourceManager.App.Infrastructure.Paths;
using ResourceManager.Shared.Packages;

namespace ResourceManager.App.Infrastructure.Updates;

public sealed class ProductVersionCatalogService(IHttpClientFactory httpClientFactory, IHostEnvironment environment)
{
    private const string Owner = "BIOcanse";
    private const string Repository = "Resource-Manager";
    private ProductVersionCatalog? lastGood;

    public async Task<ProductVersionCatalog> ReadAsync(CancellationToken cancellationToken)
    {
        var installedVersion = ReadInstalledVersion();
        var checkedAt = DateTimeOffset.UtcNow;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            using var client = httpClientFactory.CreateClient();
            var listing = await GitHubReleaseReader.ReadAsync(client, Owner, Repository, timeout.Token);
            var installedKnown = ReleaseVersion.TryParse(installedVersion, out var installed);
            var entries = new List<(ProductVersionOption Option, ReleaseVersion Parsed)>();
            foreach (var release in listing.Releases)
            {
                if (!ReleaseVersion.TryParse(release.Tag, out var parsed)
                    || parsed is null
                    || release.Tag.Contains("-local", StringComparison.OrdinalIgnoreCase))
                    continue;
                var assetVersion = release.Tag.StartsWith('v') ? release.Tag[1..] : release.Tag;
                var name = $"ResourceManager-{assetVersion}-win-x64.zip";
                var archive = release.Assets.FirstOrDefault(asset => asset.Name == name);
                var checksum = release.Assets.FirstOrDefault(asset => asset.Name == name + ".sha256");
                if (archive is null || checksum is null) continue;
                var selectable = installedKnown && installed is not null && parsed.CompareTo(installed) > 0;
                var reason = !installedKnown
                    ? "无法确认当前安装版本，升级选择已关闭。"
                    : !selectable ? "只能选择高于当前安装版本的发行版。" : null;
                entries.Add((new ProductVersionOption(
                    "version:" + release.Tag,
                    release.Tag,
                    parsed.Series,
                    release.Prerelease || !parsed.Stable ? "preview" : "stable",
                    release.PublishedAt,
                    archive.Name,
                    archive.DownloadUrl,
                    checksum.DownloadUrl,
                    selectable,
                    reason), parsed));
            }
            var ordered = entries.OrderByDescending(item => item.Parsed)
                .Select(item => item.Option).ToArray();
            var result = new ProductVersionCatalog(
                "resource-manager", "product", installedVersion,
                listing.Complete ? "loaded" : "partial", listing.Complete,
                checkedAt, ordered, null);
            if (listing.Complete) lastGood = result;
            return result;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (lastGood is not null)
                return lastGood with { Status = "stale", Error = exception.Message };
            return new ProductVersionCatalog(
                "resource-manager", "product", installedVersion, "error", false,
                checkedAt, [], exception.Message);
        }
    }

    private string? ReadInstalledVersion()
    {
        var root = PackagePathResolver.ResolvePackageRoot(environment.ContentRootPath);
        var manifestPath = Path.Combine(root, "release-manifest.json");
        if (!File.Exists(manifestPath)) return null;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
            var version = document.RootElement.GetProperty("version").GetString();
            return ReleaseVersion.TryParse(version, out _) ? version : null;
        }
        catch (Exception exception) when (exception is IOException or JsonException or KeyNotFoundException)
        {
            return null;
        }
    }
}
