using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ResourceManager.App.Application.ResourceBreakdown;
using ResourceManager.App.Application.ResourceTable;
using ResourceManager.App.Application.RuntimeSpecialization;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Domain.ResourceTable;
using ResourceManager.App.Endpoints;

namespace Resource_Manager_APP.Tests;

public sealed partial class ProcessNetworkFieldCollectorTests
{
    [Fact]
    public async Task RealHttpAndUnifiedPushUseTheOriginalOwnerRecoverAndStopWithoutDemand()
    {
        using var drainDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var phase = 0;
        var captured = DateTimeOffset.UtcNow.AddSeconds(-3);
        var good = Window(captured, Birth, "9007199254740993", "7");
        var network = new NetworkReader(_ => Volatile.Read(ref phase) switch
        {
            1 => good with { ObservedTraffic = null },
            3 => throw new InvalidOperationException("fixture source failure"),
            _ => good
        });
        using var fixture = new Fixture(network);
        await using var app = await StartAsync(fixture);
        using var client = Client(app);
        using var catalog = JsonDocument.Parse(await client.GetStringAsync("/api/resource-breakdown/fields/catalog"));
        Assert.Equal(ProcessMetricFieldRegistry.Definitions.Count, catalog.RootElement.GetArrayLength());
        Assert.All(catalog.RootElement.EnumerateArray(), field => Assert.Equal("process", field.GetProperty("resource").GetString()));
        var selector = "/api/resource-monitor/subscribe?scope=bars&intervalMs=1000&ids=" + string.Join(',', Names);
        var snapshotUrl = "/api/resource-breakdown/snapshot?ids=" + string.Join(',', Names);
        Assert.Equal(0, network.Reads);
        using var initial = JsonDocument.Parse(await client.GetStringAsync(snapshotUrl));
        Assert.Equal(4, initial.RootElement.GetProperty("processFields").GetProperty("fields").GetArrayLength());
        Assert.Equal(captured, initial.RootElement.GetProperty("processFields").GetProperty("capturedAt").GetDateTimeOffset());
        Assert.Equal("9007199254740993", WireValues(initial.RootElement)[0].GetProperty("integerValue").GetString());
        Assert.True(network.Reads > 0);
        await ProcessBasicFieldCollectorTests.WaitForProcessDemandDrainedAsync(fixture, drainDeadline.Token);
        var initialReads = network.Reads;
        await ProcessBasicFieldCollectorTests.WaitForProcessDemandDrainedAsync(fixture, drainDeadline.Token);
        Assert.Equal(initialReads, network.Reads);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/subscriptions/stream")
        { Content = JsonContent.Create(new { version = 1, subscriptions = new[] { new { id = "process-network", path = selector } } }) };
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var stream = new StreamReader(await response.Content.ReadAsStreamAsync(timeout.Token));
        using var first = await NextFrame(stream, timeout.Token, true);
        var wire = first.RootElement.GetProperty("value").GetProperty("breakdown");
        Assert.Equal("9007199254740993", WireValues(wire)[0].GetProperty("integerValue").GetString());
        Assert.Equal(captured, wire.GetProperty("processFields").GetProperty("capturedAt").GetDateTimeOffset());
        Assert.Empty(wire.GetProperty("bars").EnumerateArray());
        Volatile.Write(ref phase, 1);
        using var unavailable = await NextFrame(stream, timeout.Token, false);
        Assert.Equal(JsonValueKind.Null, WireValues(unavailable.RootElement.GetProperty("value").GetProperty("breakdown"))[0].GetProperty("integerValue").ValueKind);
        Volatile.Write(ref phase, 2);
        using var recovered = await NextFrame(stream, timeout.Token, true);
        Assert.Equal("9007199254740993", WireValues(recovered.RootElement.GetProperty("value").GetProperty("breakdown"))[0].GetProperty("integerValue").GetString());
        Volatile.Write(ref phase, 3);
        using var failed = await NextFrame(stream, timeout.Token, false);
        Assert.Equal(JsonValueKind.Null, WireValues(failed.RootElement.GetProperty("value").GetProperty("breakdown"))[3].GetProperty("numericValue").ValueKind);
        Volatile.Write(ref phase, 2);
        using var final = await NextFrame(stream, timeout.Token, true);
        await timeout.CancelAsync();
        response.Dispose();
        await ProcessBasicFieldCollectorTests.WaitForProcessDemandDrainedAsync(fixture, drainDeadline.Token);
        var stoppedReads = network.Reads;
        using var fresh = JsonDocument.Parse(await client.GetStringAsync(snapshotUrl));
        Assert.Equal("9007199254740993", WireValues(fresh.RootElement)[0].GetProperty("integerValue").GetString());
        Assert.True(network.Reads > stoppedReads);
        var beforeMonitor = network.Reads;
        using var monitor = JsonDocument.Parse(await client.GetStringAsync("/api/resource-monitor/snapshot?scope=bars&ids=" + Names[3]));
        Assert.Single(WireValues(monitor.RootElement.GetProperty("breakdown")).EnumerateArray());
        Assert.Equal(3.5, WireValues(monitor.RootElement.GetProperty("breakdown"))[0].GetProperty("numericValue").GetDouble());
        Assert.True(network.Reads > beforeMonitor);
        await ProcessBasicFieldCollectorTests.WaitForProcessDemandDrainedAsync(fixture, drainDeadline.Token);
        var fetchedReads = network.Reads;
        await ProcessBasicFieldCollectorTests.WaitForProcessDemandDrainedAsync(fixture, drainDeadline.Token);
        Assert.Equal(fetchedReads, network.Reads);
        await app.StopAsync();
    }

