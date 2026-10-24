using System.Net;
using System.Text;
using ResourceManager.App.Infrastructure.Updates;

namespace Resource_Manager_APP.Tests;

public sealed class ProductUpdateSourceTests
{
    private const string Asset = "https://github.com/BIOcanse/Resource-Manager/releases/download/v0.3.0/ResourceManager-0.3.0-win-x64.zip";

    [Theory]
    [InlineData("http://github.com/BIOcanse/Resource-Manager/releases/download/v0.3.0/package.zip")]
    [InlineData("https://github.com/other/Resource-Manager/releases/download/v0.3.0/package.zip")]
    [InlineData("https://github.com/BIOcanse/Resource-Manager-else/releases/download/v0.3.0/package.zip")]
    [InlineData("https://user@github.com/BIOcanse/Resource-Manager/releases/download/v0.3.0/package.zip")]
    [InlineData("https://github.com:8443/BIOcanse/Resource-Manager/releases/download/v0.3.0/package.zip")]
    public void RejectsAssetsOutsideOfficialHttpsRepository(string url)
        => Assert.False(ProductUpdateSource.IsReleaseAssetUrl(url, ProductUpdateSource.Owner, ProductUpdateSource.Repository));

    [Fact]
    public async Task AssetDownloadFollowsValidatedGitHubDeliveryRedirect()
    {
        var visited = new List<string>();
        using var client = new HttpClient(new Handler(request =>
        {
            visited.Add(request.RequestUri!.AbsoluteUri);
            return visited.Count == 1
                ? Redirect("https://release-assets.githubusercontent.com/github-production-release-asset/file?token=signed")
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("archive") };
        }));
        using var response = await ProductUpdateSource.SendAssetAsync(client, Asset, CancellationToken.None);
        Assert.Equal("archive", await response.Content.ReadAsStringAsync());
        Assert.Equal(2, visited.Count);
    }

    [Theory]
    [InlineData("https://github.com/attacker/Resource-Manager/releases/download/v0.3.0/package.zip")]
    [InlineData("https://evil.example/package.zip")]
    [InlineData("http://release-assets.githubusercontent.com/package.zip")]
    [InlineData("https://user@release-assets.githubusercontent.com/package.zip")]
    public async Task AssetDownloadRejectsRedirectBeforeContactingUntrustedDestination(string destination)
    {
        var requests = 0;
        using var client = new HttpClient(new Handler(_ => { requests++; return Redirect(destination); }));
        await Assert.ThrowsAsync<InvalidDataException>(() => ProductUpdateSource.SendAssetAsync(client, Asset, CancellationToken.None));
        Assert.Equal(1, requests);
    }

    [Fact]
    public async Task AssetDownloadRedirectLoopIsBounded()
    {
        var requests = 0;
        using var client = new HttpClient(new Handler(_ => { requests++; return Redirect(Asset); }));
        await Assert.ThrowsAsync<InvalidDataException>(() => ProductUpdateSource.SendAssetAsync(client, Asset, CancellationToken.None));
        Assert.Equal(6, requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReleaseReaderRejectsRepositoryRedirectsIncludingAlreadyFollowedResponse(bool alreadyFollowed)
    {
        using var client = new HttpClient(new Handler(request =>
        {
            if (!alreadyFollowed) return Redirect("https://api.github.com/repos/attacker/Resource-Manager/releases");
            request.RequestUri = new Uri("https://api.github.com/repos/attacker/Resource-Manager/releases");
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request, Content = new StringContent("[]", Encoding.UTF8, "application/json")
            };
        }));
        await Assert.ThrowsAsync<InvalidDataException>(() => GitHubReleaseReader.ReadAsync(
            client, ProductUpdateSource.Owner, ProductUpdateSource.Repository, CancellationToken.None));
    }

    [Fact]
    public async Task InjectedCatalogStillRequiresMatchingOfficialArchiveAndChecksum()
    {
        var now = new DateTimeOffset(2026, 10, 24, 1, 0, 0, TimeSpan.Zero);
        GitHubPublishedRelease Release(string version, bool preview, bool validChecksum = true)
        {
            var name = $"ResourceManager-{version}-win-x64.zip";
            var root = $"https://github.com/BIOcanse/Resource-Manager/releases/download/{version}/";
            return new(version, preview, now,
            [new(name, root + name), new(name + ".sha256", validChecksum ? root + name + ".sha256" : root + "other.sha256")]);
        }
        var catalog = new ProductVersionCatalogService(
            _ => Task.FromResult(new GitHubReleaseListing(
                [Release("0.3.0", false), Release("0.3.1-beta.1", true), Release("0.3.2", false, false)], true)),
            () => "0.2.4", () => now);

        var result = await catalog.ReadAsync(CancellationToken.None);

        Assert.Equal(now, result.CheckedAt);
        Assert.Equal(["0.3.1-beta.1", "0.3.0"], result.Options.Select(option => option.Version));
        Assert.Equal(["preview", "stable"], result.Options.Select(option => option.Channel));
        Assert.All(result.Options, option => Assert.True(option.Selectable));
    }

    private static HttpResponseMessage Redirect(string destination)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Found);
        response.Headers.Location = new Uri(destination);
        return response;
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(send(request));
    }
}
