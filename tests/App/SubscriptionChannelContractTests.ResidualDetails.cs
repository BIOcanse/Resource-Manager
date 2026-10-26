using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ResourceManager.App.Domain.ResourceBreakdown;
using Resource_Manager_APP.Tests;

namespace ResourceManager.App.Tests;

public sealed partial class ResourceMonitorDetailsSubscriptionTests
{
    [Theory]
    [InlineData("bars", "ids=memory.usage", "memory.usage")]
    [InlineData("details", "ids=memory.usage", "memory.usage")]
    [InlineData("bars", "measures=process_all_private_working_set_actual", "memory.usage")]
    [InlineData("details", "measures=process_all_private_working_set_actual", "memory.usage")]
    [InlineData("details", "measures=process_all_private_working_set_actual", "process_all_private_working_set_actual")]
    [InlineData("details", "ids=memory.usage", "MEMORY.USAGE,memory.usage&residualDetails=memory.usage")]
    public async Task ResidualDetailsHttpSelectsCounterBreakdown(string scope, string selection, string residual)
    {
        var source = ResidualHttpSource();
        await using var app = await StartAsync(source);
        using var client = Client(app);
        var query = $"?scope={scope}&{selection}&residualDetails={residual}";
        using var direct = JsonDocument.Parse(await client.GetStringAsync("/api/resource-monitor/snapshot" + query));
        AssertResidualHttpRows(direct.RootElement, scope, selected: true);
        Assert.Equal(scope == "details", direct.RootElement.TryGetProperty("physicalMemoryResidual", out _));
        Assert.Equal(scope == "details", source.SnapshotRequests.Last().MetricIds.Contains(PhysicalMemoryResidualFrame.MetricId));
        await AssertResidualHttpStream(client, source, query, direct.RootElement);
        Assert.Equal(scope == "details", source.PushRequests.Last().Request.MetricIds.Contains(PhysicalMemoryResidualFrame.MetricId));
        await app.StopAsync();
    }

    [Theory]
    [InlineData("bars", "processDetails=resource-residual:memory.usage")]
    [InlineData("details", "processDetails=resource-residual:memory.usage")]
    [InlineData("bars", "residualDetails=")]
    [InlineData("details", "residualDetails=")]
    [InlineData("bars", "residualDetails=cpu.usage")]
    [InlineData("details", "residualDetails=cpu.usage")]
    public async Task ResidualDetailsHttpDoesNotSelectOldSoftwareIdOrUnrequestedResidual(string scope, string selector)
    {
        var source = ResidualHttpSource();
        await using var app = await StartAsync(source);
        using var client = Client(app);
        var query = $"?scope={scope}&ids=memory.usage&{selector}&processDetails=software:a";
        using var direct = JsonDocument.Parse(await client.GetStringAsync("/api/resource-monitor/snapshot" + query));
        AssertResidualHttpRows(direct.RootElement, scope, selected: false);
        Assert.False(direct.RootElement.TryGetProperty("physicalMemoryResidual", out _));
        await AssertResidualHttpStream(client, source, query, direct.RootElement);
        Assert.All(source.SnapshotRequests, request => Assert.DoesNotContain(PhysicalMemoryResidualFrame.MetricId, request.MetricIds));
        Assert.All(source.PushRequests, push => Assert.DoesNotContain(PhysicalMemoryResidualFrame.MetricId, push.Request.MetricIds));
        await app.StopAsync();
    }

