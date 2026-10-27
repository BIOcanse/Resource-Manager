using System.Text.Json;
using ResourceManager.App.Domain.DeviceTopology;

namespace Resource_Manager_APP.Tests;

// Use the existing real endpoint/owner fixture; no parallel topology owner or camera session.
public sealed partial class DeviceInformationEndpointTests
{
    [Fact]
    public async Task UvcIntervalsSingleFetchHttpKeepsIntegersClearsFailureRecoversAndRemovesAbsentDevice()
    {
        var healthy = UvcFrameIntervalCapabilityTests.Snapshot();
        var reader = new Reader(healthy, new InvalidOperationException("recorded descriptor capture failure"),
            healthy, DeviceInformationTestData.Snapshot());
        using var fixture = new Fixture(reader);
        await using var app = await StartAsync(fixture.Provider);
        using var client = Client(app);
        using (var initial = JsonDocument.Parse(ReadPublishedFields(fixture.Provider, app)))
            Assert.Empty(initial.RootElement.GetProperty("devices").EnumerateArray());
        Assert.Equal(0, reader.ReadCount);
        for (var phase = 0; phase < 4; phase++)
        {
            var sampledHttpFields = await client.GetStringAsync("/api/device-topology/fields");
            var current = fixture.Provider.ReadState().InformationFields;
            using var response = JsonDocument.Parse(sampledHttpFields);
            if (phase == 3)
                Assert.DoesNotContain(response.RootElement.GetProperty("devices").EnumerateArray(),
                    item => item.GetProperty("deviceIdentity").GetString() == UvcFrameIntervalCapabilityTests.Identity);
            else
            {
                var field = Field(response.RootElement, UvcFrameIntervalCapabilityTests.Identity,
                    UvcFrameIntervalCapabilityTests.FieldName);
                Assert.Equal("100ns", field.GetProperty("definition").GetProperty("unit").GetString());
                if (phase == 1) Assert.Equal(JsonValueKind.Null, field.GetProperty("value").ValueKind);
                else
                {
                    var row = field.GetProperty("value")[0];
                    Assert.Equal(UvcFrameIntervalCapabilityTests.Identity, row.GetProperty("usbDeviceIdentity").GetString());
                    Assert.Equal(new uint[] { 333_667, 666_667 }, row.GetProperty("frameIntervalsDeclared")
                        .GetProperty("discreteIntervals100Nanoseconds").EnumerateArray().Select(static item => item.GetUInt32()).ToArray());
                }
            }
            Assert.Same(current, fixture.Provider.ReadState().InformationFields);
            Assert.Equal(phase + 1, reader.ReadCount);
        }
        await app.StopAsync();
    }

    [Fact]
    public async Task UvcOriginalSubscriptionPublishesWholeCapabilityAndStopsOnDemandRelease()
    {
        var reader = new Reader(UvcFrameIntervalCapabilityTests.Snapshot());
        using var fixture = new Fixture(reader);
        await using var app = await StartAsync(fixture.Provider);
        using var client = Client(app);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/subscriptions/stream") {
            Content = System.Net.Http.Json.JsonContent.Create(new { version = 1, subscriptions = new[] {
                new { id = "uvc", path = "/api/device-topology/fields/subscribe?intervalMs=1000" } } }) };
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();
        using var stream = new StreamReader(await response.Content.ReadAsStreamAsync(timeout.Token));
        using var packet = JsonDocument.Parse(await ReadFirstObservedInformationAsync(stream, timeout.Token));
        var value = Field(packet.RootElement.GetProperty("value"), UvcFrameIntervalCapabilityTests.Identity,
            UvcFrameIntervalCapabilityTests.FieldName).GetProperty("value");
        Assert.Equal(666_667U, value[0].GetProperty("frameIntervalsDeclared")
            .GetProperty("defaultFrameInterval100Nanoseconds").GetUInt32());
        await timeout.CancelAsync();
        response.Dispose();
        await fixture.WaitForNoDemandAndIdleAsync();
        var reads = reader.ReadCount;
        var current = fixture.Provider.ReadState().InformationFields;
        using var cached = JsonDocument.Parse(ReadPublishedFields(fixture.Provider, app));
        Assert.Equal(reads, reader.ReadCount);
        Assert.Same(current, fixture.Provider.ReadState().InformationFields);
        await app.StopAsync();
    }
}
