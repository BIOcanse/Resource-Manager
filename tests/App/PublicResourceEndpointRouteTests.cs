using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using ResourceManager.App.Application.PublicResources;
using ResourceManager.App.Domain.PublicResources;
using ResourceManager.App.Endpoints;

namespace Resource_Manager_APP.Tests;

public sealed class PublicResourceEndpointRouteTests
{
    [Fact]
    public async Task PublicResourceRoutes_BuildAndMatchWithoutCustomConstraints()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton<IPublicResourceDirectoryQueries>(new EmptyQueries());
        builder.Services.AddSingleton<IHostPublicResourceCapability>(
            new FixedCapability(available: true));
        await using var app = builder.Build();
        app.MapPublicResourceEndpoints();

        await app.StartAsync();
        var addresses = app.Services
            .GetRequiredService<IServer>()
            .Features
            .Get<IServerAddressesFeature>()!
            .Addresses;
        using var client = new HttpClient { BaseAddress = new Uri(Assert.Single(addresses)) };

        using var transport = await client.GetAsync("/api/public/v1/resources/transport");
        using var missing = await client.GetAsync("/api/public/v1/resources/1");

        Assert.True(transport.IsSuccessStatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        await app.StopAsync();
    }

    [Fact]
    public async Task PublicResourceRoutes_ReturnUnavailableUntilAHostPublisherExists()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton<IPublicResourceDirectoryQueries>(new EmptyQueries());
        builder.Services.AddSingleton<IHostPublicResourceCapability>(
            new FixedCapability(available: false));
        await using var app = builder.Build();
        app.MapPublicResourceEndpoints();

        await app.StartAsync();
        var addresses = app.Services
            .GetRequiredService<IServer>()
            .Features
            .Get<IServerAddressesFeature>()!
            .Addresses;
        using var client = new HttpClient { BaseAddress = new Uri(Assert.Single(addresses)) };

        using var capability = await client.GetAsync("/api/public/v1/capability");
        using var catalog = await client.GetAsync("/api/public/v1/resources");
        using var transport = await client.GetAsync("/api/public/v1/resources/transport");
        using var resource = await client.GetAsync("/api/public/v1/resources/1");

        Assert.Equal(HttpStatusCode.OK, capability.StatusCode);
        await using (var body = await capability.Content.ReadAsStreamAsync())
        using (var document = await JsonDocument.ParseAsync(body))
        {
            Assert.Equal(
                HostPublicResourceCapabilityStates.Unavailable,
                document.RootElement.GetProperty("state").GetString());
            Assert.False(document.RootElement.GetProperty("available").GetBoolean());
        }
        Assert.Equal(HttpStatusCode.ServiceUnavailable, catalog.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, transport.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, resource.StatusCode);
        await app.StopAsync();
    }

    private sealed class EmptyQueries : IPublicResourceDirectoryQueries
    {
        public PublicResourceCatalogSnapshot GetCatalog()
            => new(1, 1, [], DateTimeOffset.UtcNow);

        public PublicResourceDescriptor? Find(ulong publicResourceId) => null;

        public PublicResourceTransportSummary GetTransportSummary()
            => new(1, "shared-memory", false, "broker-query-only");
    }

    private sealed class FixedCapability(bool available) : IHostPublicResourceCapability
    {
        public HostPublicResourceCapabilitySnapshot GetCapability() => new(
            available
                ? HostPublicResourceCapabilityStates.Available
                : HostPublicResourceCapabilityStates.Unavailable,
            available,
            available ? "host-publisher-observed" : "host-publisher-not-observed");
    }
}
