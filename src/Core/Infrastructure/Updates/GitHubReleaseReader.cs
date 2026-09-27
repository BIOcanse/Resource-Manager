using System.Net.Http.Headers;
using System.Text.Json;

namespace ResourceManager.App.Infrastructure.Updates;

public sealed record GitHubReleaseAsset(string Name, string DownloadUrl);
public sealed record GitHubPublishedRelease(
    string Tag,
    bool Prerelease,
    DateTimeOffset? PublishedAt,
    IReadOnlyList<GitHubReleaseAsset> Assets);

public sealed record GitHubReleaseListing(IReadOnlyList<GitHubPublishedRelease> Releases, bool Complete);

/// <summary>Reads published releases in pages. A capped result is explicitly incomplete.</summary>
public static class GitHubReleaseReader
{
    private const int PageSize = 100;
    private const int MaxPages = 20;

    public static async Task<GitHubReleaseListing> ReadAsync(
        HttpClient client,
        string owner,
        string repository,
        CancellationToken cancellationToken)
    {
        var releases = new List<GitHubPublishedRelease>();
        for (var page = 1; page <= MaxPages; page++)
        {
            var url = $"https://api.github.com/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repository)}/releases?per_page={PageSize}&page={page}";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd("ResourceManager-VersionCatalog");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("Release listing has an invalid shape.");

            var items = document.RootElement.EnumerateArray().ToArray();
            foreach (var item in items)
            {
                if (item.ValueKind != JsonValueKind.Object
                    || ReadBoolean(item, "draft")
                    || ReadString(item, "tag_name") is not { Length: > 0 } tag)
                    continue;
                var assets = new List<GitHubReleaseAsset>();
                if (item.TryGetProperty("assets", out var assetItems)
                    && assetItems.ValueKind == JsonValueKind.Array)
                {
                    foreach (var asset in assetItems.EnumerateArray())
                    {
                        var name = ReadString(asset, "name");
                        var downloadUrl = ReadString(asset, "browser_download_url");
                        if (name is not null && downloadUrl is not null
                            && Uri.TryCreate(downloadUrl, UriKind.Absolute, out var parsed)
                            && parsed.Scheme == Uri.UriSchemeHttps
                            && parsed.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
                            && parsed.AbsolutePath.StartsWith($"/{owner}/{repository}/releases/download/", StringComparison.OrdinalIgnoreCase))
                            assets.Add(new GitHubReleaseAsset(name, downloadUrl));
                    }
                }
                var published = DateTimeOffset.TryParse(ReadString(item, "published_at"), out var date)
                    ? date : (DateTimeOffset?)null;
                releases.Add(new GitHubPublishedRelease(tag, ReadBoolean(item, "prerelease"), published, assets));
            }
            if (items.Length < PageSize)
                return new GitHubReleaseListing(releases, Complete: true);
        }
        return new GitHubReleaseListing(releases, Complete: false);
    }

    private static string? ReadString(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(property, out var value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString() : null;

    private static bool ReadBoolean(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(property, out var value)
            && value.ValueKind == JsonValueKind.True;
}
