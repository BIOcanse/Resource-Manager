using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using ResourceManager.App.Application.CpuTopology;
using ResourceManager.App.Endpoints;
using ResourceManager.App.Hosting;
using ResourceManager.App.Hosting.StartupCapabilities;

namespace Resource_Manager_APP.Tests;

public sealed class CpuTopologyEndpointTests
{
    [Fact]
    public async Task OrdinaryHttpReadsReturnJsonNullWithoutCapturing()
    {
        await using var fixture = new CpuTopologyProviderTestFixture();
        await using var app = await StartEndpointsAsync(fixture.Reader);
        using var client = CreateClient(app);
        foreach (var path in new[] { "/api/cpu/topology", "/api/cpu/topology/exclusive-bindings" })
        {
            using var response = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("null", await response.Content.ReadAsStringAsync());
        }
        Assert.Equal(0, fixture.Sampler.CaptureCount);
        Assert.Equal(0u, fixture.ReadDemand().SourceCount);
        await app.StopAsync();
    }

    [Fact]
    public async Task SharedHttpChannelForwardsNullAndRecoveryWithoutEndingEitherSubscription()
    {
        await using var fixture = new CpuTopologyProviderTestFixture();
        await using var app = await StartEndpointsAsync(fixture.Reader);
        using var client = CreateClient(app);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/subscriptions/stream")
        {
            Content = JsonContent.Create(new
            {
                version = 1,
                subscriptions = new[]
                {
                    new { id = "cpu-first", path = "/api/cpu/topology/subscribe?intervalMs=100" },
                    new { id = "cpu-second", path = "/api/cpu/topology/subscribe?intervalMs=100" }
                }
            })
        };
        var responseTask = client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        while (fixture.ReadDemand().SourceCount != 2)
        {
            await Task.Delay(10, timeout.Token);
        }
        await fixture.Reader.StartAsync(CancellationToken.None);
        Assert.Equal(1, await fixture.Sampler.WaitForCaptureAsync());
        Assert.Equal(2u, fixture.ReadDemand().SourceCount);
        fixture.Sampler.Complete(new InvalidOperationException("Native capture failed."));

        using var response = await responseTask;
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var stream = new StreamReader(await response.Content.ReadAsStreamAsync(timeout.Token));
        var emptyFrames = await ReadPairAsync(stream, timeout.Token);
        Assert.All(emptyFrames, frame => Assert.Equal(JsonValueKind.Null, frame.GetProperty("value").ValueKind));

        Assert.Equal(2, await fixture.Sampler.WaitForCaptureAsync());
        fixture.Sampler.Complete(CpuTopologyProviderTestFixture.CreateSnapshot(34));
        var populatedFrames = await ReadPairAsync(stream, timeout.Token);
        Assert.All(populatedFrames, frame =>
        {
            Assert.Equal("Subscription Test CPU", frame.GetProperty("value").GetProperty("cpuName").GetString());
            Assert.Equal(34, frame.GetProperty("value").GetProperty("logicalProcessors")[0].GetProperty("usagePercent").GetDouble());
            Assert.Equal(["subscriptionId", "value"], frame.EnumerateObject().Select(property => property.Name));
        });
        await timeout.CancelAsync();
        response.Dispose();
        await app.StopAsync().WaitAsync(CpuTopologyProviderTestFixture.Deadline);
        Assert.Equal(0u, fixture.ReadDemand().SourceCount);
    }

    private static async Task<JsonElement[]> ReadPairAsync(StreamReader stream, CancellationToken cancellationToken)
    {
        var frames = new JsonElement[2];
        for (var index = 0; index < frames.Length; index++)
        {
            using var document = JsonDocument.Parse((await stream.ReadLineAsync(cancellationToken))!);
            frames[index] = document.RootElement.Clone();
        }
        Assert.Equal(["cpu-first", "cpu-second"], frames
            .Select(frame => frame.GetProperty("subscriptionId").GetString()).Order());
        return frames;
    }

    private static async Task<WebApplication> StartEndpointsAsync(ICpuTopologyReader reader)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddResourceManagerApp(["--no-native-ui"], StartupCapabilitySet.NormalReadOnly);
        builder.Services.RemoveAll<IHostedService>();
        builder.Services.AddSingleton(reader);
        var app = builder.Build();
        var endpoints = typeof(ResourceManagerEndpointRouteBuilderExtensions);
        endpoints.GetMethod("MapSystemEndpoints", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [app, StartupCapabilitySet.NormalReadOnly]);
        endpoints.GetMethod("MapSubscriptionChannelEndpoints", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [app]);
        await app.StartAsync();
        return app;
    }

    private static HttpClient CreateClient(WebApplication app)
        => new()
        {
            BaseAddress = new Uri(Assert.Single(app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses)),
            Timeout = CpuTopologyProviderTestFixture.Deadline
        };
}
