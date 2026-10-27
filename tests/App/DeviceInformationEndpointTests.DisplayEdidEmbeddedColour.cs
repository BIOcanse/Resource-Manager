using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ResourceManager.TestFixtures;

namespace Resource_Manager_APP.Tests;

public sealed partial class DeviceInformationEndpointTests
{
    [Theory]
     [InlineData(0x20)]
    public async Task EmbeddedColourHttpFetchesCurrentAndKeepsRawAcrossFailure(byte version)
    {
        var reader = new Reader(DisplayEdidEmbeddedColourTestData.Snapshot(version),
            DisplayEdidEmbeddedColourTestData.Snapshot(version, unsupported: true),
            new IOException("recorded embedded-colour read failure"), DisplayEdidEmbeddedColourTestData.Snapshot(version));
        using var fixture = new Fixture(reader); await using var app = await StartAsync(fixture.Provider); using var client = Client(app);
        _ = ReadPublishedFields(fixture.Provider, app); _ = ReadPublishedState(fixture.Provider, app);
        Assert.Equal(0, reader.ReadCount); string? identity = null;
        for (var phase = 0; phase < 4; phase++)
        {
            var sampledHttpFields1 = await client.GetStringAsync("/api/device-topology/fields"); var reads = reader.ReadCount;
            using var fields = JsonDocument.Parse(sampledHttpFields1);
            var device = Assert.Single(fields.RootElement.GetProperty("devices").EnumerateArray(), item => item.GetProperty("objectType").GetString() == "display_path");
            var currentIdentity = device.GetProperty("deviceIdentity").GetString(); identity ??= currentIdentity; Assert.Equal(identity, currentIdentity);
            var mode = Hdr(fields.RootElement, DisplayEdidModeInformationCollectorTests.Name);
            Assert.Equal(DisplayEdidModeInformationCollectorTests.Source, mode.GetProperty("definition").GetProperty("sourceId").GetString());
            if (phase is 0 or 3) DisplayEdidEmbeddedColourTestData.AssertValue(mode.GetProperty("value"), version);
            else Assert.Equal(JsonValueKind.Null, mode.GetProperty("value").ValueKind);
            if (phase == 1) Assert.Equal(Convert.ToBase64String(DisplayEdidEmbeddedColourTestData.Bytes(version, unsupported: true)),
                Hdr(fields.RootElement, "display_all_edid_raw_declared").GetProperty("value").GetString());
            Assert.Equal(JsonValueKind.Null, Hdr(fields.RootElement, "display_all_resolution_actual").GetProperty("value").ValueKind);
            using var state = JsonDocument.Parse(ReadPublishedState(fixture.Provider, app));
            AssertPublishedInformationFieldsMatchResponse(state.RootElement, fields.RootElement); Assert.Equal(reads, reader.ReadCount);
        }
        await app.StopAsync();
    }

    [Theory]
     [InlineData(0x20)]
    public async Task EmbeddedColourUnifiedSubscriptionUsesTheRealWorkerAndStopsWithoutErasingCurrent(byte version)
    {
        var reader = new Reader(() => DisplayEdidEmbeddedColourTestData.Snapshot(version));
        using var fixture = new Fixture(reader); await using var app = await StartAsync(fixture.Provider); using var client = Client(app);
        _ = ReadPublishedFields(fixture.Provider, app); Assert.Equal(0, reader.ReadCount);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/subscriptions/stream") {
            Content = JsonContent.Create(new { version = 1, subscriptions = new[] {
                new { id = "display-colour", path = "/api/device-topology/fields/subscribe?intervalMs=1000" } } }) };
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var stream = new StreamReader(await response.Content.ReadAsStreamAsync(timeout.Token));
        using var message = JsonDocument.Parse(await ReadFirstObservedInformationAsync(stream, timeout.Token));
        DisplayEdidEmbeddedColourTestData.AssertValue(Hdr(message.RootElement.GetProperty("value"), DisplayEdidModeInformationCollectorTests.Name).GetProperty("value"), version);
        Assert.True(reader.ReadCount >= 1); await timeout.CancelAsync(); response.Dispose(); await fixture.WaitForNoDemandAndIdleAsync();
        var reads = reader.ReadCount; var current = fixture.Provider.ReadState().InformationFields;
        using var cached = JsonDocument.Parse(ReadPublishedFields(fixture.Provider, app));
        DisplayEdidEmbeddedColourTestData.AssertValue(Hdr(cached.RootElement, DisplayEdidModeInformationCollectorTests.Name).GetProperty("value"), version);
        await Task.Delay(1100); Assert.Equal(reads, reader.ReadCount); Assert.Same(current, fixture.Provider.ReadState().InformationFields);
        await app.StopAsync();
    }
}
