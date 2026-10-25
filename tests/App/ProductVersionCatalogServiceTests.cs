using System.Net;
using System.Text;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using ResourceManager.App.Infrastructure.Updates;
using ResourceManager.App.Domain.Updates;
using ResourceManager.App.Endpoints;
using System.Text.Json;

namespace Resource_Manager_APP.Tests;

public sealed class ProductVersionCatalogServiceTests
{
    [Fact]
    public async Task OfficialCatalogShowsHistoryAndOnlyFutureVersionsAreSelectable()
    {
        var root = Path.Combine(Path.GetTempPath(), "rm-catalog-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "release-manifest.json"),
                "{\"version\":\"0.2.4\"}");
            var payload = "[" + string.Join(",", new[]
            {
                Release("0.2.5-beta.1", true),
                Release("0.2.4", false),
                Release("0.2.3", false),
                Release("0.2.6-local.1", true)
            }) + "]";
            using var client = new HttpClient(new StubHandler(payload));
            var catalog = new ProductVersionCatalogService(new FakeClientFactory(client), new FakeEnvironment(root));

            var result = await catalog.ReadAsync(CancellationToken.None);

            Assert.Equal("loaded", result.Status);
            Assert.True(result.Complete);
            Assert.Equal("0.2.4", result.InstalledVersion);
            Assert.Equal(["0.2.5-beta.1", "0.2.4", "0.2.3"],
                result.Options.Select(option => option.Version));
            Assert.Equal("preview", result.Options[0].Channel);
            Assert.Equal("0.2", result.Options[0].Series);
            Assert.True(result.Options[0].Selectable);
            Assert.False(result.Options[1].Selectable);
            Assert.False(result.Options[2].Selectable);
            Assert.Equal(ProductVersionEligibility.Eligible, result.Options[0].Eligibility);
            Assert.Equal(ProductVersionEligibility.NotNewer, result.Options[1].Eligibility);
            var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            ResourceManagerEndpointRouteBuilderExtensions.ConfigureJsonSerializerOptions(options);
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(result, options));
            var oldVersion = json.RootElement.GetProperty("options")[1];
            Assert.Equal("notNewer", oldVersion.GetProperty("eligibility").GetString());
            Assert.False(oldVersion.TryGetProperty("unavailableReason", out _));
            Assert.False(json.RootElement.TryGetProperty("error", out _));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task UnknownInstalledVersionProducesEligibilityFactsAndCatalogFailureHidesExceptionText()
    {
        var name = "ResourceManager-0.3.0-win-x64.zip";
        var root = "https://github.com/BIOcanse/Resource-Manager/releases/download/0.3.0/";
        var catalog = new ProductVersionCatalogService(
            _ => Task.FromResult(new GitHubReleaseListing(
                [new("0.3.0", false, null, [new(name, root + name), new(name + ".sha256", root + name + ".sha256")])], true)),
            () => null, () => DateTimeOffset.UnixEpoch);
        var result = await catalog.ReadAsync(CancellationToken.None);
        Assert.False(Assert.Single(result.Options).Selectable);
        Assert.Equal(ProductVersionEligibility.InstalledVersionUnknown, result.Options[0].Eligibility);

        var failedCatalog = new ProductVersionCatalogService(
            _ => throw new InvalidOperationException("private exception detail"),
            () => null, () => DateTimeOffset.UnixEpoch);
        var failed = await failedCatalog.ReadAsync(CancellationToken.None);
        Assert.Equal("private exception detail", failed.Error);
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        ResourceManagerEndpointRouteBuilderExtensions.ConfigureJsonSerializerOptions(options);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(failed, options));
        Assert.Equal("update.catalogUnavailable", json.RootElement.GetProperty("errorCode").GetString());
        Assert.False(json.RootElement.TryGetProperty("error", out _));
    }

    private static string Release(string version, bool prerelease)
    {
        var archive = $"ResourceManager-{version}-win-x64.zip";
        var assetRoot = $"https://github.com/BIOcanse/Resource-Manager/releases/download/{version}/";
        return $$"""
            { "tag_name": "{{version}}", "draft": false, "prerelease": {{prerelease.ToString().ToLowerInvariant()}},
              "assets": [
                { "name": "{{archive}}", "browser_download_url": "{{assetRoot}}{{archive}}" },
                { "name": "{{archive}}.sha256", "browser_download_url": "{{assetRoot}}{{archive}}.sha256" }
              ] }
            """;
    }

    private sealed class StubHandler(string payload) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            });
    }

    private sealed class FakeClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class FakeEnvironment(string root) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "ResourceManager";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
