using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ResourceManager.App.Application.DeviceTopology;
using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.App.Infrastructure.DeviceTopology.Snapshots;

namespace Resource_Manager_APP.Tests;

public sealed partial class DeviceInformationEndpointTests
{
    private static JsonElement EdidBasicDisplay(JsonElement state) => Assert.Single(state.GetProperty("snapshot").GetProperty("ports").EnumerateArray()).GetProperty("display");

    [Fact]
    public async Task EdidBasicCachedStatePublishesCorrectedDeclarationsWithoutGetIo()
    {
        var bytes = EdidBasicParameterValidationTests.Bytes(); var healthy = EdidBasicParameterValidationTests.Snapshot(bytes);
        bytes[19] = 3; EdidBasicParameterValidationTests.Checksum(bytes); var old = EdidBasicParameterValidationTests.Snapshot(bytes);
        bytes[19] = 4; bytes[21] = 79; bytes[22] = 0; EdidBasicParameterValidationTests.Checksum(bytes); var ratio = EdidBasicParameterValidationTests.Snapshot(bytes);
        var reader = new Reader(healthy, old, ratio, healthy); using var fixture = new Fixture(reader);
        await using var app = await StartAsync(fixture.Provider); using var client = Client(app);
        _ = await client.GetStringAsync("/api/device-topology/state"); Assert.Equal(0, reader.ReadCount);
        for (var phase = 0; phase < 4; phase++)
        {
            await fixture.Provider.SampleNowAsync(CancellationToken.None); var reads = reader.ReadCount;
            using var state = JsonDocument.Parse(await client.GetStringAsync("/api/device-topology/state")); var display = EdidBasicDisplay(state.RootElement);
            if (phase == 1) Assert.Equal(JsonValueKind.Null, display.GetProperty("bitsPerColorChannel").ValueKind);
            else Assert.Equal(10U, display.GetProperty("bitsPerColorChannel").GetUInt32());
            if (phase == 2) Assert.Equal(JsonValueKind.Null, display.GetProperty("physicalSize").ValueKind);
            else Assert.Equal("600 x 340 mm", display.GetProperty("physicalSize").GetString());
            using var fields = JsonDocument.Parse(await client.GetStringAsync("/api/device-topology/fields"));
            Assert.Equal(phase == 1 ? "EDID 1.3" : "EDID 1.4", display.GetProperty("edidVersion").GetString());
            Assert.Equal("90", Hdr(fields.RootElement, DisplayRotationInformationCollectorTests.Name).GetProperty("value").GetString());
            Assert.Equal(reads, reader.ReadCount);
        }
        await app.StopAsync();
    }

    [Fact]
    public async Task EdidBasicOriginalPersistenceAndFailureKeepCanonicalValuesUnobservedUntilSuccessfulSample()
    {
        var bytes = EdidBasicParameterValidationTests.Bytes(); bytes[19] = 3; EdidBasicParameterValidationTests.Checksum(bytes);
        var corrected = EdidBasicParameterValidationTests.Snapshot(bytes);
        var persisted = new DeviceTopologyPersistedSnapshot(DeviceTopologySnapshotState.CurrentSchemaVersion, "test", 1, new DeviceTopologySemanticComparer().ComputeHash(corrected), corrected);
        var reader = new Reader(corrected, new IOException("capture failed"), corrected); using var fixture = new Fixture(reader, persisted);
        await using var app = await StartAsync(fixture.Provider); using var client = Client(app);
        using var startup = JsonDocument.Parse(await client.GetStringAsync("/api/device-topology/fields"));
        Assert.Equal(JsonValueKind.Null, Hdr(startup.RootElement, DisplayEdidRawInformationCollectorTests.Name).GetProperty("value").ValueKind); Assert.Equal(0, reader.ReadCount);
        for (var phase = 0; phase < 3; phase++)
        {
            await fixture.Provider.SampleNowAsync(CancellationToken.None); using var fields = JsonDocument.Parse(await client.GetStringAsync("/api/device-topology/fields"));
            var raw = Hdr(fields.RootElement, DisplayEdidRawInformationCollectorTests.Name).GetProperty("value");
            if (phase == 1) Assert.Equal(JsonValueKind.Null, raw.ValueKind);
            else
            {
                Assert.Equal(bytes, Convert.FromBase64String(raw.GetString()!));
                using var state = JsonDocument.Parse(await client.GetStringAsync("/api/device-topology/state"));
                Assert.Equal(JsonValueKind.Null, EdidBasicDisplay(state.RootElement).GetProperty("bitsPerColorChannel").ValueKind);
            }
        }
        await app.StopAsync();
    }

    [Fact]
    public async Task EdidBasicOriginalStateSubscriptionKeepsCorrectedDisplayAfterCancellation()
    {
        var bytes = EdidBasicParameterValidationTests.Bytes(); bytes[21] = 79; bytes[22] = 0; EdidBasicParameterValidationTests.Checksum(bytes);
        var reader = new Reader(() => EdidBasicParameterValidationTests.Snapshot(bytes)); using var fixture = new Fixture(reader);
        await using var app = await StartAsync(fixture.Provider); using var client = Client(app);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12)); using var request = new HttpRequestMessage(HttpMethod.Post, "/api/subscriptions/stream")
        { Content = JsonContent.Create(new { version = 1, subscriptions = new[] { new { id = "edid-basic", path = "/api/device-topology/state/subscribe?intervalMs=1000" } } }) };
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var stream = new StreamReader(await response.Content.ReadAsStreamAsync(timeout.Token)); using var message = JsonDocument.Parse(await ReadFirstObservedInformationAsync(stream, timeout.Token, "lastSuccessAt"));
        var display = EdidBasicDisplay(message.RootElement.GetProperty("value")); Assert.Equal(JsonValueKind.Null, display.GetProperty("physicalSize").ValueKind);
        Assert.Equal(10U, display.GetProperty("bitsPerColorChannel").GetUInt32());
        await timeout.CancelAsync(); response.Dispose(); await Task.Delay(250); var reads = reader.ReadCount; var cached = fixture.Provider.ReadState();
        using var displayed = JsonDocument.Parse(await client.GetStringAsync("/api/device-topology/state"));
        Assert.Equal(JsonValueKind.Null, EdidBasicDisplay(displayed.RootElement).GetProperty("physicalSize").ValueKind);
        await Task.Delay(1300); Assert.Equal(reads, reader.ReadCount); Assert.Same(cached, fixture.Provider.ReadState()); await app.StopAsync();
    }
}
