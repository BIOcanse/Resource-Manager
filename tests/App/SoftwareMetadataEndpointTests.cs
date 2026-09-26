using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using ResourceManager.App.Application.SoftwareMetadata;
using ResourceManager.App.Domain.SoftwareMetadata;
using ResourceManager.App.Endpoints;

namespace Resource_Manager_APP.Tests;

public sealed class SoftwareMetadataEndpointTests
{
    [Fact]
    public async Task Route_ValidatesLanguageAndReturnsExactFallbackAndMissingResults()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton<ISoftwareMetadataCatalog>(new FixtureCatalog());
        await using var app = builder.Build();
        app.MapSoftwareMetadataEndpoints();

        await app.StartAsync();
        try
        {
            var addresses = app.Services
                .GetRequiredService<IServer>()
                .Features
                .Get<IServerAddressesFeature>()!
                .Addresses;
            using var client = new HttpClient
            {
                BaseAddress = new Uri(Assert.Single(addresses))
            };

            using var missingLanguage = await client.GetAsync(
                "/api/software/metadata/app-test");
            using var invalidLanguage = await client.GetAsync(
                "/api/software/metadata/app-test?language=invalid");
            using var exact = await client.GetAsync(
                "/api/software/metadata/app-test?language=zh-CN");
            using var fallback = await client.GetAsync(
                "/api/software/metadata/app-test?language=fr-FR");
            using var unknown = await client.GetAsync(
                "/api/software/metadata/app-unknown?language=en-US");

            Assert.Equal(HttpStatusCode.BadRequest, missingLanguage.StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, invalidLanguage.StatusCode);
            Assert.Equal(HttpStatusCode.OK, exact.StatusCode);
            Assert.Equal(HttpStatusCode.OK, fallback.StatusCode);
            Assert.Equal(HttpStatusCode.OK, unknown.StatusCode);

            using var exactDocument = await ReadJsonAsync(exact);
            Assert.True(exactDocument.RootElement.GetProperty("found").GetBoolean());
            Assert.Equal(
                "zh-CN",
                exactDocument.RootElement
                    .GetProperty("metadata")
                    .GetProperty("resolvedLanguage")
                    .GetString());
            Assert.Equal(
                "中文说明",
                exactDocument.RootElement
                    .GetProperty("metadata")
                    .GetProperty("summary")
                    .GetString());

            using var fallbackDocument = await ReadJsonAsync(fallback);
            Assert.Equal(
                "fr-FR",
                fallbackDocument.RootElement
                    .GetProperty("metadata")
                    .GetProperty("requestedLanguage")
                    .GetString());
            Assert.Equal(
                "en-US",
                fallbackDocument.RootElement
                    .GetProperty("metadata")
                    .GetProperty("resolvedLanguage")
                    .GetString());

            using var unknownDocument = await ReadJsonAsync(unknown);
            Assert.False(unknownDocument.RootElement.GetProperty("found").GetBoolean());
            Assert.Equal(
                JsonValueKind.Null,
                unknownDocument.RootElement.GetProperty("metadata").ValueKind);
        }
        finally
        {
            await app.StopAsync();
        }
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response)
    {
        await using var stream = await response.Content.ReadAsStreamAsync();
        return await JsonDocument.ParseAsync(stream);
    }

    private sealed class FixtureCatalog : ISoftwareMetadataCatalog
    {
        public string Version => "1.0.0";

        public bool Contains(string softwareIdentityId)
        {
            return softwareIdentityId.Equals("app-test", StringComparison.Ordinal);
        }

        public ResolvedSoftwareMetadata? Resolve(
            string softwareIdentityId,
            string language)
        {
            if (language.Equals("invalid", StringComparison.Ordinal))
            {
                throw new ArgumentException("Language tag is invalid.", nameof(language));
            }
            if (!Contains(softwareIdentityId))
            {
                return null;
            }

            var resolvedLanguage = language.Equals("zh-CN", StringComparison.Ordinal)
                ? "zh-CN"
                : "en-US";
            return new ResolvedSoftwareMetadata(
                softwareIdentityId,
                language,
                resolvedLanguage,
                resolvedLanguage == "zh-CN" ? "中文说明" : "English summary",
                null,
                "Publisher",
                "https://example.test/app",
                null,
                "MIT",
                null,
                ["utility"],
                ["resource-manager-curation"]);
        }
    }
}
