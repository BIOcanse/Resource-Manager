using ResourceManager.App.Infrastructure.ReadWrite;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using ResourceManager.App.Application.DeviceTopology;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ResourceManager.App.Domain.DeviceTopology;

namespace Resource_Manager_APP.Tests;

public sealed partial class DeviceInformationEndpointTests
{
    [Fact]
    public async Task StructuralOwnerPublishesOnlyOwnChangesWhileSampleCompletionStillAdvances()
    {
        var snapshot = DeviceInformationTestData.Snapshot();
        using var fixture = new Fixture(new Reader(() => snapshot));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var changes = fixture.Provider.SubscribeChangesAsync("structural-change-holder",
            TimeSpan.FromSeconds(1), timeout.Token).GetAsyncEnumerator();
        Assert.True(await changes.MoveNextAsync());
        var pending = changes.MoveNextAsync().AsTask();
        await fixture.Provider.SampleNowAsync(timeout.Token);
        Assert.True(await pending);
        Assert.All(changes.Current.InformationFields.Devices.SelectMany(static device => device.Fields),
            static field => Assert.NotEqual(ResourceManager.ReadWrite.ReadVolatilityKind.Sampled, field.Definition.Volatility.Kind));
        var first = changes.Current;
        pending = changes.MoveNextAsync().AsTask();
        for (var sample = 0; sample < 10; sample++)
        {
            snapshot = snapshot with { CapturedAt = DateTimeOffset.UtcNow,
                NetworkEndpointInformation = new([new("ipv4", new("192.0.2.2", null, checked((ushort)sample)), null, 2, 5)], [], [], []) };
            await fixture.Provider.SampleNowAsync(timeout.Token);
        }
        Assert.False(pending.IsCompleted);
        Assert.True(fixture.Provider.ReadState().StateRevision > first.StateRevision);
        snapshot = snapshot with { System = snapshot.System with { Model = "changed model" } };
        await fixture.Provider.SampleNowAsync(timeout.Token);
        Assert.True(await pending);
        Assert.Equal(first.ContentGeneration + 1, changes.Current.ContentGeneration);
        Assert.Equal("changed model", changes.Current.Snapshot!.System.Model);
        var committed = changes.Current;
        pending = changes.MoveNextAsync().AsTask();
        await fixture.Provider.StartAsync(default);
        var fetched = await fixture.Provider.FetchOnceAsync("unchanged-single-fetch", timeout.Token);
        Assert.True(fetched!.StateRevision > committed.StateRevision);
        Assert.False(pending.IsCompleted);
        await timeout.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        await fixture.Provider.StopAsync(default);
    }

    [Fact]
    public async Task IdenticalTopologyFailuresDoNotNotifyAgainAndRecoveryNotifiesOnce()
    {
        var snapshot = DeviceInformationTestData.Snapshot();
        using var fixture = new Fixture(new Reader(snapshot, new InvalidOperationException("failed"),
            new InvalidOperationException("failed"), snapshot));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var changes = fixture.Provider.SubscribeChangesAsync("health-changes", TimeSpan.FromSeconds(1), timeout.Token).GetAsyncEnumerator();
        Assert.True(await changes.MoveNextAsync());
        var next = changes.MoveNextAsync().AsTask();
        await fixture.Provider.SampleNowAsync(timeout.Token);
        Assert.True(await next);
        next = changes.MoveNextAsync().AsTask();
        await fixture.Provider.SampleNowAsync(timeout.Token);
        Assert.True(await next);
        Assert.Equal(DeviceTopologySnapshotStatus.Failed, changes.Current.State);
        next = changes.MoveNextAsync().AsTask();
        await fixture.Provider.SampleNowAsync(timeout.Token);
        Assert.False(next.IsCompleted);
        await fixture.Provider.SampleNowAsync(timeout.Token);
        Assert.True(await next);
        Assert.Equal(DeviceTopologySnapshotStatus.Ready, changes.Current.State);
        await timeout.CancelAsync();
    }
    [Fact]
    public async Task NetworkSingleFetchUsesOnlyItsOwnSamplingItemAndLeavesNoDemand()
    {
        var api = new NetworkEndpointInformationCollectorTests.Api();
        var reader = new Reader(DeviceInformationTestData.Snapshot());
        using var fixture = new Fixture(reader, networkEndpoints: new WindowsNetworkEndpointInformationReader(api));
        await using var app = await StartAsync(fixture.Provider);
        using var client = Client(app);
        await client.GetStringAsync("/api/device-topology/state");
        Assert.Equal(0, api.Reads);
        var topologyReads = reader.ReadCount;
        using var endpoints = JsonDocument.Parse(await client.GetStringAsync("/api/device-topology/network-endpoints/fields"));
        Assert.Equal(3, Field(endpoints.RootElement, "machine", "network_all_tcp_ipv4_endpoint_reported").GetProperty("value").GetArrayLength());
        Assert.Equal(topologyReads, reader.ReadCount);
        Assert.True(api.Reads >= 8);
        await fixture.WaitForNoDemandAndIdleAsync();
        var reads = api.Reads;
        await client.GetStringAsync("/api/device-topology/state");
        Assert.Equal(reads, api.Reads);
        await fixture.WaitForNoDemandAndIdleAsync();
        await app.StopAsync();
    }

