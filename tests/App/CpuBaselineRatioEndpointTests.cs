using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using ResourceManager.App.Application.CpuTopology;
using ResourceManager.App.Application.RuntimeSpecialization;
using ResourceManager.App.Domain.CpuTopology;
using ResourceManager.App.Endpoints;
using ResourceManager.App.Hosting;
using ResourceManager.App.Hosting.StartupCapabilities;
using ResourceManager.App.Infrastructure.CpuTopology;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;

namespace Resource_Manager_APP.Tests;

public sealed class CpuBaselineRatioEndpointTests
{
    [Fact]
    public async Task UnpublishedSettingsAreAJsonNullValueWithoutStartingCompilation()
    {
        await using var fixture = await EndpointFixture.StartAsync(publish: false);
        using var response = await fixture.Client.GetAsync("/api/cpu/baseline-ratio");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("null", await response.Content.ReadAsStringAsync());
        Assert.Equal("application/json", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal(0, fixture.Sampler.TopologyCaptures);
        Assert.Equal(0, fixture.Sampler.UsageCaptures);
    }

    [Fact]
    public async Task HttpSaveReadAndResetUseRealStoreCompilerAndPublicationWithoutUsageSampling()
    {
        await using var fixture = await EndpointFixture.StartAsync();
        var original = await fixture.Client.GetFromJsonAsync<CpuBaselineRatioSettings>("/api/cpu/baseline-ratio");
        Assert.Equal(0.5557, original!.Ratio);
        Assert.Null(original.OverrideRatio);
        var captures = fixture.Sampler.TopologyCaptures;
        Assert.Equal(1, captures);
        var originalMapping = fixture.Plans.Current.HostManager.CpuCoreResidency.PhysicalCoreByLogicalProcessor;
        Assert.Equal(24, originalMapping.Count);
        Assert.All(originalMapping.Values, core => Assert.Equal(1, core.LogicalProcessorShare));
        _ = await fixture.Client.GetFromJsonAsync<CpuBaselineRatioSettings>("/api/cpu/baseline-ratio");
        Assert.Equal(captures, fixture.Sampler.TopologyCaptures);

        fixture.Store.Save(new CpuCorePerformanceOverrideRequest(original.CpuName, [new(0, 900)]));
        using var saved = await fixture.Client.PutAsJsonAsync("/api/cpu/baseline-ratio", new { ratio = 0.75 });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var response = (await saved.Content.ReadFromJsonAsync<CpuBaselineRatioUpdateResult>())!;
        Assert.Equal("applied", response.RuntimeApplicationDisposition);
        Assert.Equal(0.75, response.Settings!.Ratio);
        Assert.Equal(0.75, response.SavedOverrideRatio);
        Assert.Equal(captures + 1, fixture.Sampler.TopologyCaptures);
        Assert.Equal(900, fixture.Plans.Current.HardwareScores.CpuPerformanceScoresByCoreIndex[0]);
        Assert.Equal(0.75, fixture.OpenStore().LoadConfiguration(original.CpuName).BaselineRatio);

        using var reset = await fixture.Client.DeleteAsync("/api/cpu/baseline-ratio");
        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
        var current = (await reset.Content.ReadFromJsonAsync<CpuBaselineRatioUpdateResult>())!.Settings!;
        Assert.Equal(original.DefaultRatio, current.Ratio);
        Assert.Null(current.OverrideRatio);
        Assert.Equal(captures + 2, fixture.Sampler.TopologyCaptures);
        Assert.Equal(900, fixture.OpenStore().LoadScores(original.CpuName)[0]);
        Assert.Equal(0, fixture.Sampler.UsageCaptures);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-0.5)]
    [InlineData(1.1)]
    [InlineData(1e-310)]
    public async Task HttpInvalidRatioDoesNotCompileOrPersist(double ratio)
    {
        await using var fixture = await EndpointFixture.StartAsync();
        var captures = fixture.Sampler.TopologyCaptures;
        using var response = await fixture.Client.PutAsJsonAsync("/api/cpu/baseline-ratio", new { ratio });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(captures, fixture.Sampler.TopologyCaptures);
        Assert.Null(fixture.Store.LoadConfiguration("CPU").BaselineRatio);
    }

    [Fact]
    public async Task CompileFailureReturnsSavedNotAppliedAndKeepsPreviousPublishedSettings()
    {
        await using var fixture = await EndpointFixture.StartAsync();
        var previous = fixture.Plans.Current;
        fixture.Sampler.FailTopology = true;
        using var response = await fixture.Client.PutAsJsonAsync("/api/cpu/baseline-ratio", new { ratio = 0.65 });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var result = (await response.Content.ReadFromJsonAsync<CpuBaselineRatioUpdateResult>())!;
        Assert.Equal("savedNotApplied", result.RuntimeApplicationDisposition);
        Assert.Equal(0.65, result.SavedOverrideRatio);
        Assert.Null(result.Settings);
        Assert.Same(previous, fixture.Plans.Current);
        Assert.Equal(0.65, fixture.OpenStore().LoadConfiguration("CPU").BaselineRatio);
        Assert.Equal(0, fixture.Sampler.UsageCaptures);
    }

