using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ResourceManager.App.Application.DeviceTopology;
using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.App.Infrastructure.DeviceTopology.Snapshots;

namespace Resource_Manager_APP.Tests;

public sealed partial class DeviceInformationEndpointTests
{
    [Fact]
    public async Task PciResourceHttpUsesOriginalCacheAndSeparatesFailedSourceFromSuccessfulEmptyRanges()
    {
        var api = new PciResourceInformationCollectorTests.Api(); var frames = api.Descriptors;
        var reader = new Reader(() => PciResourceInformationCollectorTests.Snapshot(api));
        using var fixture = new Fixture(reader); await using var app = await StartAsync(fixture.Provider); using var client = Client(app);
        _ = await client.GetStringAsync("/api/device-topology/fields"); Assert.Equal(0, api.Opens);
        for (var phase = 0; phase < 4; phase++)
        {
            api.Fault = phase == 1 ? "data" : ""; api.Descriptors = phase == 2 ? [] : frames;
            await fixture.Provider.SampleNowAsync(CancellationToken.None);
            var current = fixture.Provider.ReadState().InformationFields; var count = api.Opens; var calls = api.DataCalls;
            using var result = JsonDocument.Parse(await client.GetStringAsync("/api/device-topology/fields"));
            var field = Field(result.RootElement, PciResourceInformationCollectorTests.Identity, "pci_all_resource_range");
            Assert.Equal("windows.pci-allocated-resources", field.GetProperty("definition").GetProperty("sourceId").GetString());
            var value = field.GetProperty("value");
            if (phase == 1) Assert.Equal(JsonValueKind.Null, value.ValueKind);
            else if (phase == 2) Assert.Equal(0, value.GetArrayLength());
            else { Assert.Equal(4, value.GetArrayLength()); Assert.Equal("18446744073709551616", value[0].GetProperty("lengthDerived").GetString()); }
            Assert.Equal("Machine Model", Field(result.RootElement, "machine", "machine_all_model_declared").GetProperty("value").GetString());
            Assert.Equal(count, api.Opens); Assert.Equal(calls, api.DataCalls); Assert.Same(current, fixture.Provider.ReadState().InformationFields);
        }
        await app.StopAsync();
    }

    [Fact]
    public async Task PciResourcePersistedAndWholeSourceFailureClearCurrentValuesUntilRecapture()
    {
        var raw = PciResourceInformationCollectorTests.Snapshot(new PciResourceInformationCollectorTests.Api());
        var persisted = new DeviceTopologyPersistedSnapshot(DeviceTopologySnapshotState.CurrentSchemaVersion, "test", 7,
            new DeviceTopologySemanticComparer().ComputeHash(raw), raw);
        var reader = new Reader(raw, new InvalidOperationException("source failed"), raw);
        using var fixture = new Fixture(reader, persisted); await using var app = await StartAsync(fixture.Provider); using var client = Client(app);
        using var initial = JsonDocument.Parse(await client.GetStringAsync("/api/device-topology/fields"));
        Assert.Equal(JsonValueKind.Null, Field(initial.RootElement, PciResourceInformationCollectorTests.Identity, "pci_all_resource_range").GetProperty("value").ValueKind);
        Assert.Equal(0, reader.ReadCount);
        for (var phase = 0; phase < 3; phase++)
        {
            await fixture.Provider.SampleNowAsync(CancellationToken.None); using var result = JsonDocument.Parse(await client.GetStringAsync("/api/device-topology/fields"));
            var value = Field(result.RootElement, PciResourceInformationCollectorTests.Identity, "pci_all_resource_range").GetProperty("value");
            if (phase == 1) Assert.Equal(JsonValueKind.Null, value.ValueKind); else Assert.Equal("9007199254740993", value[1].GetProperty("startReported").GetString());
        }
        Assert.Equal(3, reader.ReadCount); await app.StopAsync();
    }

    [Fact]
    public async Task OriginalWorkerPciResourceStreamCancelsFutureReadsAndKeepsCachedDisplay()
    {
        var api = new PciResourceInformationCollectorTests.Api(); var reader = new Reader(() => PciResourceInformationCollectorTests.Snapshot(api));
        using var fixture = new Fixture(reader); await using var app = await StartAsync(fixture.Provider); using var client = Client(app);
        _ = await client.GetStringAsync("/api/device-topology/fields"); Assert.Equal(0, api.Opens);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/subscriptions/stream")
        { Content = JsonContent.Create(new { version = 1, subscriptions = new[] {
            new { id = "pci-resources-worker", path = "/api/device-topology/fields/subscribe?intervalMs=1000" } } }) };
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); using var stream = new StreamReader(await response.Content.ReadAsStreamAsync(timeout.Token));
        using var message = JsonDocument.Parse(await ReadFirstObservedInformationAsync(stream, timeout.Token));
        Assert.Equal("4294967296", Field(message.RootElement.GetProperty("value"), PciResourceInformationCollectorTests.Identity, "pci_all_resource_range").GetProperty("value")[3].GetProperty("lengthDerived").GetString());
        Assert.True(api.Opens >= 1); Assert.Equal(4 * api.Opens, api.DataCalls);
        await timeout.CancelAsync(); response.Dispose(); await Task.Delay(250);
        var count = api.Opens; var current = fixture.Provider.ReadState().InformationFields;
        using var cached = JsonDocument.Parse(await client.GetStringAsync("/api/device-topology/fields"));
        Assert.Equal(4, Field(cached.RootElement, PciResourceInformationCollectorTests.Identity, "pci_all_resource_range").GetProperty("value").GetArrayLength());
        await Task.Delay(1300); Assert.Equal(count, api.Opens); Assert.Equal(4 * count, api.DataCalls);
        Assert.Same(current, fixture.Provider.ReadState().InformationFields); await app.StopAsync();
    }
}