    [Theory]
    [InlineData("bars", "unknown.metric")]
    [InlineData("details", "unknown.metric")]
    [InlineData("bars", "process_all_working_set_actual")]
    [InlineData("details", "process_all_working_set_actual")]
    [InlineData("details", "memory.residual.details")]
    [InlineData("details", "memory.usage,unknown.metric")]
    public async Task ResidualDetailsHttpRejectsInvalidTypedMetricBeforeAcquiringDemand(string scope, string residual)
    {
        var source = ResidualHttpSource();
        await using var app = await StartAsync(source);
        using var client = Client(app);
        var query = $"?scope={scope}&ids=memory.usage&residualDetails={residual}";
        using var direct = await client.GetAsync("/api/resource-monitor/snapshot" + query);
        Assert.Equal(HttpStatusCode.BadRequest, direct.StatusCode);
        Assert.Contains("Resource residual details metric", await direct.Content.ReadAsStringAsync());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/subscriptions/stream")
        {
            Content = JsonContent.Create(new { version = 1, subscriptions = new[] {
                new { id = "valid", path = "/api/resource-monitor/subscribe?scope=bars&ids=cpu.usage" },
                new { id = "invalid", path = "/api/resource-monitor/subscribe" + query } } })
        };
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("One or more subscription queries are invalid.", await response.Content.ReadAsStringAsync(timeout.Token));
        Assert.Empty(source.SnapshotRequests);
        Assert.Empty(source.PushRequests);
        await app.StopAsync();
    }

    [Theory]
    [InlineData("residualDetails=memory.usage", true)]
    [InlineData("processDetails=resource-residual:memory.usage", false)]
    public async Task ResidualDetailsHttpBreakdownUsesTheSameRowSelection(string selector, bool selected)
    {
        var source = ResidualHttpSource();
        await using var app = await StartAsync(source);
        using var client = Client(app);
        using var response = JsonDocument.Parse(await client.GetStringAsync(
            "/api/resource-breakdown/snapshot?ids=memory.usage&" + selector));
        var root = response.RootElement;
        var catalog = root.GetProperty("softwareCatalog").EnumerateArray().ToArray();
        var bar = Assert.Single(root.GetProperty("bars").EnumerateArray());
        var residual = Assert.Single(bar.GetProperty("software").EnumerateArray(),
            row => catalog[row[0].GetInt32()][0].GetString() == "resource-residual:memory.usage");
        Assert.Equal(selected, residual[5].GetArrayLength() > 0);
        using var invalid = await client.GetAsync("/api/resource-breakdown/snapshot?ids=memory.usage&residualDetails=unknown.metric");
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Single(source.SnapshotRequests);
        await app.StopAsync();
    }

    [Theory]
    [InlineData("bars")]
    [InlineData("details")]
    public async Task ResidualDetailsHttpCombinesRealSoftwareAndMultipleMetricResiduals(string scope)
    {
        var at = PhysicalMemoryPfnTests.At;
        var source = new ResourceMonitorSource(CreateSnapshot(at) with
        {
            Datasets = [CurrentDataset("memory.usage", at), CurrentDataset("cpu.usage", at)]
        });
        await using var app = await StartAsync(source);
        using var client = Client(app);
        var query = $"?scope={scope}&ids=memory.usage,cpu.usage&processDetails=software:a&residualDetails=memory.usage,cpu.usage";
        using var direct = JsonDocument.Parse(await client.GetStringAsync("/api/resource-monitor/snapshot" + query));
        if (scope == "details")
        {
            var metrics = direct.RootElement.GetProperty("metrics").EnumerateArray().ToArray();
            Assert.Equal(2, metrics.Length);
            foreach (var metric in metrics)
            {
                var owners = metric.GetProperty("software").EnumerateArray().ToArray();
                Assert.Equal(2, owners.Length);
                Assert.Contains(owners, row => row.GetProperty("softwareId").GetString() == "software:a");
                Assert.Contains(owners, row => row.GetProperty("softwareId").GetString() == "resource-residual:" + metric.GetProperty("metricId").GetString());
                Assert.All(owners, owner => Assert.NotEmpty(owner.GetProperty("processes").EnumerateArray()));
            }
        }
        else
        {
            var breakdown = direct.RootElement.GetProperty("breakdown");
            var catalog = breakdown.GetProperty("softwareCatalog").EnumerateArray().ToArray();
            Assert.Equal(2, breakdown.GetProperty("bars").GetArrayLength());
            foreach (var bar in breakdown.GetProperty("bars").EnumerateArray())
            foreach (var row in bar.GetProperty("software").EnumerateArray())
                Assert.Equal(catalog[row[0].GetInt32()][0].GetString() != "software:b", row[5].GetArrayLength() > 0);
        }
        await AssertResidualHttpStream(client, source, query, direct.RootElement);
        await app.StopAsync();
    }

