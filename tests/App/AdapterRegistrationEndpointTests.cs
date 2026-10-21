using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ResourceManager.App.Application.Adaptation;
using ResourceManager.App.Application.Controlled;
using ResourceManager.App.Application.RuntimeSpecialization;
using ResourceManager.App.Application.Settings;
using ResourceManager.App.Domain.Adaptation;
using ResourceManager.App.Domain.CpuTopology;
using ResourceManager.App.Domain.Settings;
using ResourceManager.App.Endpoints;
using ResourceManager.App.Hosting.StartupCapabilities;
using ResourceManager.App.Infrastructure.Adaptation;
using ResourceManager.App.Infrastructure.Security;

namespace Resource_Manager_APP.Tests;

public sealed class AdapterRegistrationEndpointTests
{
    [Fact]
    public async Task ActualRoutePersistsOnlyAttestedRegistrationAndNeverProbesItsOwnService()
    {
        var root = Path.Combine(Path.GetTempPath(), "rm-adapter-api-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var markerBuilder = WebApplication.CreateBuilder();
        markerBuilder.Logging.ClearProviders(); markerBuilder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var marker = markerBuilder.Build();
        var markerRequests = 0;
        marker.MapGet("/marker", () => { markerRequests++; return new { ok = true }; });
        await marker.StartAsync().WaitAsync(TimeSpan.FromSeconds(15));
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = root });
        builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        var runtime = new Runtime();
        builder.Services.AddSingleton<IRuntimeSpecializationCoordinator>(runtime);
        builder.Services.AddSingleton<IControlledSoftwareRegistry, InMemoryControlledSoftwareRegistry>();
        builder.Services.AddSingleton<IAdapterSoftwareRegistry, JsonAdapterSoftwareRegistry>();
        builder.Services.AddTransient<HttpAdapterCallbackSafetyHandler>();
        builder.Services.AddHttpClient<IAdapterResourceMarkerProbe, HttpAdapterResourceMarkerProbe>()
            .ConfigurePrimaryHttpMessageHandler(HttpAdapterRegistrationConnection.CreateHandler)
            .AddHttpMessageHandler<HttpAdapterCallbackSafetyHandler>();
        await using var app = builder.Build();
        app.MapAdapterEndpoints(StartupCapabilitySet.Full);
        await app.StartAsync().WaitAsync(TimeSpan.FromSeconds(15));
        try
        {
            Assert.True(WindowsLoopbackApiAdministratorReader.TryReadProcessIdentity(Environment.ProcessId, out var owner));
            var markerUrl = marker.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            var request = new AdapterSoftwareRegistrationRequest(AdapterRegistrationSchemaVersions.Current, "test.adapter", "test.app", "Test", null,
                [Path.GetDirectoryName(owner.ExecutablePath)!], [new("test", Environment.ProcessId, owner.ExecutablePath)], [],
                new(AdapterResourceMarkerTransports.LoopbackHttp, markerUrl + "/marker"));
            var url = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { BaseAddress = new Uri(url) };
            using var success = await client.PostAsJsonAsync("/api/adapters/register", request);
            Assert.Equal(HttpStatusCode.Created, success.StatusCode);
            Assert.Equal(1, runtime.Rebuilds);
            Assert.Equal(1, markerRequests);
            var registry = app.Services.GetRequiredService<IAdapterSoftwareRegistry>();
            Assert.True(Assert.Single(await registry.GetAllAsync(default)).OwnershipVerified);
            var storage = Path.Combine(root, "Config", "adapted-software.json");
            var original = await File.ReadAllTextAsync(storage);

            using var dead = await client.PostAsJsonAsync("/api/adapters/register", request with { Processes = [new("missing", int.MaxValue, null)] });
            Assert.Equal(HttpStatusCode.BadRequest, dead.StatusCode);
            Assert.Equal(1, markerRequests);
            using var conflict = await client.PostAsJsonAsync("/api/adapters/register", request with { AdapterId = "unrelated.adapter" });
            Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
            Assert.Equal(2, markerRequests);
            using var ownService = await client.PostAsJsonAsync("/api/adapters/register", request with
            { ResourceMarkerEndpoint = new(AdapterResourceMarkerTransports.LoopbackHttp, url + "/api/controlled") });
            Assert.Equal(HttpStatusCode.BadRequest, ownService.StatusCode);
            Assert.Equal(original, await File.ReadAllTextAsync(storage));
            Assert.Equal(1, runtime.Rebuilds);
        }
        finally
        {
            await app.StopAsync().WaitAsync(TimeSpan.FromSeconds(15));
            await marker.StopAsync().WaitAsync(TimeSpan.FromSeconds(15));
            var storage = Path.Combine(root, "Config", "adapted-software.json");
            if (File.Exists(storage)) File.Delete(storage);
            if (Directory.Exists(Path.GetDirectoryName(storage)!)) Directory.Delete(Path.GetDirectoryName(storage)!);
            Directory.Delete(root);
        }
    }

    private sealed class Runtime : IRuntimeSpecializationCoordinator
    {
        internal int Rebuilds;
        public Task<RuntimePlanPublicationResult> RebuildAsync(string reason, CancellationToken cancellationToken)
        { Rebuilds++; return Task.FromResult<RuntimePlanPublicationResult>(null!); }
        public Task<CpuBaselineRatioUpdateResult> ApplyCpuBaselineRatioAsync(double? overrideRatio, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AppSettingsUpdateResult> ApplyAppSettingsAsync(AppSettings settings, string reason, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AppSettingsUpdateResult> ApplyAppSettingsPatchAsync(AppSettingsPatchRequest patch, string reason, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
