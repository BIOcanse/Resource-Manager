using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ResourceManager.App.Infrastructure.ResourceBreakdown;
using NetworkTests = Resource_Manager_APP.Tests.ProcessNetworkFieldCollectorTests;

namespace Resource_Manager_APP.Tests;

[Collection(SoftwareIdentityCatalogProcessStateCollection.Name)]
public sealed partial class ProcessBasicFieldCollectorTests
{
    [Fact]
    public async Task ResourceFactsStreamsAnExistingNonResourceFieldAndReleasesItsDemand()
    {
        const string field = "process_all_handle_count_actual";
        using var catalog = new WindowsResourceBreakdownSamplerLifecycleTests.VersionedProcessAttributionCatalogProvider();
        using var fixture = new NetworkTests.Fixture(
            new NetworkTests.NetworkReader(_ => throw new InvalidOperationException("unexpected network source")),
            basic: new WindowsProcessBasicMetricReader(new Api()), attribution: catalog);
        await using var app = await NetworkTests.StartAsync(fixture);
        using var client = NetworkTests.Client(app);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/subscriptions/stream")
        { Content = JsonContent.Create(new { version = 1, subscriptions = new[] { new {
            id = "table-handles", path = $"/api/resource-facts/subscribe?items=process.inventory&items=process.attribution&items={field}&intervalMs=1000" } } }) };
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var stream = new StreamReader(await response.Content.ReadAsStreamAsync(timeout.Token));
        while (true)
        {
            var line = await stream.ReadLineAsync(timeout.Token);
            Assert.NotNull(line);
            using var frame = JsonDocument.Parse(line);
            var facts = frame.RootElement.GetProperty("value");
            var reading = Assert.Single(facts.GetProperty("fields").EnumerateArray());
            Assert.Equal(field, reading.GetProperty("field").GetString());
            Assert.False(facts.TryGetProperty("columns", out _));
            if (reading.GetProperty("capturedAt").ValueKind == JsonValueKind.Null) continue;
            var value = Assert.Single(reading.GetProperty("processes").EnumerateArray());
            Assert.Equal(Environment.ProcessId, facts.GetProperty("processes")[value.GetProperty("processIndex").GetInt32()]
                .GetProperty("processId").GetInt32());
            Assert.Equal("0", value.GetProperty("value").GetProperty("integerValue").GetString());
            break;
        }
        await timeout.CancelAsync(); response.Dispose();
        using var drain = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await WaitForProcessDemandDrainedAsync(fixture, drain.Token);
        await app.StopAsync();
    }
}
