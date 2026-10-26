using Resource_Manager_APP.Tests;
using System.Net.Http.Json;
using System.Text.Json;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.ReadWrite.ProcessFields;

namespace ResourceManager.App.Tests;

public sealed partial class ResourceMonitorDetailsSubscriptionTests
{
    public static IEnumerable<object[]> ResourceMeasureRequests()
    {
        foreach (var name in new[] { "process_all_cpu_usage_actual", "process_all_network_receive_rate_attributed",
            "process_all_network_send_rate_attributed", "process_all_network_receive_bytes_attributed",
            "process_all_network_send_bytes_attributed", "process_all_physical_disk_read_bytes_attributed",
            "process_all_physical_disk_write_bytes_attributed" }) yield return [name];
        yield return [ProcessGpuResourceFieldRegistry.ForEngine(0, new(123, 0, 2)).Name];
        yield return [ProcessGpuResourceFieldRegistry.ForResidentMemory(0, 123, ProcessMetricFieldCode.GpuResidentBytes).Name];
    }

    [Theory]
    [MemberData(nameof(ResourceMeasureRequests))]
    public async Task EveryEligibleResourceMeasureUsesTheSameBarsDetailsAndFactsDemand(string field)
    {
        var snapshot = ResourceMeasureFactsTests.FieldSnapshot(field, 100, 60);
        var source = new ResourceMonitorSource(snapshot) { Foundations = ResourceFactsPublicationTests.Foundations() };
        await using var app = await StartAsync(source);
        using var client = Client(app);
        foreach (var scope in new[] { "bars", "details" })
        foreach (var mode in new[] { "capacity", "active" })
        {
            var path = "?scope=" + scope + "&measures=" + field + "&modes=" + field + ":" + mode + "&processDetails=example";
            using var direct = JsonDocument.Parse(await client.GetStringAsync("/api/resource-monitor/snapshot" + path));
            if (scope == "bars")
            {
                var bar = Assert.Single(direct.RootElement.GetProperty("breakdown").GetProperty("bars").EnumerateArray());
                Assert.Equal(field, bar.GetProperty("metricId").GetString());
                Assert.Equal(ResourceBreakdownScaleModes.Normalize(ProcessMemoryFactFields.TotalMetricId(field), mode), bar.GetProperty("scaleMode").GetString());
                Assert.Equal(60, bar.GetProperty("processSumValue").GetDouble());
                Assert.Equal(40, bar.GetProperty("residualValue").GetDouble());
            }
            else
            {
                var metric = Assert.Single(direct.RootElement.GetProperty("metrics").EnumerateArray());
                Assert.Equal(field, metric.GetProperty("metricId").GetString());
                Assert.Equal(60, metric.GetProperty("software")[0].GetProperty("processes")[0][3].GetDouble());
            }
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/subscriptions/stream")
            { Content = JsonContent.Create(new { version = 1, subscriptions = new[] {
                new { id = "measure", path = "/api/resource-monitor/subscribe" + path } } }) },
                HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            response.EnsureSuccessStatusCode();
            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(timeout.Token));
            using var frame = JsonDocument.Parse((await reader.ReadLineAsync(timeout.Token))!);
            Assert.Equal(direct.RootElement.GetRawText(), frame.RootElement.GetProperty("value").GetRawText());
            var request = source.PushRequests.Last().Request;
            Assert.Equal([field], request.ProcessMemoryFactIds);
            Assert.Equal([field], request.MetricIds);
            await timeout.CancelAsync();
        }
        using var facts = JsonDocument.Parse(await client.GetStringAsync("/api/resource-facts/snapshot?measures=" + field));
        var fact = Assert.Single(facts.RootElement.GetProperty("metrics").EnumerateArray());
        Assert.Equal(ProcessMemoryFactFields.TotalMetricId(field), fact.GetProperty("totalMetricId").GetString());
        Assert.Equal(40, fact.GetProperty("residualValue").GetDouble());
        Assert.Equal([field], source.SnapshotRequests.Last().ProcessMemoryFactIds);
        await app.StopAsync();
    }
}

