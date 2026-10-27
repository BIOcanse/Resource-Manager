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
    public async Task DisplayHdrHttpKeepsIndependentStatesClearsLegacyAndRemovesAbsentPathThroughSingleFetch()
    {
        var healthy = DisplayHdrInformationCollectorTests.Healthy();
        var legacy = DisplayHdrInformationCollectorTests.Snapshot(DisplayRotationInformationCollectorTests.Path(0) with {
            AdvancedColor = new(true, true, false, false, "rgb", 10,
                HighDynamicRangeSupported: true, HighDynamicRangeUserEnabled: true, HighDynamicRangeActive: true) });
        var reader = new Reader(healthy, legacy, DisplayHdrInformationCollectorTests.Healthy(mode: 1),
            DisplayHdrInformationCollectorTests.Healthy(flags: 0, mode: 0), healthy with { Ports = [] }, healthy);
        using var fixture = new Fixture(reader); await using var app = await StartAsync(fixture.Provider); using var client = Client(app);
        _ = ReadPublishedFields(fixture.Provider, app); Assert.Equal(0, reader.ReadCount);
        for (var phase = 0; phase < 6; phase++)
        {
            var sampledHttpFields1 = await client.GetStringAsync("/api/device-topology/fields"); var reads = reader.ReadCount;
            using var document = JsonDocument.Parse(sampledHttpFields1);
            if (phase == 4) Assert.DoesNotContain(document.RootElement.GetProperty("devices").EnumerateArray(), d => d.GetProperty("objectType").GetString() == "display_path");
            else
            {
                if (phase == 1) foreach (var name in DisplayHdrInformationCollectorTests.Names) Assert.Equal(JsonValueKind.Null, Hdr(document.RootElement, name).GetProperty("value").ValueKind);
                else
                {
                    Assert.Equal(phase != 3, Hdr(document.RootElement, DisplayHdrInformationCollectorTests.Names[0]).GetProperty("value").GetBoolean());
                    Assert.Equal(phase != 3, Hdr(document.RootElement, DisplayHdrInformationCollectorTests.Names[1]).GetProperty("value").GetBoolean());
                    Assert.Equal(phase is 0 or 5, Hdr(document.RootElement, DisplayHdrInformationCollectorTests.Names[2]).GetProperty("value").GetBoolean());
                    Assert.Equal(phase == 3 ? "0" : phase == 2 ? "1" : "2", Hdr(document.RootElement, DisplayHdrInformationCollectorTests.Names[3]).GetProperty("value").GetString());
                }
                Assert.Equal(DisplayRotationInformationCollectorTests.Source, Hdr(document.RootElement, DisplayHdrInformationCollectorTests.Names[0]).GetProperty("definition").GetProperty("sourceId").GetString());
            }
            Assert.Equal(reads, reader.ReadCount);
        }
        await app.StopAsync();
    }

    [Fact]
    public async Task DisplayHdrPersistedStartupAndWholeFailureNeverExposeHistoricalModeAsCurrent()
    {
        var raw = DisplayHdrInformationCollectorTests.Healthy();
        var persisted = new DeviceTopologyPersistedSnapshot(DeviceTopologySnapshotState.CurrentSchemaVersion, "test", 1,
            new DeviceTopologySemanticComparer().ComputeHash(raw), raw);
        var reader = new Reader(raw, new IOException("capture failed"), DisplayHdrInformationCollectorTests.Healthy(mode: 1));
        using var fixture = new Fixture(reader, persisted); await using var app = await StartAsync(fixture.Provider); using var client = Client(app);
        using var startup = JsonDocument.Parse(ReadPublishedFields(fixture.Provider, app));
        foreach (var name in DisplayHdrInformationCollectorTests.Names) Assert.Equal(JsonValueKind.Null, Hdr(startup.RootElement, name).GetProperty("value").ValueKind);
        Assert.Equal(0, reader.ReadCount);
        for (var phase = 0; phase < 3; phase++)
        {
            var sampledHttpFields2 = await client.GetStringAsync("/api/device-topology/fields"); using var document = JsonDocument.Parse(sampledHttpFields2);
            if (phase == 1) foreach (var name in DisplayHdrInformationCollectorTests.Names) Assert.Equal(JsonValueKind.Null, Hdr(document.RootElement, name).GetProperty("value").ValueKind);
            else Assert.Equal(phase == 0 ? "2" : "1", Hdr(document.RootElement, DisplayHdrInformationCollectorTests.Names[3]).GetProperty("value").GetString());
        }
        await app.StopAsync();
    }

    [Fact]
    public async Task DisplayHdrUnifiedWorkerUsesOriginalDemandAndCancelKeepsReportedMode()
    {
        var reader = new Reader(() => DisplayHdrInformationCollectorTests.Healthy());
        using var fixture = new Fixture(reader); await using var app = await StartAsync(fixture.Provider); using var client = Client(app);
        _ = ReadPublishedFields(fixture.Provider, app); Assert.Equal(0, reader.ReadCount);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12)); using var request = new HttpRequestMessage(HttpMethod.Post, "/api/subscriptions/stream")
        { Content = JsonContent.Create(new { version = 1, subscriptions = new[] { new { id = "display-hdr", path = "/api/device-topology/fields/subscribe?intervalMs=1000" } } }) };
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var stream = new StreamReader(await response.Content.ReadAsStreamAsync(timeout.Token)); using var message = JsonDocument.Parse(await ReadFirstObservedInformationAsync(stream, timeout.Token));
        Assert.Equal("2", Hdr(message.RootElement.GetProperty("value"), DisplayHdrInformationCollectorTests.Names[3]).GetProperty("value").GetString());
        Assert.True(reader.ReadCount >= 1); await timeout.CancelAsync(); response.Dispose(); await Task.Delay(250);
        var reads = reader.ReadCount; var cached = fixture.Provider.ReadState().InformationFields;
        using var displayed = JsonDocument.Parse(ReadPublishedFields(fixture.Provider, app));
        Assert.True(Hdr(displayed.RootElement, DisplayHdrInformationCollectorTests.Names[2]).GetProperty("value").GetBoolean());
        await Task.Delay(1300); Assert.Equal(reads, reader.ReadCount); Assert.Same(cached, fixture.Provider.ReadState().InformationFields); await app.StopAsync();
    }

    private static JsonElement Hdr(JsonElement root, string name) => Assert.Single(
        Assert.Single(root.GetProperty("devices").EnumerateArray(), d => d.GetProperty("objectType").GetString() == "display_path").GetProperty("fields").EnumerateArray(),
        f => f.GetProperty("definition").GetProperty("name").GetString() == name);
}
