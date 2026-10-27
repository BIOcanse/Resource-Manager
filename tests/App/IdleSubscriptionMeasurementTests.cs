using ResourceManager.App.Application.Monitoring;
using System.Net.Http.Json;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Application.Components;
using ResourceManager.App.Application.DeviceTopology;
using ResourceManager.App.Application.Metrics;
using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Endpoints;
using ResourceManager.App.Infrastructure.DeviceTopology.Snapshots;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;

namespace Resource_Manager_APP.Tests;

public sealed class IdleSubscriptionMeasurementTests
{
    [Fact]
    public async Task MeasureSixtySecondsOfUnchangedContentOverTheRealHttpSubscriptionHost()
    {
        var raw = StorageMetricSnapshotTestData.CommittedSnapshot();
        var binding = raw.FieldBindings.Values.First();
        var snapshot = raw with { FieldBindings = Enumerable.Range(0, 1000).Select(index => binding with
        { DeviceIdentity = $"fixture-device-{index}", RequestAddress = $"fixture.field.{index}", RequestAliases = [], SelectedRequestAliases = [] })
            .ToDictionary(static item => item.RequestAddress) };
        var catalog = new HardwareMetricCatalogState();
        catalog.PublishCatalogProbe(snapshot);
        using var sampling = new HostManagerSamplingSubscriptionTestFixture();
        using var topology = new DeviceTopologySnapshotProvider(new Reader(), new Store(), new(), sampling.Owner,
            NullLogger<DeviceTopologySnapshotProvider>.Instance, new WindowsAcpiFirmwareTableReader());
        await topology.SampleNowAsync(default);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        TrustedAccessEndpointTestAuthentication.Add(builder.Services, "read", "metrics.read");
        builder.Services.AddSingleton(catalog);
        builder.Services.AddSingleton<IMetricSnapshotPushSource>(new Discovery(catalog, snapshot));
        builder.Services.AddSingleton<IComponentManager>(new NoInstalledComponentManager());
        builder.Services.AddSingleton<IDeviceTopologySnapshotProvider>(topology);
        builder.Services.AddSingleton(topology);
        builder.Services.AddHostedService(_ => topology);
        await using var app = TrustedAccessEndpointTestAuthentication.Build(builder);
        typeof(ResourceManagerEndpointRouteBuilderExtensions).GetMethod("MapSubscriptionChannelEndpoints",
            BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [app]);
        await app.StartAsync();
        using var client = TrustedAccessEndpointTestAuthentication.Client(app);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var counts = new Dictionary<string, Counter> { ["catalog"] = new(), ["topology"] = new() };
        using var response = await client.SendAsync(new(HttpMethod.Post, "/api/subscriptions/stream")
        {
            Content = JsonContent.Create(new { version = 1, subscriptions = new[] {
                new { id = "catalog", path = "/api/metrics/fields/catalog/subscribe?intervalMs=1000" },
                new { id = "topology", path = "/api/device-topology/state/subscribe?intervalMs=1000" } } })
        }, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        response.EnsureSuccessStatusCode();
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(deadline.Token));
        try
        {
            while (await reader.ReadLineAsync(deadline.Token) is { } line)
            {
                using var frame = JsonDocument.Parse(line);
                var counter = counts[frame.RootElement.GetProperty("subscriptionId").GetString()!];
                counter.Frames++;
                counter.Bytes += Encoding.UTF8.GetByteCount(line) + 1;
            }
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested) { }
        finally { response.Dispose(); await app.StopAsync(); }
        Assert.All(counts.Values, static counter => Assert.True(counter.Frames > 0));
        var output = Environment.GetEnvironmentVariable("RM_IDLE_MEASUREMENT_OUTPUT")
            ?? Path.Combine(Environment.CurrentDirectory, ".local", "reports", "idle-after.json");
        File.WriteAllText(output, JsonSerializer.Serialize(new { seconds = 60, fixture = "1000 metric devices and 1000 rows per endpoint family", channels = counts },
            new JsonSerializerOptions { WriteIndented = true }));
    }

    private sealed class Counter { public int Frames { get; set; } public long Bytes { get; set; } }
    private sealed class Discovery(HardwareMetricCatalogState catalog, HardwareMetricSnapshot snapshot) : IMetricSnapshotPushSource
    {
        public async IAsyncEnumerable<HardwareMetricSnapshot> SubscribeAsync(string id, MetricSampleRequest request,
            TimeSpan interval, [EnumeratorCancellation] CancellationToken token)
        {
            using var timer = new PeriodicTimer(interval);
            ulong generation = 1;
            do
            {
                var next = snapshot with { CatalogGeneration = generation++ };
                catalog.PublishCatalogProbe(next);
                yield return next;
            } while (await timer.WaitForNextTickAsync(token));
        }
    }
    private sealed class Reader : IDeviceTopologyReader
    {
        private readonly DeviceTopologyNetworkEndpointInformation endpoints = new(
            Enumerable.Range(0, 1000).Select(index => new NetworkTcpEndpoint("ipv4", new("192.0.2.2", null, checked((ushort)index)), null, 2, 5)).ToArray(),
            Enumerable.Range(0, 1000).Select(index => new NetworkTcpEndpoint("ipv6", new("::1", null, checked((ushort)index)), null, 2, 5)).ToArray(),
            Enumerable.Range(0, 1000).Select(index => new NetworkUdpEndpoint("ipv4", new("192.0.2.2", null, checked((ushort)index)), 5)).ToArray(),
            Enumerable.Range(0, 1000).Select(index => new NetworkUdpEndpoint("ipv6", new("::1", null, checked((ushort)index)), 5)).ToArray());
        public DeviceTopologySnapshot ReadSnapshot() => DeviceInformationTestData.Snapshot() with
        { CapturedAt = DateTimeOffset.UtcNow, NetworkEndpointInformation = endpoints };
    }
    private sealed class Store : IDeviceTopologySnapshotStore
    {
        public Task<DeviceTopologyPersistedSnapshot?> LoadAsync(CancellationToken token) => Task.FromResult<DeviceTopologyPersistedSnapshot?>(null);
        public Task SaveAsync(DeviceTopologyPersistedSnapshot snapshot, CancellationToken token) => Task.CompletedTask;
    }
}