using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Resource_Manager_APP.Tests;

public sealed partial class DeviceInformationEndpointTests
{
    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 0)]
    [InlineData(true, 0x12)]
    public async Task DisplayIdIVHttpUsesOriginalCurrentAndWholeUnknownKeepsRaw(bool originalVic, byte bitmapRevision)
    {
        Action<JsonElement> assertModeValue = value => { if (bitmapRevision != 0) DisplayIdBitmapTestData.AssertValue(value, bitmapRevision); else if (originalVic) DisplayIdVicTimingTestData.AssertLegacyValue(value); else DisplayIdCodeTimingTestData.AssertLegacyValue(value); };
        var bytes = (bitmapRevision != 0 ? DisplayIdBitmapTestData.Bytes(bitmapRevision) : originalVic ? DisplayIdVicTimingTestData.LegacyBytes() : DisplayIdCodeTimingTestData.LegacyBytes()); bytes[bitmapRevision != 0 ? 533 : 520] = bitmapRevision != 0 ? (byte)1 : (byte)0; DisplayIdVicTimingTestData.RepairChecksums(bytes);
        var invalid = DisplayEdidModeTestData.Snapshot(DisplayEdidModeTestData.Path(ResourceManager.ReadWrite.Windows.Devices.Display.WindowsMonitorEdidReader.CreateReadResult(bytes)));
        var reader = new Reader((bitmapRevision != 0 ? DisplayIdBitmapTestData.Snapshot(bitmapRevision) : originalVic ? DisplayIdVicTimingTestData.LegacySnapshot() : DisplayIdCodeTimingTestData.LegacySnapshot()), invalid, (bitmapRevision != 0 ? DisplayIdBitmapTestData.Snapshot(bitmapRevision) : originalVic ? DisplayIdVicTimingTestData.LegacySnapshot() : DisplayIdCodeTimingTestData.LegacySnapshot()));
        using var fixture = new Fixture(reader); await using var app = await StartAsync(fixture.Provider); using var client = Client(app);
        _ = await client.GetStringAsync("/api/device-topology/fields"); Assert.Equal(0, reader.ReadCount);
        for(var phase = 0; phase < 3; phase++)
        {
            await fixture.Provider.SampleNowAsync(CancellationToken.None); var reads = reader.ReadCount;
            using var fields = JsonDocument.Parse(await client.GetStringAsync("/api/device-topology/fields"));
            var value = Hdr(fields.RootElement, DisplayIdCodeTimingTestData.Name).GetProperty("value");
            if(phase == 1) Assert.Equal(JsonValueKind.Null, value.ValueKind); else assertModeValue(value);
            if(phase == 1) Assert.Equal(Convert.ToBase64String(bytes), Hdr(fields.RootElement, "display_all_edid_raw_declared").GetProperty("value").GetString());
            using var state = JsonDocument.Parse(await client.GetStringAsync("/api/device-topology/state")); Assert.False(state.RootElement.TryGetProperty("informationFields", out _));
            Assert.Equal(reads, reader.ReadCount);
        }
        await app.StopAsync();
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 0)]
    [InlineData(true, 0x12)]
    public async Task DisplayIdIVUnifiedWorkerDeliversNewDeclarationsAndStopRetainsCurrent(bool originalVic, byte bitmapRevision)
    {
        Action<JsonElement> assertModeValue = value => { if (bitmapRevision != 0) DisplayIdBitmapTestData.AssertValue(value, bitmapRevision); else if (originalVic) DisplayIdVicTimingTestData.AssertLegacyValue(value); else DisplayIdCodeTimingTestData.AssertLegacyValue(value); };
        var reader = new Reader(() => (bitmapRevision != 0 ? DisplayIdBitmapTestData.Snapshot(bitmapRevision) : originalVic ? DisplayIdVicTimingTestData.LegacySnapshot() : DisplayIdCodeTimingTestData.LegacySnapshot()));
        using var fixture = new Fixture(reader); await using var app = await StartAsync(fixture.Provider); using var client = Client(app);
        _ = await client.GetStringAsync("/api/device-topology/fields"); Assert.Equal(0, reader.ReadCount);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/subscriptions/stream") { Content = JsonContent.Create(new { version = 1, subscriptions = new[] {
            new { id = "displayid-iv", path = "/api/device-topology/fields/subscribe?intervalMs=1000" } } }) };
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var stream = new StreamReader(await response.Content.ReadAsStreamAsync(timeout.Token));
        using var message = JsonDocument.Parse((await stream.ReadLineAsync(timeout.Token))!);
        assertModeValue(Hdr(message.RootElement.GetProperty("value"), DisplayIdCodeTimingTestData.Name).GetProperty("value")); Assert.True(reader.ReadCount >= 1);
        await timeout.CancelAsync(); response.Dispose(); await fixture.WaitForNoDemandAndIdleAsync();
        var reads = reader.ReadCount; var current = fixture.Provider.ReadState().InformationFields;
        using var displayed = JsonDocument.Parse(await client.GetStringAsync("/api/device-topology/fields"));
        assertModeValue(Hdr(displayed.RootElement, DisplayIdCodeTimingTestData.Name).GetProperty("value"));
        await Task.Delay(1100); Assert.Equal(reads, reader.ReadCount); Assert.Same(current, fixture.Provider.ReadState().InformationFields);
        await app.StopAsync();
    }
}