    [Fact]
    public async Task NetworkStreamIsIndependentOfTopologyAndKeepsExactTypedFields()
    {
        var api = new NetworkEndpointInformationCollectorTests.Api();
        var reader = new Reader(DeviceInformationTestData.Snapshot());
        using var fixture = new Fixture(reader, networkEndpoints: new WindowsNetworkEndpointInformationReader(api));
        await using var app = await StartAsync(fixture.Provider);
        using var client = Client(app);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var response = await client.SendAsync(new(HttpMethod.Post, "/api/subscriptions/stream")
        {
            Content = JsonContent.Create(new { version = 1, subscriptions = new[] {
                new { id = "connections", path = "/api/device-topology/network-endpoints/fields/subscribe?intervalMs=100" } } })
        }, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var stream = new StreamReader(await response.Content.ReadAsStreamAsync(timeout.Token));
        using var frame = JsonDocument.Parse(await ReadFirstObservedInformationAsync(stream, timeout.Token));
        var value = frame.RootElement.GetProperty("value");
        Assert.Equal(4, value.GetProperty("devices")[0].GetProperty("fields").GetArrayLength());
        Assert.Equal(0, reader.ReadCount);
        var tcp4 = Field(value, "machine", "network_all_tcp_ipv4_endpoint_reported");
        Assert.Equal(3, tcp4.GetProperty("value").GetArrayLength());
        Assert.All(api.Leases, static lease => Assert.Equal(1, lease.Disposals));
        await timeout.CancelAsync();
        response.Dispose();
        await fixture.WaitForNoDemandAndIdleAsync();
        await app.StopAsync();
    }
    [Fact]
    public async Task ExactNetworkFieldsUseTheSameItemClearFailuresAndRemainDirectlyReadableAfterStop()
    {
        var api = new NetworkEndpointInformationCollectorTests.Api();
        var topology = new Reader(DeviceInformationTestData.Snapshot());
        using var fixture = new Fixture(topology, networkEndpoints: new WindowsNetworkEndpointInformationReader(api));
        await using var cpu = new CpuTopologyProviderTestFixture();
        using var process = ProcessBasicFieldCollectorTests.Fixture(new ProcessBasicFieldCollectorTests.Api());
        var metrics = new HostReadWriteBackend(null!, null!, new HardwareMetricCatalogState(), null!, null!);
        var host = new HostFieldBackends(metrics, new HostFieldSubscriptionBackend(metrics, null!, null!),
            fixture.Provider, cpu.Reader, new HostProcessFieldBackend(process.Sampler));
        await fixture.Provider.CaptureNetworkEndpointInformationAsync(default);
        var field = host.Reads.Catalog.Fields.Single(static field =>
            field.Field.FieldName == "network_all_tcp_ipv4_endpoint_reported").Field;
        Assert.Equal(3, host.Reads.ReadCached(field).Value!.Structure!.Value.GetArrayLength());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await fixture.Provider.StartAsync(default);
        try
        {
            using var slow = host.Subscriptions.Acquire("network-slow", [field], TimeSpan.FromMinutes(1));
            await using var stream = host.Subscriptions.SubscribeAsync("network-fast", [field],
                TimeSpan.FromMilliseconds(100), timeout.Token).GetAsyncEnumerator();
            Assert.True(await stream.MoveNextAsync());
            Assert.Equal(3, Assert.Single(stream.Current).Value!.Structure!.Value.GetArrayLength());
            api.ReadTable = (kind, _) => kind == WindowsNetworkEndpointTableKind.TcpIpv4
                ? null : NetworkEndpointInformationCollectorTests.Raw(kind);
            do { Assert.True(await stream.MoveNextAsync()); }
            while (Assert.Single(stream.Current).Value is not null);
            Assert.Null(host.Reads.ReadCached(field).Value);
            Assert.Equal(0, topology.ReadCount);
        }
        finally { await fixture.Provider.StopAsync(default); }
        api.ReadTable = (kind, _) => NetworkEndpointInformationCollectorTests.Raw(kind);
        Assert.Equal(3, Assert.Single(await host.Reads.CollectAsync([field], default)).Value!.Structure!.Value.GetArrayLength());
        Assert.Equal(0, topology.ReadCount);
        Assert.Equal(0, cpu.Sampler.CaptureCount);
    }}