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
using ResourceManager.App.Application.Software;
using ResourceManager.App.Domain.GpuPlacement;
using ResourceManager.App.Domain.Software;
using ResourceManager.App.Endpoints;
using ResourceManager.App.Infrastructure.GpuPlacement;
using ResourceManager.App.Infrastructure.Security;
using Microsoft.AspNetCore.Http;

namespace Resource_Manager_APP.Tests;

public sealed class PerformanceOverlayEndpointTests
{
    [Fact]
    public async Task GenericStartupSelectionUsesRouteIdentityAndAdmittedModuleIds()
    {
        using var files = new StartupModuleTestCatalog();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        TrustedAccessEndpointTestAuthentication.Add(builder.Services, "read", "machine.write", "injection.configure");
        builder.Services.AddSingleton<ILoopbackApiAdministratorReader>(new Administrator());
        builder.Services.ConfigureHttpJsonOptions(options => ResourceManagerEndpointRouteBuilderExtensions.ConfigureJsonSerializerOptions(options.SerializerOptions));
        var store = new JsonStartupModuleSettingsStore(files, files.Catalog);
        var registry = new CapturingLaunchRegistry();
        var software = new SingleSoftwareRegistryView(files.Image);
        builder.Services.AddSingleton(files.Catalog);
        builder.Services.AddSingleton<IStartupModuleSettingsStore>(store);
        builder.Services.AddSingleton<ISoftwareRegistryView>(software);
        builder.Services.AddSingleton(new GpuLaunchInterceptionReconciler(new EmptyGpuPolicyStore(), store,
            files.Catalog, software, registry, new UnavailableGpuScheduling(), NullLogger<GpuLaunchInterceptionReconciler>.Instance));
        await using var app = TrustedAccessEndpointTestAuthentication.Build(builder);
        typeof(ResourceManagerEndpointRouteBuilderExtensions).GetMethod("MapStartupModuleEndpoints", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null,
            [app, ResourceManager.App.Hosting.StartupCapabilities.StartupCapabilitySet.Full]);
        app.MapApiNotFoundEndpoint();
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(Assert.Single(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses)) };
        TrustedAccessEndpointTestAuthentication.Apply(client, app.Services);
        var initial = await client.GetFromJsonAsync<StartupModuleSelection>("/api/startup-modules/software/game");
        Assert.Empty(initial!.ModuleIds);
        using var saved = await client.PutAsJsonAsync("/api/startup-modules/software/game", new StartupModuleSelection("spoofed", ["fixture-module"]));
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var value = await saved.Content.ReadFromJsonAsync<StartupModuleSelection>();
        Assert.Equal("game", value!.SoftwareId);
        Assert.Equal(["fixture-module"], value.ModuleIds);
        Assert.Equal([files.Image], registry.OverlayPaths);
        using var presentation = await client.PutAsJsonAsync("/api/startup-modules/software/game",
            new { softwareId = "game", moduleIds = new[] { "fixture-module" }, fontSize = 24 });
        Assert.Equal(HttpStatusCode.BadRequest, presentation.StatusCode);
        Assert.Equal(["fixture-module"], Assert.Single((await store.GetAsync(default)).Software).ModuleIds);
        using var invalid = await client.PutAsJsonAsync("/api/startup-modules/software/game", new StartupModuleSelection("game", ["unadmitted"]));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        using var unknown = await client.PutAsJsonAsync("/api/startup-modules/software/missing", new StartupModuleSelection("game", []));
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        using var removed = await client.PutAsJsonAsync("/api/startup-modules/software/game", new StartupModuleSelection("game", []));
        Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
        Assert.Empty(registry.OverlayPaths);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/performance-overlay/software/game")).StatusCode);
        await app.StopAsync();
    }

    private sealed class Administrator : ILoopbackApiAdministratorReader
    {
        public bool IsAdministrator(HttpContext context) => true;
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
                [new SoftwareRecord("game", "Game", SoftwareKinds.Game, "registered",
                    [], [], new SoftwareOperationCapabilities(false, "None", "missing_uninstall_entry"),
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