    private static JsonElement WireValues(JsonElement wire)
        => wire.GetProperty("processFields").GetProperty("processes")[0].GetProperty("values");

    private static async Task<JsonDocument> NextFrame(StreamReader stream, CancellationToken token, bool hasValue)
    {
        while (!token.IsCancellationRequested)
        {
            var line = await stream.ReadLineAsync(token);
            Assert.NotNull(line);
            var frame = JsonDocument.Parse(line!);
            var wire = frame.RootElement.GetProperty("value").GetProperty("breakdown");
            if (wire.GetProperty("processFields").GetProperty("processes").GetArrayLength() > 0
                && (WireValues(wire)[0].GetProperty("integerValue").ValueKind != JsonValueKind.Null) == hasValue)
                return frame;
            frame.Dispose();
        }
        throw new OperationCanceledException(token);
    }

    internal static async Task<WebApplication> StartAsync(Fixture fixture)
    {
        var builder = WebApplication.CreateBuilder();
        TrustedAccessEndpointTestAuthentication.Add(builder.Services, "processes.read");
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<IResourceBreakdownSampler>(fixture.Sampler);
        builder.Services.AddSingleton<IResourceBreakdownPushSource>(fixture.Sampler);
        builder.Services.AddSingleton<IRuntimePlanProvider>(fixture.Subscriptions.Provider);
        builder.Services.AddHostedService(_ => fixture.Sampler);
        var app = TrustedAccessEndpointTestAuthentication.Build(builder);
        foreach (var method in new[] { "MapResourceMonitorEndpoints", "MapSubscriptionChannelEndpoints" })
            typeof(ResourceManagerEndpointRouteBuilderExtensions).GetMethod(method, BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [app]);
        await app.StartAsync();
        return app;
    }

    internal static HttpClient Client(WebApplication app) => TrustedAccessEndpointTestAuthentication.Apply(new HttpClient(new SocketsHttpHandler { MaxResponseDrainSize = 0 })
    { BaseAddress = new Uri(Assert.Single(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses)),
        Timeout = TimeSpan.FromSeconds(15) }, app.Services);


}
