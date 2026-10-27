using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ResourceManager.App.Application.DeviceTopology;
using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.App.Infrastructure.DeviceTopology.Snapshots;

namespace Resource_Manager_APP.Tests;

public sealed partial class DeviceInformationEndpointTests
{
    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    public async Task DisplayChromaticityHttpKeepsRawAndIndependentModeWhenDeclarationFailsThroughSingleFetch(int revision)
    {
        var healthy = DisplayChromaticityInformationCollectorTests.Healthy(revision);
        var invalid = DisplayChromaticityInformationCollectorTests.Bytes(revision); invalid[127] ^= 1;
        var incomplete = DisplayHdrInformationCollectorTests.Snapshot(DisplayChromaticityInformationCollectorTests.Path(DisplayChromaticityInformationCollectorTests.Bytes(revision))
            with { EdidObservationComplete = false });
        var reader = new Reader(healthy, DisplayChromaticityInformationCollectorTests.Snapshot(invalid), incomplete, healthy with { Ports = [] }, healthy);
        using var fixture = new Fixture(reader); await using var app = await StartAsync(fixture.Provider); using var client = Client(app);
        _ = ReadPublishedFields(fixture.Provider, app); Assert.Equal(0, reader.ReadCount);
        for (var phase = 0; phase < 5; phase++)
        {
            var sampledHttpFields1 = await client.GetStringAsync("/api/device-topology/fields"); var reads = reader.ReadCount;
            using var document = JsonDocument.Parse(sampledHttpFields1);
            if (phase == 3) Assert.DoesNotContain(document.RootElement.GetProperty("devices").EnumerateArray(), d => d.GetProperty("objectType").GetString() == "display_path");
            else
            {
                var value = Hdr(document.RootElement, DisplayChromaticityInformationCollectorTests.Name).GetProperty("value");
                if (phase is 1 or 2) Assert.Equal(JsonValueKind.Null, value.ValueKind);
                else Assert.Equal(0.5, value.GetProperty("defaultWhite").GetProperty("x").GetDouble());
                var raw = Hdr(document.RootElement, DisplayEdidRawInformationCollectorTests.Name).GetProperty("value");
                if (phase == 1) Assert.Equal(invalid, Convert.FromBase64String(raw.GetString()!));
                if (phase == 2) Assert.Equal(JsonValueKind.Null, raw.ValueKind);
                Assert.Equal("90", Hdr(document.RootElement, DisplayRotationInformationCollectorTests.Name).GetProperty("value").GetString());
            }
            Assert.Equal(reads, reader.ReadCount);
        }
        await app.StopAsync();
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    public async Task DisplayChromaticityPersistedStartupAndWholeFailureDoNotPublishHistoricalCoordinates(int revision)
    {
        var healthy = DisplayChromaticityInformationCollectorTests.Healthy(revision);
        var persisted = new DeviceTopologyPersistedSnapshot(DeviceTopologySnapshotState.CurrentSchemaVersion, "test", 1, new DeviceTopologySemanticComparer().ComputeHash(healthy), healthy);
        var reader = new Reader(healthy, new IOException("capture failed"), healthy); using var fixture = new Fixture(reader, persisted);
        await using var app = await StartAsync(fixture.Provider); using var client = Client(app);
        using var startup = JsonDocument.Parse(ReadPublishedFields(fixture.Provider, app));
        Assert.Equal(JsonValueKind.Null, Hdr(startup.RootElement, DisplayChromaticityInformationCollectorTests.Name).GetProperty("value").ValueKind); Assert.Equal(0, reader.ReadCount);
        for (var phase = 0; phase < 3; phase++)
        {
            var sampledHttpFields2 = await client.GetStringAsync("/api/device-topology/fields"); using var document = JsonDocument.Parse(sampledHttpFields2);
            var value = Hdr(document.RootElement, DisplayChromaticityInformationCollectorTests.Name).GetProperty("value");
            if (phase == 1) Assert.Equal(JsonValueKind.Null, value.ValueKind);
            else Assert.Equal(0.5, value.GetProperty("defaultWhite").GetProperty("x").GetDouble());
        }
        await app.StopAsync();
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    public async Task DisplayChromaticityUnifiedWorkerUsesOriginalDemandAndCancellationRetainsCoordinates(int revision)
    {
        var reader = new Reader(() => DisplayChromaticityInformationCollectorTests.Healthy(revision));
        using var fixture = new Fixture(reader); await using var app = await StartAsync(fixture.Provider); using var client = Client(app);
        _ = ReadPublishedFields(fixture.Provider, app); Assert.Equal(0, reader.ReadCount);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12)); using var request = new HttpRequestMessage(HttpMethod.Post, "/api/subscriptions/stream")
        { Content = JsonContent.Create(new { version = 1, subscriptions = new[] { new { id = "display-chromaticity", path = "/api/device-topology/fields/subscribe?intervalMs=1000" } } }) };
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var stream = new StreamReader(await response.Content.ReadAsStreamAsync(timeout.Token)); using var message = JsonDocument.Parse(await ReadFirstObservedInformationAsync(stream, timeout.Token));
        var field = Hdr(message.RootElement.GetProperty("value"), DisplayChromaticityInformationCollectorTests.Name);
        Assert.Equal(0.5, field.GetProperty("value").GetProperty("defaultWhite").GetProperty("x").GetDouble());
        Assert.Equal(DisplayChromaticityInformationCollectorTests.Source, field.GetProperty("definition").GetProperty("sourceId").GetString());
        Assert.True(reader.ReadCount >= 1); await timeout.CancelAsync(); response.Dispose(); await Task.Delay(250);
        var reads = reader.ReadCount; var cached = fixture.Provider.ReadState().InformationFields;
        using var displayed = JsonDocument.Parse(ReadPublishedFields(fixture.Provider, app));
        Assert.Equal(0.5, Hdr(displayed.RootElement, DisplayChromaticityInformationCollectorTests.Name).GetProperty("value").GetProperty("defaultWhite").GetProperty("x").GetDouble());
        await Task.Delay(1300); Assert.Equal(reads, reader.ReadCount); Assert.Same(cached, fixture.Provider.ReadState().InformationFields); await app.StopAsync();
    }
}