    [Fact]
    public async Task PublicationDeliveryFailureCannotReturnSuccessfulApplication()
    {
        await using var fixture = await EndpointFixture.StartAsync();
        fixture.Plans.Published += _ => throw new InvalidOperationException("Consumer rejected publication.");
        using var response = await fixture.Client.PutAsJsonAsync("/api/cpu/baseline-ratio", new { ratio = 0.6 });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var result = (await response.Content.ReadFromJsonAsync<CpuBaselineRatioUpdateResult>())!;
        Assert.Equal("appliedWithDeliveryFailures", result.RuntimeApplicationDisposition);
        Assert.Equal(0.6, result.Settings!.Ratio);
        Assert.Equal(0.6, fixture.OpenStore().LoadConfiguration("CPU").BaselineRatio);
    }

    [Fact]
    public async Task ConcurrentWritesLeavePublishedAndPersistedRatioEqual()
    {
        await using var fixture = await EndpointFixture.StartAsync();
        var tasks = new[] { 0.6, 0.7, 0.8 }.Select(ratio =>
            fixture.Client.PutAsJsonAsync("/api/cpu/baseline-ratio", new { ratio })).ToArray();
        var responses = await Task.WhenAll(tasks);
        foreach (var response in responses)
        {
            using (response) Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        Assert.Equal(fixture.OpenStore().LoadConfiguration("CPU").BaselineRatio,
            fixture.Plans.Current.CpuBaseline!.OverrideRatio);
        Assert.Equal(0, fixture.Sampler.UsageCaptures);
    }

    [Fact]
    public async Task ReadOnlyStartupDoesNotExposeMutationRoutes()
    {
        await using var fixture = await EndpointFixture.StartAsync(readOnly: true);
        using var put = await fixture.Client.PutAsJsonAsync("/api/cpu/baseline-ratio", new { ratio = 0.5 });
        using var delete = await fixture.Client.DeleteAsync("/api/cpu/baseline-ratio");
        Assert.Equal(HttpStatusCode.MethodNotAllowed, put.StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, delete.StatusCode);
        Assert.Null(fixture.Store.LoadConfiguration("CPU").BaselineRatio);
    }

    private sealed class EndpointFixture(WebApplication app, string root, CountingSampler sampler) : IAsyncDisposable
    {
        public HttpClient Client { get; } = new()
        {
            BaseAddress = new Uri(Assert.Single(app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses)),
            Timeout = TimeSpan.FromSeconds(20)
        };

        public CountingSampler Sampler => sampler;
        public RuntimePlanProvider Plans => app.Services.GetRequiredService<RuntimePlanProvider>();
        public ICpuCorePerformanceOverrideStore Store => app.Services.GetRequiredService<ICpuCorePerformanceOverrideStore>();
        public JsonCpuCorePerformanceOverrideStore OpenStore() => new(app.Environment);

        public static async Task<EndpointFixture> StartAsync(bool readOnly = false, bool publish = true)
        {
            var root = Path.Combine(Path.GetTempPath(), $"rm-baseline-api-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = root });
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            var capabilities = readOnly ? StartupCapabilitySet.NormalReadOnly
                : new StartupCapabilitySet("cpu-baseline-test", StartupCapability.RuntimeEffectOwners | StartupCapability.MutablePersistence);
            builder.Services.AddResourceManagerApp(["--no-native-ui"], capabilities);
            builder.Services.RemoveAll<IHostedService>();
            var sampler = new CountingSampler();
            builder.Services.AddSingleton<ICpuTopologySampler>(sampler);
            var app = builder.Build();
            try
            {
                typeof(ResourceManagerEndpointRouteBuilderExtensions)
                    .GetMethod("MapCpuBaselineRatioEndpoints", BindingFlags.Static | BindingFlags.NonPublic)!
                    .Invoke(null, [app, capabilities]);
                await app.StartAsync();
                if (publish)
                {
                    await app.Services.GetRequiredService<IRuntimeSpecializationCoordinator>()
                        .RebuildAsync("test-startup", CancellationToken.None);
                }
                return new EndpointFixture(app, root, sampler);
            }
            catch
            {
                await app.DisposeAsync();
                Directory.Delete(root, recursive: true);
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await app.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await app.DisposeAsync();
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class CountingSampler : ICpuTopologySampler
    {
        public int TopologyCaptures { get; private set; }
        public int UsageCaptures { get; private set; }
        public bool FailTopology { get; set; }

        public CpuTopologySnapshot CaptureSnapshot()
        {
            UsageCaptures++;
            throw new InvalidOperationException("CPU baseline configuration must not request usage sampling.");
        }

        public CpuTopologySnapshot CaptureTopology()
        {
            TopologyCaptures++;
            if (FailTopology) throw new InvalidOperationException("Topology configuration unavailable.");
            return CpuBaselineRatioCatalogTests.CreateTopology("Intel Core i9-14900K", "Intel", [24]);
        }
    }
}
