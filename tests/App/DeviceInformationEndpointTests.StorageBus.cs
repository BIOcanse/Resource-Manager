using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ResourceManager.App.Application.DeviceTopology;
using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.App.Infrastructure.DeviceTopology;
using ResourceManager.App.Infrastructure.DeviceTopology.Snapshots;

namespace Resource_Manager_APP.Tests;

public sealed partial class DeviceInformationEndpointTests
{
    [Fact]
    public async Task StorageBusHttpUsesSingleFetchAndClearsFailureThenEmptyDirectoryRemovesKnownDisk()
    {
        var api = new StorageBusInformationCollectorTests.Api(); var collector = new WindowsDiskLayoutInformationReader(api);
        var reader = new Reader(() => DeviceInformationTestData.Snapshot() with { DiskLayoutInformation = collector.Read(), VolumeSpaceInformation = [] });
        using var fixture = new Fixture(reader); await using var app = await StartAsync(fixture.Provider); using var client = Client(app);
        _ = ReadPublishedFields(fixture.Provider, app); Assert.Equal(0, api.Reads);
        for (var phase = 0; phase < 5; phase++)
        {
            api.Bus = phase == 1 ? null : phase == 4 ? 0U : 14U;
            api.Directory = phase == 2 ? null : phase == 3 ? [] : [DiskLayoutInformationCollectorTests.DiskA];
            var sampledHttpFields1 = await client.GetStringAsync("/api/device-topology/fields"); var reads = api.Reads;
            using var document = JsonDocument.Parse(sampledHttpFields1); var root = document.RootElement;
            if (phase == 3) Assert.DoesNotContain(root.GetProperty("devices").EnumerateArray(), d => d.GetProperty("deviceIdentity").GetString() == DiskLayoutInformationCollectorTests.IdA);
            else
            {
                var value = Field(root, DiskLayoutInformationCollectorTests.IdA, StorageBusInformationCollectorTests.Name).GetProperty("value");
                if (phase is 1 or 2) Assert.Equal(JsonValueKind.Null, value.ValueKind); else Assert.Equal(phase == 4 ? "0" : "14", value.GetString());
                if (phase == 1) Assert.Equal("2", Field(root, DiskLayoutInformationCollectorTests.IdA, "storage_all_partition_style_reported").GetProperty("value").GetString());
            }
            Assert.Equal(reads, api.Reads); Assert.Equal("Machine Model", Field(root, "machine", "machine_all_model_declared").GetProperty("value").GetString());
        }
        await app.StopAsync();
    }

    [Fact]
    public async Task StorageBusPersistedStartupAndWholeFailureNeverPublishOldTypeAsCurrent()
    {
        var raw = DeviceInformationTestData.Snapshot() with { DiskLayoutInformation = [new(DiskLayoutInformationCollectorTests.IdA) { OsDiskNumberReported = 2, BusTypeReported = 14 }], VolumeSpaceInformation = [] };
        var persisted = new DeviceTopologyPersistedSnapshot(DeviceTopologySnapshotState.CurrentSchemaVersion, "test", 11, new DeviceTopologySemanticComparer().ComputeHash(raw), raw);
        var reader = new Reader(raw, new IOException("capture failed"), raw); using var fixture = new Fixture(reader, persisted);
        await using var app = await StartAsync(fixture.Provider); using var client = Client(app);
        using var initial = JsonDocument.Parse(ReadPublishedFields(fixture.Provider, app)); AssertBusNull(initial.RootElement); Assert.Equal(0, reader.ReadCount);
        var sampledHttpFields2 = await client.GetStringAsync("/api/device-topology/fields"); using var fresh = JsonDocument.Parse(sampledHttpFields2);
        Assert.Equal("14", Field(fresh.RootElement, DiskLayoutInformationCollectorTests.IdA, StorageBusInformationCollectorTests.Name).GetProperty("value").GetString());
        var sampledHttpFields3 = await client.GetStringAsync("/api/device-topology/fields"); using var failed = JsonDocument.Parse(sampledHttpFields3); AssertBusNull(failed.RootElement);
        var sampledHttpFields4 = await client.GetStringAsync("/api/device-topology/fields"); using var recovered = JsonDocument.Parse(sampledHttpFields4);
        Assert.Equal("14", Field(recovered.RootElement, DiskLayoutInformationCollectorTests.IdA, StorageBusInformationCollectorTests.Name).GetProperty("value").GetString()); await app.StopAsync();
    }

    [Fact]
    public async Task StorageBusUnifiedWorkerReadsOnlyWithDemandAndCancellationKeepsDisplay()
    {
        var api = new StorageBusInformationCollectorTests.Api(); var collector = new WindowsDiskLayoutInformationReader(api);
        var reader = new Reader(() => DeviceInformationTestData.Snapshot() with { DiskLayoutInformation = collector.Read(), VolumeSpaceInformation = [] });
        using var fixture = new Fixture(reader); await using var app = await StartAsync(fixture.Provider); using var client = Client(app);
        _ = ReadPublishedFields(fixture.Provider, app); Assert.Equal(0, api.Reads);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12)); using var request = new HttpRequestMessage(HttpMethod.Post, "/api/subscriptions/stream")
        { Content = JsonContent.Create(new { version = 1, subscriptions = new[] { new { id = "storage-bus", path = "/api/device-topology/fields/subscribe?intervalMs=1000" } } }) };
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var stream = new StreamReader(await response.Content.ReadAsStreamAsync(timeout.Token)); using var message = JsonDocument.Parse(await ReadFirstObservedInformationAsync(stream, timeout.Token));
        var bus = Field(message.RootElement.GetProperty("value"), DiskLayoutInformationCollectorTests.IdA, StorageBusInformationCollectorTests.Name);
        Assert.Equal("14", bus.GetProperty("value").GetString()); Assert.Equal(StorageBusInformationCollectorTests.Source, bus.GetProperty("definition").GetProperty("sourceId").GetString());
        Assert.True(api.Reads >= 1); await timeout.CancelAsync(); response.Dispose(); await Task.Delay(250);
        var reads = api.Reads; var cached = fixture.Provider.ReadState().InformationFields;
        using var displayed = JsonDocument.Parse(ReadPublishedFields(fixture.Provider, app)); Assert.Equal("14", Field(displayed.RootElement, DiskLayoutInformationCollectorTests.IdA, StorageBusInformationCollectorTests.Name).GetProperty("value").GetString());
        await Task.Delay(1300); Assert.Equal(reads, api.Reads); Assert.Same(cached, fixture.Provider.ReadState().InformationFields); await app.StopAsync();
    }

    private static void AssertBusNull(JsonElement root) => Assert.Equal(JsonValueKind.Null,
        Field(root, DiskLayoutInformationCollectorTests.IdA, StorageBusInformationCollectorTests.Name).GetProperty("value").ValueKind);
}
