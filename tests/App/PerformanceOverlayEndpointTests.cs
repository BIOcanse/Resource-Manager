using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Application.GpuPlacement;
using ResourceManager.App.Application.Overlay;
using ResourceManager.App.Application.Software;
using ResourceManager.App.Domain.GpuPlacement;
using ResourceManager.App.Domain.Overlay;
using ResourceManager.App.Domain.Software;
using ResourceManager.App.Endpoints;
using ResourceManager.App.Infrastructure.GpuPlacement;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;

namespace Resource_Manager_APP.Tests;

public sealed class PerformanceOverlayEndpointTests
{
    [Fact]
    public async Task DefaultGetAndPutUseCompleteSettingsAndRouteIdentity()
    {
        const string executablePath = @"C:\Apps\Overlay\overlay.exe";
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var store = new MemoryStore();
        var registry = new CapturingLaunchRegistry();
        builder.Services.AddSingleton<IPerformanceOverlaySettingsStore>(store);
        builder.Services.AddSingleton(new GpuLaunchInterceptionReconciler(
            new EmptyGpuPolicyStore(), store, new SingleSoftwareRegistryView(executablePath),
            registry, new UnavailableGpuScheduling(),
            NullLogger<GpuLaunchInterceptionReconciler>.Instance));
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
        Assert.Equal(executablePath, Assert.Single(registry.OverlayPaths));

        using var externalResponse = await client.PutAsJsonAsync("/api/performance-overlay/software/game",
            saved with { Mode = "external" });
        Assert.Equal(HttpStatusCode.OK, externalResponse.StatusCode);
        Assert.Empty(registry.OverlayPaths);
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

    private sealed class EmptyGpuPolicyStore : IGpuPlacementPolicyStore
    {
        public Task<GpuPlacementPolicyDocument> GetAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new GpuPlacementPolicyDocument(
                GpuPlacementPolicyDocumentVersions.Current, [], [], DateTimeOffset.UnixEpoch));
        public Task<GpuPlacementSoftwarePolicy> GetOrCreateSoftwarePolicyAsync(
            string softwareId, string softwareName, string? softwareKind,
            CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<GpuPlacementSoftwarePolicy> SaveSoftwarePolicyAsync(
            GpuPlacementSoftwarePolicy policy, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<GpuPlacementProcessPolicy> SaveProcessPolicyAsync(
            GpuPlacementProcessPolicy policy, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class SingleSoftwareRegistryView(string executablePath) : ISoftwareRegistryView
    {
        public Task<IReadOnlyList<SoftwareRecord>> GetSoftwareAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<SoftwareRecord>>(
                [new SoftwareRecord("game", "Game", SoftwareKinds.Game, "Game", "registered",
                    [], [], string.Empty, new SoftwareOperationCapabilities(false, "", "", ""),
                    null, ExecutablePaths: [executablePath])]);

        public Task<IReadOnlyList<SoftwareRecord>> RefreshSoftwareAsync(CancellationToken cancellationToken) =>
            GetSoftwareAsync(cancellationToken);
    }

    private sealed class CapturingLaunchRegistry : IGpuLaunchInterceptionRegistry
    {
        public IReadOnlyCollection<string> OverlayPaths { get; private set; } = [];
        public GpuLaunchInterceptionStatus Apply(GpuPlacementProcessPolicy policy) => throw new NotSupportedException();
        public GpuLaunchInterceptionStatus GetStatus(GpuPlacementProcessPolicy policy) => throw new NotSupportedException();
        public IReadOnlyList<GpuLaunchInterceptionStatus> Reconcile(
            GpuPlacementPolicyDocument document, IReadOnlyCollection<string> overlayExecutablePaths)
        {
            OverlayPaths = overlayExecutablePaths;
            return [];
        }
        public GpuLaunchInterceptionCleanupResult RemoveAllOwnedRules() => throw new NotSupportedException();
    }

    private sealed class UnavailableGpuScheduling : IGpuSchedulingAvailability
    {
        public ValueTask<GpuSchedulingAvailability> EvaluateAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(new GpuSchedulingAvailability(false, null));
    }
}
