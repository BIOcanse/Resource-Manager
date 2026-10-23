using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Resource_Manager_APP.Tests;

public sealed partial class DeviceInformationEndpointTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisplayIdVIIIHttpUsesOriginalCurrentAndWholeUnknownKeepsRaw(bool originalVic)
    {
        Action<JsonElement> assertModeValue = value => { if (originalVic) DisplayIdVicTimingTestData.AssertValue(value); else DisplayIdCodeTimingTestData.AssertValue(value); };
        var bytes = (originalVic ? DisplayIdVicTimingTestData.Bytes() : DisplayIdCodeTimingTestData.Bytes()); bytes[520] = 0; DisplayIdVicTimingTestData.RepairChecksums(bytes);
        var invalid = DisplayEdidModeTestData.Snapshot(DisplayEdidModeTestData.Path(ResourceManager.ReadWrite.Windows.Devices.Display.WindowsMonitorEdidReader.CreateReadResult(bytes)));
        var reader = new Reader((originalVic ? DisplayIdVicTimingTestData.Snapshot() : DisplayIdCodeTimingTestData.Snapshot()), invalid, (originalVic ? DisplayIdVicTimingTestData.Snapshot() : DisplayIdCodeTimingTestData.Snapshot()));
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
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisplayIdVIIIUnifiedWorkerDeliversNewDeclarationsAndStopRetainsCurrent(bool originalVic)
    {
        Action<JsonElement> assertModeValue = value => { if (originalVic) DisplayIdVicTimingTestData.AssertValue(value); else DisplayIdCodeTimingTestData.AssertValue(value); };
        var reader = new Reader(() => (originalVic ? DisplayIdVicTimingTestData.Snapshot() : DisplayIdCodeTimingTestData.Snapshot()));
        using var fixture = new Fixture(reader); await using var app = await StartAsync(fixture.Provider); using var client = Client(app);
        _ = await client.GetStringAsync("/api/device-topology/fields"); Assert.Equal(0, reader.ReadCount);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/subscriptions/stream") { Content = JsonContent.Create(new { version = 1, subscriptions = new[] {
            new { id = "displayid-viii", path = "/api/device-topology/fields/subscribe?intervalMs=1000" } } }) };
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var stream = new StreamReader(await response.Content.ReadAsStreamAsync(timeout.Token));
        using var message = JsonDocument.Parse(await ReadFirstObservedInformationAsync(stream, timeout.Token));
        assertModeValue(Hdr(message.RootElement.GetProperty("value"), DisplayIdCodeTimingTestData.Name).GetProperty("value")); Assert.True(reader.ReadCount >= 1);
        await timeout.CancelAsync(); response.Dispose(); await fixture.WaitForNoDemandAndIdleAsync();
        var reads = reader.ReadCount; var current = fixture.Provider.ReadState().InformationFields;
        using var displayed = JsonDocument.Parse(await client.GetStringAsync("/api/device-topology/fields"));
        assertModeValue(Hdr(displayed.RootElement, DisplayIdCodeTimingTestData.Name).GetProperty("value"));
        await Task.Delay(1100); Assert.Equal(reads, reader.ReadCount); Assert.Same(current, fixture.Provider.ReadState().InformationFields);
        await app.StopAsync();
    }
}
