using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Application.Software;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Domain.Software;
using ResourceManager.App.Endpoints;
using ResourceManager.App.Infrastructure.Optimization;
using ResourceManager.App.Infrastructure.Security;

namespace Resource_Manager_APP.Tests;

public sealed class SoftwareProtectionEndpointTests
{
    [Fact]
    public async Task MappedOptimizationGroupPersistsProtectionWithoutRevivingLegacyRoutes()
    {
        var store = new Store();
        var protection = new OptimizationProtectionService(store, new Registry(), FixedSoftwarePolicyProfileProvider.Empty);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<IOptimizationProtectionService>(protection);
        // Other mapped routes must not be called by this protection request path.
        builder.Services.AddSingleton<IHostManagerReportService>(_ =>
            throw new InvalidOperationException("Protection must use its registered owner."));
        builder.Services.AddSingleton<IHostManagerSmartCoordinator>(_ =>
            throw new InvalidOperationException("Protection must not execute the scheduler."));
        await using var app = builder.Build();
        typeof(ResourceManagerEndpointRouteBuilderExtensions)
            .GetMethod("MapOptimizationEndpoints", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [app, LoopbackApiPipelineMode.StandardUser]);
        await app.StartAsync();
        using var client = new HttpClient
        {
            BaseAddress = new Uri(Assert.Single(app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses)),
            Timeout = TimeSpan.FromSeconds(10)
        };

        Assert.Empty(ProtectionTestEntries.User((await client.GetFromJsonAsync<ProtectedOptimizationTarget[]>(
            "/api/optimization/protection"))!));
        using var enabled = await client.PutAsJsonAsync(
            "/api/optimization/protection/software/app", new SoftwareProtectionRequest(true));
        Assert.Equal(HttpStatusCode.OK, enabled.StatusCode);
        var selected = (await enabled.Content.ReadFromJsonAsync<ProtectedOptimizationTarget>())!;
        Assert.Equal("app", selected.SoftwareId);
        Assert.Equal(OptimizationProtectionLevels.NoFreeze, selected.ProtectionLevel);
        var targets = (await client.GetFromJsonAsync<ProtectedOptimizationTarget[]>(
            "/api/optimization/protection"))!;
        Assert.Equal(selected, Assert.Single(ProtectionTestEntries.User(targets)));
        Assert.Equal(selected, Assert.Single(ProtectionTestEntries.User(await new OptimizationProtectionService(store, new Registry(), FixedSoftwarePolicyProfileProvider.Empty)
            .GetProtectedTargetsAsync(default))));

        using var unknown = await client.PutAsJsonAsync(
            "/api/optimization/protection/software/missing", new SoftwareProtectionRequest(false));
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Single(store.Targets);

        using var disabled = await client.PutAsJsonAsync(
            "/api/optimization/protection/software/app", new SoftwareProtectionRequest(false));
        Assert.Equal(HttpStatusCode.OK, disabled.StatusCode);
        Assert.Empty(ProtectionTestEntries.User((await client.GetFromJsonAsync<ProtectedOptimizationTarget[]>(
            "/api/optimization/protection"))!));
        Assert.Empty(store.Targets);
        foreach (var legacy in new[]
        {
            "/api/optimization/trust", "/api/optimization/protection/placement/records"
        })
        {
            using var absent = await client.GetAsync(legacy);
            Assert.Equal(HttpStatusCode.NotFound, absent.StatusCode);
        }
        await app.StopAsync();
    }

