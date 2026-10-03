using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using ResourceManager.App.Application.Overlay;
using ResourceManager.App.Domain.Overlay;
using ResourceManager.App.Endpoints;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;

namespace Resource_Manager_APP.Tests;

public sealed class PerformanceOverlayEndpointTests
{
    [Fact]
    public async Task DefaultGetAndPutUseCompleteSettingsAndRouteIdentity()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton<IPerformanceOverlaySettingsStore, MemoryStore>();
        builder.Services.AddSingleton<DashboardMonitoringCatalogState>();
        await using var app = builder.Build();
        typeof(ResourceManagerEndpointRouteBuilderExtensions)
            .GetMethod("MapPerformanceOverlayEndpoints", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [app]);
        await app.StartAsync();
        using var client = new HttpClient
        {
            BaseAddress = new Uri(Assert.Single(app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses))
        };

        var defaults = await client.GetFromJsonAsync<PerformanceOverlaySettings>(
            "/api/performance-overlay/default?softwareId=game");
        Assert.NotNull(defaults);
        Assert.Equal("game", defaults.SoftwareId);
        Assert.Equal("external", defaults.Mode);
        Assert.Equal(14, defaults.FontSizePx);

        var catalog = await client.GetFromJsonAsync<System.Text.Json.JsonElement>(
            "/api/performance-overlay/metrics/catalog");
        Assert.Contains(catalog.EnumerateArray(), item =>
            item.GetProperty("id").GetString() == "target.fps");

        var missing = await client.GetFromJsonAsync<PerformanceOverlaySettings>(
            "/api/performance-overlay/software/game");
        Assert.False(missing!.Enabled);

        using var response = await client.PutAsJsonAsync("/api/performance-overlay/software/game",
            defaults with { SoftwareId = "spoof", Enabled = true, Mode = "injected" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var saved = await response.Content.ReadFromJsonAsync<PerformanceOverlaySettings>();
        Assert.Equal("game", saved!.SoftwareId);
        Assert.True(saved.Enabled);
        Assert.Equal("injected", saved.Mode);
        var retrieved = await client.GetFromJsonAsync<PerformanceOverlaySettings>(
            "/api/performance-overlay/software/game");
        Assert.Equal(saved.SoftwareId, retrieved!.SoftwareId);
        Assert.Equal(saved.Mode, retrieved.Mode);
        Assert.True(retrieved.Enabled);
        await app.StopAsync();
    }

    private sealed class MemoryStore : IPerformanceOverlaySettingsStore
    {
        private readonly Dictionary<string, PerformanceOverlaySettings> software =
            new(StringComparer.OrdinalIgnoreCase);

        public Task<PerformanceOverlaySettingsDocument> GetAsync(CancellationToken cancellationToken)
            => Task.FromResult(new PerformanceOverlaySettingsDocument(1, software.Values.ToArray()));

        public Task<PerformanceOverlaySettings> GetSoftwareAsync(
            string softwareId, CancellationToken cancellationToken)
            => Task.FromResult(software.GetValueOrDefault(softwareId)
                ?? PerformanceOverlaySettings.Normalize(null, softwareId));

        public Task<PerformanceOverlaySettings> SaveSoftwareAsync(
            PerformanceOverlaySettings settings, CancellationToken cancellationToken)
        {
            var normalized = PerformanceOverlaySettings.Normalize(settings, settings.SoftwareId);
            software[normalized.SoftwareId] = normalized;
            return Task.FromResult(normalized);
        }
    }
}