    private static ResourceMonitorSource ResidualHttpSource()
    {
        var at = PhysicalMemoryPfnTests.At;
        var source = CreateSnapshot(at);
        var memory = source.Bars.Single(bar => bar.MetricId == "memory.usage") with
        {
            TotalValue = 1000, CapacityValue = 2000, TotalSystemPercent = 50,
            Software = [Software("software:a", 1001, "worker-a.exe"),
                new("resource-residual:memory.usage", "", "windows_system", "system-residual", 999, 49.95, 0,
                    [new(-1, "", null, 200, 10, 200d / 999 * 100, AttributionKind: PhysicalMemoryResidualKinds.NonPagedPool)])]
        };
        const string measure = "process_all_private_working_set_actual";
        return new(source with
        {
            Bars = [memory], PhysicalMemoryResidual = PhysicalMemoryPfnTests.Frame(),
            ProcessMemoryFacts = [new(measure, "memory.usage", 1000, 2000, 0, []) { Residuals = [memory.Software[1]] }],
            Datasets = [.. source.Datasets, CurrentDataset(measure, at)]
        });
    }

    private static void AssertResidualHttpRows(JsonElement value, string scope, bool selected)
    {
        if (scope == "details")
        {
            var metric = Assert.Single(value.GetProperty("metrics").EnumerateArray());
            var software = metric.GetProperty("software").EnumerateArray().ToArray();
            if (!selected)
            {
                var owner = Assert.Single(software);
                Assert.Equal("software:a", owner.GetProperty("softwareId").GetString());
                Assert.Equal(1001, owner.GetProperty("processes")[0][0].GetInt32());
                return;
            }
            var residual = Assert.Single(software);
            Assert.Equal("resource-residual:memory.usage", residual.GetProperty("softwareId").GetString());
            var parts = residual.GetProperty("processes").EnumerateArray().ToArray();
            Assert.Contains(parts, part => part[8].GetString() == PhysicalMemoryResidualKinds.NonPagedPool && part[3].GetDouble() == 200);
            Assert.Equal(metric.GetProperty("residualValue").GetDouble(), parts.Sum(part => part[3].GetDouble()));
            return;
        }
        var breakdown = value.GetProperty("breakdown");
        var catalog = breakdown.GetProperty("softwareCatalog").EnumerateArray().ToArray();
        var bar = Assert.Single(breakdown.GetProperty("bars").EnumerateArray());
        var row = Assert.Single(bar.GetProperty("software").EnumerateArray(),
            item => catalog[item[0].GetInt32()][0].GetString() == "resource-residual:memory.usage");
        Assert.Equal(selected, row[5].GetArrayLength() > 0);
        if (selected)
        {
            Assert.Contains(row[5].EnumerateArray(), part => part[8].GetString() == PhysicalMemoryResidualKinds.NonPagedPool && part[3].GetDouble() == 200);
            Assert.Equal(bar.GetProperty("residualValue").GetDouble(), row[5].EnumerateArray().Sum(part => part[3].GetDouble()));
        }
        else
        {
            var real = Assert.Single(bar.GetProperty("software").EnumerateArray(), item => catalog[item[0].GetInt32()][0].GetString() == "software:a");
            Assert.Equal(1001, real[5][0][0].GetInt32());
        }
    }

    private static async Task AssertResidualHttpStream(HttpClient client, ResourceMonitorSource source, string query, JsonElement expected)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/subscriptions/stream")
        { Content = JsonContent.Create(new { version = 1, subscriptions = new[] {
            new { id = "residual", path = "/api/resource-monitor/subscribe" + query } } }) };
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(timeout.Token));
        using var frame = JsonDocument.Parse((await reader.ReadLineAsync(timeout.Token))!);
        Assert.Equal(expected.GetRawText(), frame.RootElement.GetProperty("value").GetRawText());
        Assert.Single(source.PushRequests);
        await timeout.CancelAsync();
    }
}
