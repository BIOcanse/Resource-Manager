using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Resource_Manager_APP.Tests;

public sealed partial class DeviceInformationEndpointTests
{
    [Fact]
    public async Task Type3HttpReadsExactOriginalCurrentAndClearsOnlyFailedDeclarationThenRecovers()
    {
        var reader = new Reader(DisplayEdidType3ConsumerTestData.Snapshot(), DisplayEdidType3ConsumerTestData.Snapshot(unknown: true),
            new IOException("recorded TypeIII source failure"), DisplayEdidType3ConsumerTestData.Snapshot());
        using var fixture = new Fixture(reader); await using var app = await StartAsync(fixture.Provider); using var client = Client(app);
        _ = await client.GetStringAsync("/api/device-topology/fields"); _ = await client.GetStringAsync("/api/device-topology/state");
        Assert.Equal(0, reader.ReadCount);
        string? identity = null;
        for (var phase = 0; phase < 4; phase++) {
            await fixture.Provider.SampleNowAsync(CancellationToken.None); var reads = reader.ReadCount;
            using var fields = JsonDocument.Parse(await client.GetStringAsync("/api/device-topology/fields"));
            var device = Assert.Single(fields.RootElement.GetProperty("devices").EnumerateArray(), item => item.GetProperty("objectType").GetString() == "display_path");
            var currentIdentity = device.GetProperty("deviceIdentity").GetString(); identity ??= currentIdentity; Assert.Equal(identity, currentIdentity);
            var mode = Hdr(fields.RootElement, DisplayEdidModeInformationCollectorTests.Name);
            Assert.Equal(DisplayEdidModeInformationCollectorTests.Source, mode.GetProperty("definition").GetProperty("sourceId").GetString());
            if (phase is 0 or 3) DisplayEdidType3ConsumerTestData.AssertValue(mode.GetProperty("value"));
            else Assert.Equal(JsonValueKind.Null, mode.GetProperty("value").ValueKind);
            if (phase == 1) Assert.Equal(Convert.ToBase64String(DisplayEdidType3ConsumerTestData.Bytes(unknown: true)),
                Hdr(fields.RootElement, "display_all_edid_raw_declared").GetProperty("value").GetString());
            Assert.Equal(JsonValueKind.Null, Hdr(fields.RootElement, "display_all_resolution_actual").GetProperty("value").ValueKind);
            using var state = JsonDocument.Parse(await client.GetStringAsync("/api/device-topology/state"));
            Assert.False(state.RootElement.TryGetProperty("informationFields", out _)); Assert.Equal(reads, reader.ReadCount);
        }
        await app.StopAsync();
    }

    [Fact]
    public async Task Type3UnifiedSubscriptionUsesRealWorkerAndStopsWithoutErasingCurrent()
    {
        var reader = new Reader(() => DisplayEdidType3ConsumerTestData.Snapshot());
        using var fixture = new Fixture(reader); await using var app = await StartAsync(fixture.Provider); using var client = Client(app);
        _ = await client.GetStringAsync("/api/device-topology/fields"); Assert.Equal(0, reader.ReadCount);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/subscriptions/stream") {
            Content = JsonContent.Create(new { version = 1, subscriptions = new[] {
                new { id = "display-type3", path = "/api/device-topology/fields/subscribe?intervalMs=1000" } } }) };
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var stream = new StreamReader(await response.Content.ReadAsStreamAsync(timeout.Token));
        using var message = JsonDocument.Parse((await stream.ReadLineAsync(timeout.Token))!);
        DisplayEdidType3ConsumerTestData.AssertValue(Hdr(message.RootElement.GetProperty("value"), DisplayEdidModeInformationCollectorTests.Name).GetProperty("value"));
        Assert.True(reader.ReadCount >= 1); await timeout.CancelAsync(); response.Dispose(); await fixture.WaitForNoDemandAndIdleAsync();
        var reads = reader.ReadCount; var current = fixture.Provider.ReadState().InformationFields;
        using var cached = JsonDocument.Parse(await client.GetStringAsync("/api/device-topology/fields"));
        DisplayEdidType3ConsumerTestData.AssertValue(Hdr(cached.RootElement, DisplayEdidModeInformationCollectorTests.Name).GetProperty("value"));
        await Task.Delay(1100); Assert.Equal(reads, reader.ReadCount); Assert.Same(current, fixture.Provider.ReadState().InformationFields);
        await app.StopAsync();
    }
}
