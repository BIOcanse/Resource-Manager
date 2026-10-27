using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Resource_Manager_APP.Tests;

public sealed partial class DeviceInformationEndpointTests
{
    [Fact]
    public async Task TypeVViCachedInspectionAndUnifiedSubscriptionUseTheRealParserAndRetainCurrentOnStop()
    {
        var reader = new Reader(() => DisplayEdidTypeVViTestData.Snapshot());
        using var fixture = new Fixture(reader);
        await using var app = await StartAsync(fixture.Provider);
        using var client = Client(app);
        _ = ReadPublishedFields(fixture.Provider, app);
        _ = ReadPublishedState(fixture.Provider, app);
        Assert.Equal(0, reader.ReadCount);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/subscriptions/stream")
        {
            Content = JsonContent.Create(new
            {
                version = 1,
                subscriptions = new[]
                {
                    new { id = "displayid-type-v-vi", path = "/api/device-topology/fields/subscribe?intervalMs=1000" }
                }
            })
        };
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var stream = new StreamReader(await response.Content.ReadAsStreamAsync(timeout.Token));
        using var message = JsonDocument.Parse(await ReadFirstObservedInformationAsync(stream, timeout.Token));
        var delivered = Hdr(message.RootElement.GetProperty("value"), DisplayEdidModeInformationCollectorTests.Name);
        DisplayEdidTypeVViTestData.AssertValue(delivered.GetProperty("value"));
        Assert.True(reader.ReadCount >= 1);

        await timeout.CancelAsync();
        response.Dispose();
        await fixture.WaitForNoDemandAndIdleAsync();
        var reads = reader.ReadCount;
        var current = fixture.Provider.ReadState().InformationFields;
        using var passive = JsonDocument.Parse(ReadPublishedFields(fixture.Provider, app));
        DisplayEdidTypeVViTestData.AssertValue(
            Hdr(passive.RootElement, DisplayEdidModeInformationCollectorTests.Name).GetProperty("value"));
        await Task.Delay(1100);
        Assert.Equal(reads, reader.ReadCount);
        Assert.Same(current, fixture.Provider.ReadState().InformationFields);
        await app.StopAsync();
    }
}
