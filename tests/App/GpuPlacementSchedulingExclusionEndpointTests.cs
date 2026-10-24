using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ResourceManager.App.Application.GpuPlacement;
using ResourceManager.App.Application.Metrics;
using ResourceManager.App.Application.RuntimeSpecialization;
using ResourceManager.App.Application.Settings;
using ResourceManager.App.Domain.CpuTopology;
using ResourceManager.App.Domain.GpuPlacement;
using ResourceManager.App.Domain.Settings;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Endpoints;
using ResourceManager.App.Infrastructure.GpuPlacement;

namespace Resource_Manager_APP.Tests;

public sealed class GpuPlacementSchedulingExclusionEndpointTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task DefaultAndSavedPolicyCarryTheThreeExclusionsExplicitly(int deliveryMode)
    {
        var root = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "rm-gpu-exclusion-api-" + Guid.NewGuid().ToString("N"))).FullName;
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = root });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var runtime = new Runtime { DeliveryMode = deliveryMode };
        builder.Services.AddSingleton<IRuntimeSpecializationCoordinator>(runtime);
        builder.Services.AddSingleton<IGpuPlacementPolicyStore, JsonGpuPlacementPolicyStore>();
        // The policy routes must not reach the other owners mapped in the same group.
        foreach (var unused in new[]
        {
            typeof(IGpuPlacementProcessHistoryStore), typeof(IGpuLaunchInterceptionRegistry),
            typeof(IGpuLaunchExecutionReportStore), typeof(IGpuPlacementCapabilityReader),
            typeof(IMetricSampler), typeof(GpuLaunchInterceptionReconciler), typeof(IGpuStartupPlacementResolver)
        })
        {
            builder.Services.AddSingleton(unused, _ => throw new InvalidOperationException($"{unused.Name} is outside the policy route."));
        }
        await using var app = builder.Build();
        typeof(ResourceManagerEndpointRouteBuilderExtensions)
            .GetMethod("MapGpuPlacementEndpoints", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [app]);
        await app.StartAsync();
        try
        {
            using var client = new HttpClient
            {
                BaseAddress = new Uri(Assert.Single(app.Services.GetRequiredService<IServer>()
                    .Features.Get<IServerAddressesFeature>()!.Addresses)),
                Timeout = TimeSpan.FromSeconds(10)
            };

            var defaults = (await client.GetFromJsonAsync<JsonObject>(
                "/api/gpu-placement/software/app/policy/default?softwareName=App"))!;
            Assert.False(defaults["cpuSchedulingExcluded"]!.GetValue<bool>());
            Assert.False(defaults["memorySchedulingExcluded"]!.GetValue<bool>());
            Assert.False(defaults["gpuSchedulingExcluded"]!.GetValue<bool>());
            foreach (var removed in new[] { "enabledMode", "schedulingMode", "runtimeSchedulingMode", "explicitSelectionMode" })
            {
                Assert.False(defaults.ContainsKey(removed), removed);
            }

            defaults["cpuSchedulingExcluded"] = true;
            defaults["gpuSchedulingExcluded"] = true;
            using var saved = await client.PutAsJsonAsync("/api/gpu-placement/software/app/policy", defaults);
            Assert.Equal(deliveryMode == 0 ? HttpStatusCode.OK : deliveryMode == 1 ? HttpStatusCode.Accepted : HttpStatusCode.Conflict, saved.StatusCode);
            var envelope = (await saved.Content.ReadFromJsonAsync<JsonObject>())!;
            Assert.Equal(deliveryMode == 0 ? "committedAndApplied" : deliveryMode == 1 ? "committedWithDeliveryFailures" : "savedNotApplied", envelope["runtimeApplicationDisposition"]!.GetValue<string>());
            Assert.Equal(deliveryMode == 1 ? 1 : 0, envelope["runtimeDeliveryFailureCount"]!.GetValue<int>());
            var echoed = envelope["policy"]!;
            Assert.True(echoed["cpuSchedulingExcluded"]!.GetValue<bool>());
            Assert.False(echoed["memorySchedulingExcluded"]!.GetValue<bool>());
            Assert.True(echoed["gpuSchedulingExcluded"]!.GetValue<bool>());
            Assert.Equal(1, runtime.Rebuilds);

            var stored = Assert.Single((await app.Services.GetRequiredService<IGpuPlacementPolicyStore>()
                .GetAsync(CancellationToken.None)).SoftwarePolicies);
            Assert.True(stored.CpuSchedulingExcluded);
            Assert.False(stored.MemorySchedulingExcluded);
            Assert.True(stored.GpuSchedulingExcluded);
        }
        finally
        {
            await app.StopAsync();
            Directory.Delete(root, true);
        }
    }

    private sealed class Runtime : IRuntimeSpecializationCoordinator
    {
        internal int Rebuilds;
        internal int DeliveryMode;
        public Task<RuntimePlanPublicationResult> RebuildAsync(string reason, CancellationToken cancellationToken)
        {
            Rebuilds++;
            if (DeliveryMode == 2) throw new IOException("publish failed");
            return Task.FromResult(new RuntimePlanPublicationResult(CompiledRuntimePlan.Default, 7,
                DeliveryMode == 1 ? [new RuntimePlanDeliveryFailure("test-owner", "IOException", "delivery failed")] : []));
        }
        public Task<CpuBaselineRatioUpdateResult> ApplyCpuBaselineRatioAsync(double? overrideRatio, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AppSettingsUpdateResult> ApplyAppSettingsAsync(AppSettings settings, string reason, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AppSettingsUpdateResult> ApplyAppSettingsPatchAsync(AppSettingsPatchRequest patch, string reason, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
