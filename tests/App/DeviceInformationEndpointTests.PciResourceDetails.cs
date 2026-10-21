using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ResourceManager.App.Application.DeviceTopology;
using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.App.Infrastructure.DeviceTopology.Snapshots;
using Pci = Resource_Manager_APP.Tests.PciResourceInformationCollectorTests;

namespace Resource_Manager_APP.Tests;

public sealed partial class DeviceInformationEndpointTests
{
    [Fact]
    public async Task PciResourceDetailsHttpSeparatesCategoryFailureAbsenceAndRecoveryWithoutGetIo()
    {
        var api = Pci.MixedApi(); var frames = api.Descriptors;
        var reader = new Reader(() => Pci.DetailSnapshot(api));
        using var fixture = new Fixture(reader); await using var app = await StartAsync(fixture.Provider); using var client = Client(app);
        _ = await client.GetStringAsync("/api/device-topology/fields"); Assert.Equal(0, api.Opens);
        for (var phase = 0; phase < 5; phase++)
        {
            api.Fault = phase == 1 ? "data" : phase == 4 ? "first" : ""; api.FaultResource = 1004;
            api.Descriptors = phase == 2 ? [] : frames; await fixture.Provider.SampleNowAsync(CancellationToken.None);
            var opens = api.Opens; var calls = api.DataCalls; var current = fixture.Provider.ReadState().InformationFields;
            using var result = JsonDocument.Parse(await client.GetStringAsync("/api/device-topology/fields"));
            foreach (var name in new[] { "pci_all_resource_range", Pci.InterruptField, Pci.DmaField })
            {
                var field = Field(result.RootElement, Pci.Identity, name); var value = field.GetProperty("value");
                Assert.Equal("windows.pci-allocated-resources", field.GetProperty("definition").GetProperty("sourceId").GetString());
                if (phase == 4 || phase == 1 && name == Pci.InterruptField) Assert.Equal(JsonValueKind.Null, value.ValueKind);
                else if (phase == 2) Assert.Equal(0, value.GetArrayLength());
                else Assert.Equal(name == "pci_all_resource_range" ? 4 : 2, value.GetArrayLength());
            }
            Assert.Equal("Machine Model", Field(result.RootElement, "machine", "machine_all_model_declared").GetProperty("value").GetString());
            Assert.Equal(opens, api.Opens); Assert.Equal(calls, api.DataCalls); Assert.Same(current, fixture.Provider.ReadState().InformationFields);
        }
        await app.StopAsync();
    }

    [Fact]
    public async Task PciResourceDetailsPersistedAndWholeSourceFailureStayUnavailableUntilRecapture()
    {
        var raw = Pci.DetailSnapshot(Pci.MixedApi()); var reader = new Reader(raw, new InvalidOperationException("source failed"), raw);
        var persisted = new DeviceTopologyPersistedSnapshot(DeviceTopologySnapshotState.CurrentSchemaVersion, "test", 7,
            new DeviceTopologySemanticComparer().ComputeHash(raw), raw);
        using var fixture = new Fixture(reader, persisted); await using var app = await StartAsync(fixture.Provider); using var client = Client(app);
        using var initial = JsonDocument.Parse(await client.GetStringAsync("/api/device-topology/fields"));
        foreach (var name in new[] { Pci.InterruptField, Pci.DmaField })
            Assert.Equal(JsonValueKind.Null, Field(initial.RootElement, Pci.Identity, name).GetProperty("value").ValueKind);
        Assert.Equal(0, reader.ReadCount);
        for (var phase = 0; phase < 3; phase++)
        {
            await fixture.Provider.SampleNowAsync(CancellationToken.None); using var result = JsonDocument.Parse(await client.GetStringAsync("/api/device-topology/fields"));
            var irq = Field(result.RootElement, Pci.Identity, Pci.InterruptField).GetProperty("value");
            var dma = Field(result.RootElement, Pci.Identity, Pci.DmaField).GetProperty("value");
            if (phase == 1) { Assert.Equal(JsonValueKind.Null, irq.ValueKind); Assert.Equal(JsonValueKind.Null, dma.ValueKind); }
            else { Assert.Equal("9007199254740993", irq[0].GetProperty("processorAffinityMaskReported").GetString()); Assert.Equal(uint.MaxValue, dma[1].GetProperty("channelReported").GetUInt32()); }
        }
        Assert.Equal(3, reader.ReadCount); await app.StopAsync();
    }

    [Fact]
    public async Task OriginalWorkerPciResourceDetailsStreamStopsFutureIoAndRetainsCachedDisplayAfterCancel()
    {
        var api = Pci.MixedApi(); var reader = new Reader(() => Pci.DetailSnapshot(api));
        using var fixture = new Fixture(reader); await using var app = await StartAsync(fixture.Provider); using var client = Client(app);
        _ = await client.GetStringAsync("/api/device-topology/fields"); Assert.Equal(0, api.Opens);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/subscriptions/stream")
        { Content = JsonContent.Create(new { version = 1, subscriptions = new[] {
            new { id = "pci-resource-details-worker", path = "/api/device-topology/fields/subscribe?intervalMs=1000" } } }) };
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); using var stream = new StreamReader(await response.Content.ReadAsStreamAsync(timeout.Token));
        using var message = JsonDocument.Parse((await stream.ReadLineAsync(timeout.Token))!); var fields = message.RootElement.GetProperty("value");
        Assert.Equal("18446744073709551615", Field(fields, Pci.Identity, Pci.InterruptField).GetProperty("value")[1].GetProperty("processorAffinityMaskReported").GetString());
        Assert.Equal(uint.MaxValue, Field(fields, Pci.Identity, Pci.DmaField).GetProperty("value")[1].GetProperty("channelReported").GetUInt32());
        Assert.True(api.Opens >= 1); Assert.Equal(8 * api.Opens, api.DataCalls);
        await timeout.CancelAsync(); response.Dispose(); await Task.Delay(250);
        var opens = api.Opens; var current = fixture.Provider.ReadState().InformationFields;
        using var cached = JsonDocument.Parse(await client.GetStringAsync("/api/device-topology/fields"));
        Assert.Equal(2, Field(cached.RootElement, Pci.Identity, Pci.InterruptField).GetProperty("value").GetArrayLength());
        Assert.Equal(2, Field(cached.RootElement, Pci.Identity, Pci.DmaField).GetProperty("value").GetArrayLength());
        await Task.Delay(1300); Assert.Equal(opens, api.Opens); Assert.Equal(8 * opens, api.DataCalls);
        Assert.Same(current, fixture.Provider.ReadState().InformationFields); await app.StopAsync();
    }
}
