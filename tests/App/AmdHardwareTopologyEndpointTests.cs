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

public sealed class AmdHardwareTopologyEndpointTests
{
    [Fact]
    public async Task OriginalHttpAndSharedSubscriptionPublishClearAndRecoverExactHardwareMembership()
    {
        await using var fixture = new CpuTopologyProviderTestFixture();
        await using var app = await StartAsync(fixture.Reader);
        using var client = new HttpClient { BaseAddress = new Uri(Assert.Single(app.Services
            .GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses)) };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using (var empty = await client.GetFromJsonAsync<JsonDocument>("/api/cpu/topology/fields", timeout.Token))
            Assert.Empty(empty!.RootElement.GetProperty("devices").EnumerateArray());
        Assert.Equal(0, fixture.Sampler.CaptureCount);
        Assert.Equal(0u, fixture.ReadDemand().SourceCount);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/subscriptions/stream")
        {
            Content = JsonContent.Create(new { version = 1,
                subscriptions = new[] { new { id = "amd-hardware", path = "/api/cpu/topology/fields/subscribe?intervalMs=100" } } })
        };
        var responseTask = client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        while (fixture.ReadDemand().SourceCount != 1) await Task.Delay(10, timeout.Token);
        await fixture.Reader.StartAsync(CancellationToken.None);
        Assert.Equal(1, await fixture.Sampler.WaitForCaptureAsync());
        var snapshot = CpuTopologyProviderTestFixture.CreateSnapshot(0) with
        { OsTopologyInformation = AmdHardwareTopologyTests.Information([0x80000000U, 0x80000001U]) };
        fixture.Sampler.Complete(snapshot);
        using var response = await responseTask;
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var stream = new StreamReader(await response.Content.ReadAsStreamAsync(timeout.Token));
        var populated = await ReadFrameAsync(stream, timeout.Token);
        var members = Membership(populated);
        Assert.Equal(JsonValueKind.Array, members.ValueKind);
        var member = Assert.Single(members.EnumerateArray());
        var coreId = member.GetProperty("coreDeviceIdentity").GetString();
        var complexId = member.GetProperty("complexDeviceIdentity").GetString();
        Assert.Contains(populated.GetProperty("devices").EnumerateArray(), device =>
            device.GetProperty("objectType").GetString() == "cpu_core" && device.GetProperty("deviceIdentity").GetString() == coreId);
        Assert.Contains(populated.GetProperty("devices").EnumerateArray(), device =>
            device.GetProperty("objectType").GetString() == "cpu_ccx" && device.GetProperty("deviceIdentity").GetString() == complexId);
        Assert.Equal(2, member.GetProperty("logicalProcessorAddresses").GetArrayLength());
        Assert.All(member.GetProperty("logicalProcessorAddresses").EnumerateArray(), address =>
            Assert.Equal(2, address.GetProperty("processorGroup").GetInt32()));
        Assert.Equal(2, await fixture.Sampler.WaitForCaptureAsync());
        var current = fixture.Reader.GetInformationSnapshot();
        for (var index = 0; index < 3; index++)
        {
            using var cached = await client.GetFromJsonAsync<JsonDocument>("/api/cpu/topology/fields", timeout.Token);
            Assert.Equal(populated.GetRawText(), cached!.RootElement.GetRawText());
            Assert.Same(current, fixture.Reader.GetInformationSnapshot());
        }
        Assert.Equal(2, fixture.Sampler.CaptureCount);
        Assert.Equal(1u, fixture.ReadDemand().SourceCount);
        fixture.Sampler.Complete(new InvalidOperationException("Recorded CPUID capture unavailable."));
        Assert.Equal(JsonValueKind.Null, Membership(await ReadFrameAsync(stream, timeout.Token)).ValueKind);
        Assert.Equal(3, await fixture.Sampler.WaitForCaptureAsync());
        fixture.Sampler.Complete(snapshot);
        Assert.Equal(members.GetRawText(), Membership(await ReadFrameAsync(stream, timeout.Token)).GetRawText());
        await timeout.CancelAsync();
        response.Dispose();
        await app.StopAsync().WaitAsync(CpuTopologyProviderTestFixture.Deadline);
        Assert.Equal(0u, fixture.ReadDemand().SourceCount);
    }

    private static JsonElement Membership(JsonElement snapshot)
        => Assert.Single(Assert.Single(snapshot.GetProperty("devices").EnumerateArray(), device =>
            device.GetProperty("objectType").GetString() == "cpu_ccd").GetProperty("fields").EnumerateArray(), field =>
            field.GetProperty("definition").GetProperty("name").GetString() == "cpu_amd_ccd_core_membership").GetProperty("value");

    private static async Task<JsonElement> ReadFrameAsync(StreamReader reader, CancellationToken cancellation)
    {
        using var document = JsonDocument.Parse((await reader.ReadLineAsync(cancellation))!);
        Assert.Equal(new[] { "subscriptionId", "value" }, document.RootElement.EnumerateObject().Select(static property => property.Name));
        Assert.Equal("amd-hardware", document.RootElement.GetProperty("subscriptionId").GetString());
        return document.RootElement.GetProperty("value").Clone();
    }

    private static async Task<WebApplication> StartAsync(ICpuTopologyReader reader)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddResourceManagerApp(["--no-native-ui"], StartupCapabilitySet.NormalReadOnly);
        builder.Services.RemoveAll<IHostedService>();
        builder.Services.AddSingleton(reader);
        var app = builder.Build();
        var endpoints = typeof(ResourceManagerEndpointRouteBuilderExtensions);
        endpoints.GetMethod("MapSystemEndpoints", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [app, StartupCapabilitySet.NormalReadOnly]);
        endpoints.GetMethod("MapSubscriptionChannelEndpoints", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [app]);
        await app.StartAsync();
        return app;
    }
}
