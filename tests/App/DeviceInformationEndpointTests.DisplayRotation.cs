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
    public async Task DisplayRotationHttpPublishesChangesClearsUnavailableAndRemovesAbsentPathThroughSingleFetch()
    {
        var port = DisplayRotationInformationCollectorTests.Port(0);
        var initial = DeviceInformationTestData.Snapshot(port);
        var reader = new Reader(initial, initial with { Ports = [DisplayRotationInformationCollectorTests.Port(270)] },
            initial with { Ports = [port with { Display = port.Display! with { TargetAvailable = false } }] },
            initial with { Ports = [] }, initial);
        using var fixture = new Fixture(reader); await using var app = await StartAsync(fixture.Provider); using var client = Client(app);
        _ = ReadPublishedFields(fixture.Provider, app); Assert.Equal(0, reader.ReadCount);
        for (var phase = 0; phase < 5; phase++)
        {
            var sampledHttpFields1 = await client.GetStringAsync("/api/device-topology/fields"); var reads = reader.ReadCount;
            using var document = JsonDocument.Parse(sampledHttpFields1);
            if (phase == 3) Assert.DoesNotContain(document.RootElement.GetProperty("devices").EnumerateArray(), d => d.GetProperty("objectType").GetString() == "display_path");
            else
            {
                var field = Rotation(document.RootElement); var value = field.GetProperty("value");
                if (phase == 2) Assert.Equal(JsonValueKind.Null, value.ValueKind); else Assert.Equal(phase == 1 ? "270" : "0", value.GetString());
                Assert.Equal(DisplayRotationInformationCollectorTests.Source, field.GetProperty("definition").GetProperty("sourceId").GetString());
            }
            Assert.Equal(reads, reader.ReadCount);
        }
        await app.StopAsync();
    }

    [Fact]
    public async Task DisplayRotationPersistedStartupAndWholeFailureAreEmptyUntilSuccessfulCapture()
    {
        var raw = DeviceInformationTestData.Snapshot(DisplayRotationInformationCollectorTests.Port(180));
        var persisted = new DeviceTopologyPersistedSnapshot(DeviceTopologySnapshotState.CurrentSchemaVersion, "test", 1,
            new DeviceTopologySemanticComparer().ComputeHash(raw), raw);
        var reader = new Reader(raw, new IOException("capture failed"), raw); using var fixture = new Fixture(reader, persisted);
        await using var app = await StartAsync(fixture.Provider); using var client = Client(app);
        using var startup = JsonDocument.Parse(ReadPublishedFields(fixture.Provider, app));
        Assert.Equal(JsonValueKind.Null, Rotation(startup.RootElement).GetProperty("value").ValueKind); Assert.Equal(0, reader.ReadCount);
        for (var phase = 0; phase < 3; phase++)
        {
            var sampledHttpFields2 = await client.GetStringAsync("/api/device-topology/fields");
            using var document = JsonDocument.Parse(sampledHttpFields2);
            var value = Rotation(document.RootElement).GetProperty("value");
            if (phase == 1) Assert.Equal(JsonValueKind.Null, value.ValueKind); else Assert.Equal("180", value.GetString());
        }
        await app.StopAsync();
    }

    [Fact]
    public async Task DisplayRotationUnifiedWorkerRequiresDemandAndCancelKeepsLastDisplay()
    {
        var reader = new Reader(() => DeviceInformationTestData.Snapshot(DisplayRotationInformationCollectorTests.Port(90)));
        using var fixture = new Fixture(reader); await using var app = await StartAsync(fixture.Provider); using var client = Client(app);
        _ = ReadPublishedFields(fixture.Provider, app); Assert.Equal(0, reader.ReadCount);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12)); using var request = new HttpRequestMessage(HttpMethod.Post, "/api/subscriptions/stream")
        { Content = JsonContent.Create(new { version = 1, subscriptions = new[] { new { id = "display-rotation", path = "/api/device-topology/fields/subscribe?intervalMs=1000" } } }) };
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var stream = new StreamReader(await response.Content.ReadAsStreamAsync(timeout.Token));
        using var message = JsonDocument.Parse(await ReadFirstObservedInformationAsync(stream, timeout.Token));
        Assert.Equal("90", Rotation(message.RootElement.GetProperty("value")).GetProperty("value").GetString()); Assert.True(reader.ReadCount >= 1);
        await timeout.CancelAsync(); response.Dispose(); await Task.Delay(250); var reads = reader.ReadCount; var cache = fixture.Provider.ReadState().InformationFields;
        using var displayed = JsonDocument.Parse(ReadPublishedFields(fixture.Provider, app));
        Assert.Equal("90", Rotation(displayed.RootElement).GetProperty("value").GetString());
        await Task.Delay(1300); Assert.Equal(reads, reader.ReadCount); Assert.Same(cache, fixture.Provider.ReadState().InformationFields); await app.StopAsync();
    }

    private static JsonElement Rotation(JsonElement root) => Assert.Single(
        Assert.Single(root.GetProperty("devices").EnumerateArray(), d => d.GetProperty("objectType").GetString() == "display_path").GetProperty("fields").EnumerateArray(),
        f => f.GetProperty("definition").GetProperty("name").GetString() == DisplayRotationInformationCollectorTests.Name);
}