    [Fact]
    public async Task ProtectionListChangesAndRestoresDefaultsThroughEntryRoutes()
    {
        var store = new Store();
        var profile = new SoftwarePolicyProfile(
            new Dictionary<string, SoftwarePolicyGroup>(StringComparer.OrdinalIgnoreCase)
            {
                ["meeting"] = new("meeting", "Meeting", null, []) { NoFreeze = true }
            },
            new Dictionary<string, SoftwarePolicyAssignment>(StringComparer.OrdinalIgnoreCase)
            {
                ["app"] = new("app", "Fixture", "meeting", [])
            });
        var protection = new OptimizationProtectionService(store, new Registry(), new FixedSoftwarePolicyProfileProvider(profile));
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<IOptimizationProtectionService>(protection);
        builder.Services.AddSingleton<IHostManagerReportService>(_ =>
            throw new InvalidOperationException("Protection must use its registered owner."));
        builder.Services.AddSingleton<IHostManagerSmartCoordinator>(_ =>
            throw new InvalidOperationException("Protection must not execute the scheduler."));
        await using var app = builder.Build();
        typeof(ResourceManagerEndpointRouteBuilderExtensions)
            .GetMethod("MapOptimizationEndpoints", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [app, LoopbackApiPipelineMode.StandardUser]);
        await app.StartAsync();
        using var client = new HttpClient
        {
            BaseAddress = new Uri(Assert.Single(app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses)),
            Timeout = TimeSpan.FromSeconds(10)
        };

        var list = (await client.GetFromJsonAsync<ProtectedOptimizationTarget[]>("/api/optimization/protection"))!;
        var catalog = Assert.Single(list, item => item.ProtectionSource == OptimizationProtectionSources.SoftwareCatalog);
        Assert.Equal(("app", OptimizationProtectionLevels.NoFreeze, "meeting"), (catalog.SoftwareId, catalog.ProtectionLevel, catalog.PolicyGroupId));
        var services = Assert.Single(list, item => item.SoftwareKind == SoftwareKinds.WindowsService);
        Assert.Equal(OptimizationProtectionSources.SystemDefault, services.ProtectionSource);

        // Switch the Windows service default off from the list; it is stored and replaces the default.
        using var off = await client.PutAsJsonAsync(
            $"/api/optimization/protection/entries/{services.Id}/level", new OptimizationProtectionLevelRequest(0));
        Assert.Equal(HttpStatusCode.OK, off.StatusCode);
        var changed = (await off.Content.ReadFromJsonAsync<ProtectedOptimizationTarget>())!;
        Assert.Equal((services.Id, OptimizationProtectionSources.ProtectionList, OptimizationProtectionLevels.None),
            (changed.Id, changed.ProtectionSource, changed.ProtectionLevel));
        Assert.Equal(((int?)OptimizationProtectionLevels.NoFreeze, OptimizationProtectionSources.SystemDefault),
            (changed.DefaultProtectionLevel, changed.DefaultProtectionSource));
        Assert.Equal(services.Id, Assert.Single(store.Targets).Id);
        list = (await client.GetFromJsonAsync<ProtectedOptimizationTarget[]>("/api/optimization/protection"))!;
        Assert.Equal(changed, Assert.Single(list, item => item.Id == services.Id));

        using var levelTwo = await client.PutAsJsonAsync(
            $"/api/optimization/protection/entries/{services.Id}/level", new OptimizationProtectionLevelRequest(2));
        Assert.Equal(HttpStatusCode.BadRequest, levelTwo.StatusCode);
        Assert.Contains("optimization-protection-level-invalid", await levelTwo.Content.ReadAsStringAsync());
        using var unknown = await client.PutAsJsonAsync(
            "/api/optimization/protection/entries/missing/level", new OptimizationProtectionLevelRequest(1));
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);

        // A default itself cannot be removed; removing the user's entry restores the default.
        using var removeDefault = await client.DeleteAsync($"/api/optimization/protection/entries/{catalog.Id}");
        Assert.Equal(HttpStatusCode.NotFound, removeDefault.StatusCode);
        using var restore = await client.DeleteAsync($"/api/optimization/protection/entries/{services.Id}");
        Assert.Equal(HttpStatusCode.NoContent, restore.StatusCode);
        Assert.Empty(store.Targets);
        list = (await client.GetFromJsonAsync<ProtectedOptimizationTarget[]>("/api/optimization/protection"))!;
        Assert.Equal(services, Assert.Single(list, item => item.Id == services.Id));

        // The software switch turns a catalog default off with a stored entry at 0.
        using var softwareOff = await client.PutAsJsonAsync(
            "/api/optimization/protection/software/app", new SoftwareProtectionRequest(false));
        var softwareEntry = (await softwareOff.Content.ReadFromJsonAsync<ProtectedOptimizationTarget>())!;
        Assert.Equal((catalog.Id, OptimizationProtectionSources.SoftwareDetail, OptimizationProtectionLevels.None),
            (softwareEntry.Id, softwareEntry.ProtectionSource, softwareEntry.ProtectionLevel));
        Assert.Equal(OptimizationProtectionSources.SoftwareCatalog, softwareEntry.DefaultProtectionSource);
        await app.StopAsync();
    }

    private sealed class Store : IOptimizationProtectionStore
    {
        internal IReadOnlyList<ProtectedOptimizationTarget> Targets = [];
        public Task<IReadOnlyList<ProtectedOptimizationTarget>> LoadAsync(CancellationToken token)
            => Task.FromResult(Targets);
        public Task SaveAsync(IReadOnlyList<ProtectedOptimizationTarget> targets, CancellationToken token)
        {
            Targets = targets.ToArray();
            return Task.CompletedTask;
        }
    }

    private sealed class Registry : ISoftwareRegistryView
    {
        public Task<IReadOnlyList<SoftwareRecord>> GetSoftwareAsync(CancellationToken token)
            => Task.FromResult<IReadOnlyList<SoftwareRecord>>(
                [new("app", "Fixture", "other", "ready", [], [],
                    new(false, "None", "missing_uninstall_entry"), null)]);
        public Task<IReadOnlyList<SoftwareRecord>> RefreshSoftwareAsync(CancellationToken token)
            => GetSoftwareAsync(token);
    }
}
