using ResourceManager.App.Application.Settings;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ResourceManager.App.Application.CpuTopology;
using ResourceManager.App.Application.Optimization.Scheduling;
using ResourceManager.App.Application.RuntimeSpecialization;
using ResourceManager.App.Domain.CpuTopology;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Domain.Settings;
using ResourceManager.App.Endpoints;
using ResourceManager.App.Hosting;
using ResourceManager.App.Hosting.StartupCapabilities;

namespace Resource_Manager_APP.Tests;

public sealed class PerformanceScoreDeliveryEndpointTests
{
    [Theory]
    [InlineData("cpu", "PUT", false)]
    [InlineData("cpu", "DELETE", false)]
    [InlineData("gpu", "PUT", false)]
    [InlineData("gpu", "DELETE", false)]
    [InlineData("cpu", "PUT", true)]
    [InlineData("cpu", "DELETE", true)]
    [InlineData("gpu", "PUT", true)]
    [InlineData("gpu", "DELETE", true)]
    public async Task CommittedScoresExposeDeliveryFailureAndRetainSavedValues(string device, string method, bool publicationThrows)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddResourceManagerApp(["--no-native-ui"], StartupCapabilitySet.Full);
        for (var i = builder.Services.Count - 1; i >= 0; i--)
            if (builder.Services[i].ServiceType == typeof(IHostedService)) builder.Services.RemoveAt(i);
        var scores = new Scores();
        builder.Services.AddSingleton<ICpuCorePerformanceOverrideStore>(scores);
        builder.Services.AddSingleton<IGpuPerformanceScoreOverrideStore>(scores);
        var runtime = new Runtime { Throws = publicationThrows };
        builder.Services.AddSingleton<IRuntimeSpecializationCoordinator>(runtime);
        await using var app = builder.Build();
        typeof(ResourceManagerEndpointRouteBuilderExtensions)
            .GetMethod("MapSystemEndpoints", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [app, StartupCapabilitySet.Full]);
        await app.StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(Assert.Single(app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses)) };
            var path = device == "cpu" ? "/api/cpu/topology/performance-overrides?cpuName=fixture" : "/api/gpu/performance-overrides";
            using var request = new HttpRequestMessage(new HttpMethod(method), path);
            if (method == "PUT") request.Content = JsonContent.Create(device == "cpu"
                ? (object)new CpuCorePerformanceOverrideRequest("fixture", [new(0, 321)])
                : new GpuPerformanceScoreOverrideRequest([new("GPU0", 321)]));
            using var response = await client.SendAsync(request);
            Assert.Equal(publicationThrows ? HttpStatusCode.Conflict : HttpStatusCode.Accepted, response.StatusCode);
            var result = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
            Assert.Equal(publicationThrows ? "savedNotApplied" : "committedWithDeliveryFailures", result["runtimeApplicationDisposition"]!.GetValue<string>());
            Assert.Equal(publicationThrows ? 0 : 1, result["runtimeDeliveryFailureCount"]!.GetValue<int>());
            Assert.Equal("notAttempted", result["startupInterceptionDisposition"]!.GetValue<string>());
            Assert.NotNull(result["policy"]);
            Assert.Equal(1, scores.Writes);
            Assert.Equal($"{device}-performance-overrides-{(method == "PUT" ? "saved" : "reset")}", runtime.Reason);
            if (device == "cpu") Assert.Equal(method == "PUT", scores.Cpu.ScoresByCoreIndex.ContainsKey(0));
            else Assert.Equal(method == "PUT", scores.Gpu.ScoresByGpuId.ContainsKey("GPU0"));
        }
        finally { await app.StopAsync(); }
    }

    private sealed class Runtime : IRuntimeSpecializationCoordinator
    {
        internal bool Throws;
        internal string? Reason;
        public Task<RuntimePlanPublicationResult> RebuildAsync(string reason, CancellationToken cancellationToken)
        {
            Reason = reason;
            if (Throws) throw new IOException("publication failed after save");
            return Task.FromResult(new RuntimePlanPublicationResult(CompiledRuntimePlan.Default, 7,
                [new RuntimePlanDeliveryFailure("fixture", nameof(IOException), "delivery failed")]));
        }
        public Task<CpuBaselineRatioUpdateResult> ApplyCpuBaselineRatioAsync(double? value, CancellationToken token) => throw new NotSupportedException();
        public Task<AppSettingsUpdateResult> ApplyAppSettingsAsync(AppSettings settings, string reason, CancellationToken token) => throw new NotSupportedException();
        public Task<AppSettingsUpdateResult> ApplyAppSettingsPatchAsync(AppSettingsPatchRequest patch, string reason, CancellationToken token) => throw new NotSupportedException();
    }

    private sealed class Scores : ICpuCorePerformanceOverrideStore, IGpuPerformanceScoreOverrideStore
    {
        internal int Writes;
        internal CpuCorePerformanceOverrideResult Cpu = new("fixture", new Dictionary<int, double>(), DateTimeOffset.UtcNow, "fixture");
        internal GpuPerformanceScoreOverrideResult Gpu = new(new Dictionary<string, double>(), DateTimeOffset.UtcNow, "fixture");
        public CpuPerformanceOverrides LoadConfiguration(string cpuName) => new(Cpu.ScoresByCoreIndex, null);
        public IReadOnlyDictionary<int, double> LoadScores(string cpuName) => Cpu.ScoresByCoreIndex;
        public void SaveBaselineRatio(double value) => throw new NotSupportedException();
        public void ResetBaselineRatio() => throw new NotSupportedException();
        public CpuCorePerformanceOverrideResult Save(CpuCorePerformanceOverrideRequest request)
        { Writes++; return Cpu = Cpu with { ScoresByCoreIndex = new Dictionary<int, double> { [0] = 321 } }; }
        public CpuCorePerformanceOverrideResult Reset(string cpuName)
        { Writes++; return Cpu = Cpu with { ScoresByCoreIndex = new Dictionary<int, double>() }; }
        public IReadOnlyDictionary<string, double> LoadScores() => Gpu.ScoresByGpuId;
        public GpuPerformanceScoreOverrideResult Load() => Gpu;
        public GpuPerformanceScoreOverrideResult Save(GpuPerformanceScoreOverrideRequest request)
        { Writes++; return Gpu = Gpu with { ScoresByGpuId = new Dictionary<string, double> { ["GPU0"] = 321 } }; }
        public GpuPerformanceScoreOverrideResult Reset()
        { Writes++; return Gpu = Gpu with { ScoresByGpuId = new Dictionary<string, double>() }; }
    }
}
