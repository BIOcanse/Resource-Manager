using System.Text.Json;
using ResourceManager.App.Domain.Updates;
using ResourceManager.App.Infrastructure.Paths;
using ResourceManager.Shared.Packages;

namespace ResourceManager.App.Infrastructure.Updates;

public sealed class ProductVersionCatalogService
{
    private readonly Func<CancellationToken, Task<GitHubReleaseListing>> readReleases;
    private readonly Func<string?> readInstalledVersion;
    private readonly Func<DateTimeOffset> utcNow;
    private ProductVersionCatalog? lastGood;

    public ProductVersionCatalogService(IHttpClientFactory httpClientFactory, IHostEnvironment environment)
        : this(async cancellationToken =>
        {
            using var client = httpClientFactory.CreateClient(ProductUpdateSource.HttpClientName);
            return await GitHubReleaseReader.ReadAsync(client, ProductUpdateSource.Owner,
                ProductUpdateSource.Repository, cancellationToken);
        }, () => ReadInstalledVersion(environment.ContentRootPath), () => DateTimeOffset.UtcNow) { }

    public ProductVersionCatalogService(
        Func<CancellationToken, Task<GitHubReleaseListing>> readReleases,
        Func<string?> readInstalledVersion, Func<DateTimeOffset> utcNow)
    {
        this.readReleases = readReleases;
        this.readInstalledVersion = readInstalledVersion;
        this.utcNow = utcNow;
    }

    public async Task<ProductVersionCatalog> ReadAsync(CancellationToken cancellationToken)
    {
        var installedVersion = readInstalledVersion();
        var checkedAt = utcNow();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            var listing = await readReleases(timeout.Token);
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
                if (!ProductUpdateSource.IsReleaseAssetUrl(archive.DownloadUrl, ProductUpdateSource.Owner,
                        ProductUpdateSource.Repository, release.Tag, name)
                    || !ProductUpdateSource.IsReleaseAssetUrl(checksum.DownloadUrl, ProductUpdateSource.Owner,
                        ProductUpdateSource.Repository, release.Tag, name + ".sha256"))
                    continue;
                var selectable = installedKnown && installed is not null && parsed.CompareTo(installed) > 0;
                var eligibility = !installedKnown
                    ? ProductVersionEligibility.InstalledVersionUnknown
                    : !selectable ? ProductVersionEligibility.NotNewer : ProductVersionEligibility.Eligible;
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
                    eligibility), parsed));
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
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            if (lastGood is not null)
                return lastGood with { Status = "stale", Error = exception.Message };
            return new ProductVersionCatalog(
                "resource-manager", "product", installedVersion, "error", false,
                checkedAt, [], exception.Message);
        }
    }

    private static string? ReadInstalledVersion(string contentRootPath)
    {
        var root = PackagePathResolver.ResolvePackageRoot(contentRootPath);
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
