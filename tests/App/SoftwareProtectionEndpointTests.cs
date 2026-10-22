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
        var protection = new OptimizationProtectionService(store, new Registry());
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

        Assert.Empty((await client.GetFromJsonAsync<ProtectedOptimizationTarget[]>(
            "/api/optimization/protection"))!);
        using var enabled = await client.PutAsJsonAsync(
            "/api/optimization/protection/software/app", new SoftwareProtectionRequest(true));
        Assert.Equal(HttpStatusCode.OK, enabled.StatusCode);
        var selected = (await enabled.Content.ReadFromJsonAsync<ProtectedOptimizationTarget>())!;
        Assert.Equal("app", selected.SoftwareId);
        Assert.Equal(OptimizationProtectionLevels.Level2NoOptimization, selected.ProtectionLevel);
        var targets = (await client.GetFromJsonAsync<ProtectedOptimizationTarget[]>(
            "/api/optimization/protection"))!;
        Assert.Equal(selected, Assert.Single(targets));
        Assert.Equal(selected, Assert.Single(await new OptimizationProtectionService(store, new Registry())
            .GetProtectedTargetsAsync(default)));

        using var unknown = await client.PutAsJsonAsync(
            "/api/optimization/protection/software/missing", new SoftwareProtectionRequest(false));
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Single(store.Targets);

        using var disabled = await client.PutAsJsonAsync(
            "/api/optimization/protection/software/app", new SoftwareProtectionRequest(false));
        Assert.Equal(HttpStatusCode.OK, disabled.StatusCode);
        Assert.Empty((await client.GetFromJsonAsync<ProtectedOptimizationTarget[]>(
            "/api/optimization/protection"))!);
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
                [new("app", "Fixture", "General", "General", "ready", [], [], "",
                    new(false, "", "", ""), null)]);
        public Task<IReadOnlyList<SoftwareRecord>> RefreshSoftwareAsync(CancellationToken token)
            => GetSoftwareAsync(token);
    }
}
