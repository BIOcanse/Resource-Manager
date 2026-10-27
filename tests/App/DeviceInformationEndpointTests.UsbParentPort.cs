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
    private static JsonElement UsbParent(JsonElement root) => Field(root, UsbParentPortInformationCollectorTests.Identity, UsbParentPortInformationCollectorTests.Name);

    [Fact]
    public async Task UsbParentHttpDoesNotTreatConnectorRepresentativeAsParentThroughSingleFetch()
    {
        var healthy = UsbParentPortInformationCollectorTests.Healthy();
        var unmatched = UsbParentPortInformationCollectorTests.Snapshot(WindowsDeviceTopologyReader.CreateUsbConnection(UsbParentPortInformationCollectorTests.Raw(), false)!);
        var reader = new Reader(healthy, unmatched, healthy with { Ports = [] }, healthy);
        using var fixture = new Fixture(reader); await using var app = await StartAsync(fixture.Provider); using var client = Client(app);
        _ = ReadPublishedFields(fixture.Provider, app); Assert.Equal(0, reader.ReadCount);
        for (var phase = 0; phase < 4; phase++)
        {
            var sampledHttpFields1 = await client.GetStringAsync("/api/device-topology/fields"); var reads = reader.ReadCount;
            using var document = JsonDocument.Parse(sampledHttpFields1);
            if (phase == 2) Assert.DoesNotContain(document.RootElement.GetProperty("devices").EnumerateArray(), d => d.GetProperty("objectType").GetString() == "usb_device");
            else
            {
                var value = UsbParent(document.RootElement).GetProperty("value");
                if (phase == 1) Assert.Equal(JsonValueKind.Null, value.ValueKind);
                else Assert.Equal(UsbParentPortInformationCollectorTests.Parent, value.GetString());
                Assert.Equal("Product", Field(document.RootElement, UsbParentPortInformationCollectorTests.Identity, "usb_all_product_name_declared").GetProperty("value").GetString());
            }
            Assert.Equal(reads, reader.ReadCount);
        }
        await app.StopAsync();
    }

    [Fact]
    public async Task UsbParentPersistedStartupAndCaptureFailureClearHistoricalParentAndRecover()
    {
        var healthy = UsbParentPortInformationCollectorTests.Healthy();
        var persisted = new DeviceTopologyPersistedSnapshot(DeviceTopologySnapshotState.CurrentSchemaVersion, "test", 1, new DeviceTopologySemanticComparer().ComputeHash(healthy), healthy);
        var reader = new Reader(healthy, new IOException("capture failed"), healthy); using var fixture = new Fixture(reader, persisted);
        await using var app = await StartAsync(fixture.Provider); using var client = Client(app);
        using var startup = JsonDocument.Parse(ReadPublishedFields(fixture.Provider, app));
        Assert.Equal(JsonValueKind.Null, UsbParent(startup.RootElement).GetProperty("value").ValueKind); Assert.Equal(0, reader.ReadCount);
        for (var phase = 0; phase < 3; phase++)
        {
            var sampledHttpFields2 = await client.GetStringAsync("/api/device-topology/fields"); using var document = JsonDocument.Parse(sampledHttpFields2);
            var value = UsbParent(document.RootElement).GetProperty("value");
            if (phase == 1) Assert.Equal(JsonValueKind.Null, value.ValueKind);
            else Assert.Equal(UsbParentPortInformationCollectorTests.Parent, value.GetString());
        }
        await app.StopAsync();
    }

    [Fact]
    public async Task UsbParentUnifiedSubscriptionCancellationStopsFutureReadsAndKeepsDisplay()
    {
        var reader = new Reader(() => UsbParentPortInformationCollectorTests.Healthy());
        using var fixture = new Fixture(reader); await using var app = await StartAsync(fixture.Provider); using var client = Client(app);
        _ = ReadPublishedFields(fixture.Provider, app); Assert.Equal(0, reader.ReadCount);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12)); using var request = new HttpRequestMessage(HttpMethod.Post, "/api/subscriptions/stream")
        { Content = JsonContent.Create(new { version = 1, subscriptions = new[] { new { id = "usb-parent-port", path = "/api/device-topology/fields/subscribe?intervalMs=1000" } } }) };
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var stream = new StreamReader(await response.Content.ReadAsStreamAsync(timeout.Token)); using var message = JsonDocument.Parse(await ReadFirstObservedInformationAsync(stream, timeout.Token));
        var field = UsbParent(message.RootElement.GetProperty("value")); Assert.Equal(UsbParentPortInformationCollectorTests.Parent, field.GetProperty("value").GetString());
        Assert.Equal(UsbParentPortInformationCollectorTests.Source, field.GetProperty("definition").GetProperty("sourceId").GetString());
        Assert.True(reader.ReadCount >= 1); await timeout.CancelAsync(); response.Dispose(); await Task.Delay(250);
        var reads = reader.ReadCount; var cached = fixture.Provider.ReadState().InformationFields;
        using var displayed = JsonDocument.Parse(ReadPublishedFields(fixture.Provider, app));
        Assert.Equal(UsbParentPortInformationCollectorTests.Parent, UsbParent(displayed.RootElement).GetProperty("value").GetString());
        await Task.Delay(1300); Assert.Equal(reads, reader.ReadCount); Assert.Same(cached, fixture.Provider.ReadState().InformationFields); await app.StopAsync();
    }
}
