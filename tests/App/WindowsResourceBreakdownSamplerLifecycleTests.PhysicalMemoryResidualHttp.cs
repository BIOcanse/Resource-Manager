using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Application.ResourceBreakdown;
using ResourceManager.App.Application.RuntimeSpecialization;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Infrastructure.ResourceBreakdown;
using ResourceManager.App.Endpoints;

namespace Resource_Manager_APP.Tests;

public sealed partial class WindowsResourceBreakdownSamplerLifecycleTests
{
    [Theory]
    [InlineData("ids=memory.usage", "memory.usage")]
    [InlineData("measures=process_all_private_working_set_actual", "process_all_private_working_set_actual")]
    public async Task RealWindowsResidualDetailsHttpPublishesCountersWithoutRequiringMemoryUsageId(
        string selection, string metric)
    {
        // Real Windows memory/process/counter/PFN readers, native subscription owner,
        // production endpoints and Kestrel. No installed service or UI is involved.
        using var fixture = new MetricSnapshotOwnerTestFixture.Fixture(MetricSnapshotOwnerTestFixture.Topology([]));
        var memory = new WindowsMemoryMonitoringZone();
        using var hardware = new WindowsHardwareMetricSampler(new WindowsCpuMonitoringZone(), memory,
            null!, null!, null!, null!, null!, null!, null!, null!, null!, new AmdIntegratedGpuSensorRoute(),
            null!, fixture.Owner, fixture.Catalog, new MonitoringSourceZoneRegistry([memory]),
            fixture.Slots.Owner, fixture.Slots.Provider, NullLogger<WindowsHardwareMetricSampler>.Instance);
        using var catalog = new VersionedProcessAttributionCatalogProvider();
        var counterCaptures = 0;
        var counters = new WindowsPhysicalMemoryResidualReader();
        var residuals = new WindowsResourceResidualBreakdownProvider(new StaticResourceResidualBreakdownProvider(),
            NullLogger<WindowsResourceResidualBreakdownProvider>.Instance, token =>
            {
                Interlocked.Increment(ref counterCaptures);
                return counters.Capture(token);
            });
        using var sampler = new WindowsResourceBreakdownSampler(hardware, catalog, residuals, null!, null!,
            fixture.Slots.Provider, fixture.Slots.Owner, new PdhProcessGpuReader());
        var walks = 0;
        var pfn = new WindowsPhysicalMemoryPfnReader();
        sampler.PhysicalMemoryPfnCapture = (attributed, token) =>
        {
            Interlocked.Increment(ref walks);
            return pfn.Capture(attributed, token);
        };
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        TrustedAccessEndpointTestAuthentication.Add(builder.Services, "processes.read");
        builder.Services.AddSingleton<IResourceBreakdownSampler>(sampler);
        builder.Services.AddSingleton<IResourceBreakdownPushSource>(sampler);
        builder.Services.AddSingleton<ISchedulingProcessFactObservationSource>(sampler);
        builder.Services.AddSingleton<IRuntimePlanProvider>(fixture.Slots.Provider);
        builder.Services.AddHostedService(_ => hardware);
        builder.Services.AddHostedService(_ => sampler);
        await using var app = TrustedAccessEndpointTestAuthentication.Build(builder);
        foreach (var method in new[] { "MapResourceMonitorEndpoints", "MapSubscriptionChannelEndpoints" })
            typeof(ResourceManagerEndpointRouteBuilderExtensions).GetMethod(method, BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, [app]);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        await app.StartAsync(deadline.Token);
        try
        {
            using var client = TrustedAccessEndpointTestAuthentication.Client(app);
            var selector = $"scope=details&{selection}&residualDetails=memory.usage";
            using var empty = JsonDocument.Parse(await client.GetStringAsync("/api/resource-monitor/snapshot?" + selector, deadline.Token));
            Assert.False(empty.RootElement.TryGetProperty("physicalMemoryResidual", out _));
            Assert.Empty(empty.RootElement.GetProperty("metrics").EnumerateArray());
            Assert.Equal(0, Volatile.Read(ref walks));
            Assert.Equal(0, Volatile.Read(ref counterCaptures));

            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/subscriptions/stream")
            {
                Content = JsonContent.Create(new { version = 1, subscriptions = new[]
                { new { id = "real-residual", path = "/api/resource-monitor/subscribe?" + selector + "&intervalMs=1000" } } })
            };
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            response.EnsureSuccessStatusCode();
            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(deadline.Token));
            DateTimeOffset pfnAt;
            while (true)
            {
                var line = await reader.ReadLineAsync(deadline.Token);
                Assert.NotNull(line);
                using var frame = JsonDocument.Parse(line);
                var value = frame.RootElement.GetProperty("value");
                if (!value.TryGetProperty("physicalMemoryResidual", out var physical)
                    || value.GetProperty("metrics").GetArrayLength() == 0) continue;
                AssertCounterDetails(value, metric);
                Assert.Equal("pfn", physical.GetProperty("source").GetString());
                Assert.Contains(physical.GetProperty("status").GetString(), new[] { "ready", "unavailable" });
                if (physical.GetProperty("status").GetString() == "unavailable")
                {
                    Assert.False(string.IsNullOrEmpty(physical.GetProperty("reasonCode").GetString()));
                    Assert.Empty(physical.GetProperty("parts").EnumerateArray());
                }
                pfnAt = physical.GetProperty("capturedAt").GetDateTimeOffset();
                break;
            }
            Assert.True(Volatile.Read(ref counterCaptures) > 0);
            var publishedWalks = Volatile.Read(ref walks);
            Assert.True(publishedWalks > 0);
            // Reads reuse the independent PFN publication; they do not stamp it
            // with a counter capture's time or perform another walk.
            using var cached = JsonDocument.Parse(await client.GetStringAsync("/api/resource-monitor/snapshot?" + selector, deadline.Token));
            AssertCounterDetails(cached.RootElement, metric);
            Assert.Equal(pfnAt, cached.RootElement.GetProperty("physicalMemoryResidual").GetProperty("capturedAt").GetDateTimeOffset());
            using var unrelated = JsonDocument.Parse(await client.GetStringAsync(
                "/api/resource-monitor/snapshot?scope=details&ids=cpu.usage&residualDetails=memory.usage", deadline.Token));
            Assert.False(unrelated.RootElement.TryGetProperty("physicalMemoryResidual", out _));
            Assert.DoesNotContain("system-memory/", unrelated.RootElement.GetRawText());
            Assert.Equal(publishedWalks, Volatile.Read(ref walks));
        }
        finally { await app.StopAsync(CancellationToken.None); }
    }

    private static void AssertCounterDetails(JsonElement value, string metric)
    {
        var details = Assert.Single(value.GetProperty("metrics").EnumerateArray());
        Assert.Equal(metric, details.GetProperty("metricId").GetString());
        Assert.Equal("memory.usage", details.GetProperty("totalMetricId").GetString());
        var owner = Assert.Single(details.GetProperty("software").EnumerateArray());
        Assert.Equal("resource-residual:memory.usage", owner.GetProperty("softwareId").GetString());
        var rows = owner.GetProperty("processes").EnumerateArray().ToArray();
        Assert.NotEmpty(rows);
        Assert.All(rows, row => { Assert.Equal(-1, row[0].GetInt32()); Assert.StartsWith("system-memory/", row[8].GetString()); });
        Assert.Contains(rows, row => row[8].GetString()!.StartsWith("system-memory/nonpaged-pool", StringComparison.Ordinal));
        Assert.Equal(details.GetProperty("residualValue").GetDouble(), rows.Sum(row => row[3].GetDouble()));
    }
}
