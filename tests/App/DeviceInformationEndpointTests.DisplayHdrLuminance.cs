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
    public async Task DisplayHdrLuminanceHttpDistinguishesEmptyArrayFailureAndRawWithoutGetIo()
    {
        var healthy = DisplayHdrLuminanceInformationCollectorTests.Healthy();
        var invalid = DisplayHdrLuminanceInformationCollectorTests.Bytes(); invalid[255] ^= 1;
        var incomplete = DisplayHdrInformationCollectorTests.Snapshot(DisplayHdrLuminanceInformationCollectorTests.Path(DisplayHdrLuminanceInformationCollectorTests.Bytes()) with { EdidObservationComplete = false });
        var reader = new Reader(healthy, DisplayHdrLuminanceInformationCollectorTests.Snapshot(invalid), incomplete,
            DisplayHdrLuminanceInformationCollectorTests.Snapshot(DisplayHdrLuminanceInformationCollectorTests.Edid()), healthy with { Ports = [] }, healthy);
        using var fixture = new Fixture(reader); await using var app = await StartAsync(fixture.Provider); using var client = Client(app);
        _ = await client.GetStringAsync("/api/device-topology/fields"); Assert.Equal(0, reader.ReadCount);
        for (var phase = 0; phase < 6; phase++)
        {
            await fixture.Provider.SampleNowAsync(CancellationToken.None); var reads = reader.ReadCount;
            using var document = JsonDocument.Parse(await client.GetStringAsync("/api/device-topology/fields"));
            if (phase == 4) Assert.DoesNotContain(document.RootElement.GetProperty("devices").EnumerateArray(), d => d.GetProperty("objectType").GetString() == "display_path");
            else
            {
                var value = Hdr(document.RootElement, DisplayHdrLuminanceInformationCollectorTests.Name).GetProperty("value");
                if (phase is 1 or 2) Assert.Equal(JsonValueKind.Null, value.ValueKind);
                else if (phase == 3) Assert.Equal(0, value.GetArrayLength());
                else Assert.Equal(800d, value[0].GetProperty("desiredContentMaxLuminanceNits").GetDouble());
                var raw = Hdr(document.RootElement, DisplayEdidRawInformationCollectorTests.Name).GetProperty("value");
                if (phase == 1) Assert.Equal(invalid, Convert.FromBase64String(raw.GetString()!));
                if (phase == 2) Assert.Equal(JsonValueKind.Null, raw.ValueKind);
                Assert.Equal("90", Hdr(document.RootElement, DisplayRotationInformationCollectorTests.Name).GetProperty("value").GetString());
            }
            Assert.Equal(reads, reader.ReadCount);
        }
        await app.StopAsync();
    }

    [Fact]
    public async Task DisplayHdrLuminancePersistedStartupAndWholeFailureDoNotPublishHistoricalBrightness()
    {
        var healthy = DisplayHdrLuminanceInformationCollectorTests.Healthy();
        var persisted = new DeviceTopologyPersistedSnapshot(DeviceTopologySnapshotState.CurrentSchemaVersion, "test", 1, new DeviceTopologySemanticComparer().ComputeHash(healthy), healthy);
        var reader = new Reader(healthy, new IOException("capture failed"), healthy); using var fixture = new Fixture(reader, persisted);
        await using var app = await StartAsync(fixture.Provider); using var client = Client(app);
        using var startup = JsonDocument.Parse(await client.GetStringAsync("/api/device-topology/fields"));
        Assert.Equal(JsonValueKind.Null, Hdr(startup.RootElement, DisplayHdrLuminanceInformationCollectorTests.Name).GetProperty("value").ValueKind); Assert.Equal(0, reader.ReadCount);
        for (var phase = 0; phase < 3; phase++)
        {
            await fixture.Provider.SampleNowAsync(CancellationToken.None); using var document = JsonDocument.Parse(await client.GetStringAsync("/api/device-topology/fields"));
            var value = Hdr(document.RootElement, DisplayHdrLuminanceInformationCollectorTests.Name).GetProperty("value");
            if (phase == 1) Assert.Equal(JsonValueKind.Null, value.ValueKind);
            else Assert.Equal(800d, value[0].GetProperty("desiredContentMaxLuminanceNits").GetDouble());
        }
        await app.StopAsync();
    }

    [Fact]
    public async Task DisplayHdrLuminanceUnifiedWorkerUsesOriginalDemandAndCancellationRetainsDeclaration()
    {
        var reader = new Reader(() => DisplayHdrLuminanceInformationCollectorTests.Healthy());
        using var fixture = new Fixture(reader); await using var app = await StartAsync(fixture.Provider); using var client = Client(app);
        _ = await client.GetStringAsync("/api/device-topology/fields"); Assert.Equal(0, reader.ReadCount);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12)); using var request = new HttpRequestMessage(HttpMethod.Post, "/api/subscriptions/stream")
        { Content = JsonContent.Create(new { version = 1, subscriptions = new[] { new { id = "display-hdr-luminance", path = "/api/device-topology/fields/subscribe?intervalMs=1000" } } }) };
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var stream = new StreamReader(await response.Content.ReadAsStreamAsync(timeout.Token)); using var message = JsonDocument.Parse(await ReadFirstObservedInformationAsync(stream, timeout.Token));
        var field = Hdr(message.RootElement.GetProperty("value"), DisplayHdrLuminanceInformationCollectorTests.Name);
        Assert.Equal(800d, field.GetProperty("value")[0].GetProperty("desiredContentMaxLuminanceNits").GetDouble());
        Assert.Equal(DisplayHdrLuminanceInformationCollectorTests.Source, field.GetProperty("definition").GetProperty("sourceId").GetString());
        Assert.True(reader.ReadCount >= 1); await timeout.CancelAsync(); response.Dispose(); await Task.Delay(250);
        var reads = reader.ReadCount; var cached = fixture.Provider.ReadState().InformationFields;
        using var displayed = JsonDocument.Parse(await client.GetStringAsync("/api/device-topology/fields"));
        Assert.Equal(800d, Hdr(displayed.RootElement, DisplayHdrLuminanceInformationCollectorTests.Name).GetProperty("value")[0].GetProperty("desiredContentMaxLuminanceNits").GetDouble());
        await Task.Delay(1300); Assert.Equal(reads, reader.ReadCount); Assert.Same(cached, fixture.Provider.ReadState().InformationFields); await app.StopAsync();
    }
}
