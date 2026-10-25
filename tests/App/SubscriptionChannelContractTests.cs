using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using ResourceManager.App.Domain.Monitoring;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Application.ResourceBreakdown;
using ResourceManager.App.Application.ResourceTable;
using ResourceManager.App.Application.RuntimeSpecialization;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Domain.ResourceTable;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Endpoints;

namespace ResourceManager.App.Tests;

public sealed class SubscriptionChannelContractTests
{
    private static readonly JsonSerializerOptions WebJson =
        new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData("/api/metrics/subscribe")]
    [InlineData("/api/metrics/gpu-specialized/subscribe")]
    [InlineData("/api/resource-monitor/subscribe")]
    [InlineData("/api/resource-facts/subscribe")]
    [InlineData("/api/performance-overlay/subscribe")]
    [InlineData("/api/control/actual/subscribe")]
    [InlineData("/api/adapters/resource-manager/scheduling/subscribe")]
    [InlineData("/api/local-system/status/subscribe")]
    [InlineData("/api/device-topology/state/subscribe")]
    [InlineData("/api/device-topology/fields/subscribe")]
    [InlineData("/api/device-topology/acpi/fields/subscribe")]
    [InlineData("/api/cpu/topology/subscribe")]
    [InlineData("/api/cpu/topology/fields/subscribe")]
    [InlineData("/api/cpu/residency/subscribe")]
    [InlineData("/api/optimization/smart/state/subscribe")]
    [InlineData("/api/optimization/smart/status/subscribe")]
    [InlineData("/api/operations/subscribe")]
    [InlineData("/api/management/inventory-revisions/subscribe")]
    public void ExactLogicalSelectorsAreAccepted(string path)
    {
        var accepted = ResourceManagerEndpointRouteBuilderExtensions
            .TryParseSubscriptionSelector(path, out var selector, out var query);

        Assert.True(accepted);
        Assert.Equal(path, selector);
        Assert.Empty(query);
    }

    [Fact]
    public void QueryValuesAreParsedOnlyAfterExactSelectorAcceptance()
    {
        const string path =
            "/api/metrics/subscribe?ids=cpu.usage&ids=memory.usage&intervalMs=5000";

        var accepted = ResourceManagerEndpointRouteBuilderExtensions
            .TryParseSubscriptionSelector(path, out var selector, out var query);

        Assert.True(accepted);
        Assert.Equal("/api/metrics/subscribe", selector);
        Assert.Equal(2, query["ids"].Count);
        Assert.Equal("cpu.usage", query["ids"][0]);
        Assert.Equal("memory.usage", query["ids"][1]);
        Assert.Equal("5000", Assert.Single(query["intervalMs"]));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" /api/metrics/subscribe")]
    [InlineData("/api/metrics/subscribe ")]
    [InlineData("http://127.0.0.1/api/metrics/subscribe")]
    [InlineData("//api/metrics/subscribe")]
    [InlineData("\\api\\metrics\\subscribe")]
    [InlineData("/api/metrics/../metrics/subscribe")]
    [InlineData("/api/metrics/%73ubscribe")]
    [InlineData("/api/metrics/subscribe/")]
    [InlineData("/api/metrics/snapshot")]
    [InlineData("/api/metrics/subscribe#fragment")]
    [InlineData("/api/metrics/subscribe?ids=cpu.usage\n")]
    [InlineData("/api/operations/cancel")]
    [InlineData("/api/optimization/smart/diagnostics/decision-snapshot/subscribe")]
    public void AliasedOrNonSubscriptionSelectorsAreRejected(string? path)
    {
        Assert.False(ResourceManagerEndpointRouteBuilderExtensions
            .TryParseSubscriptionSelector(path, out _, out _));
    }

    [Fact]
    public void OversizedSelectorIsRejected()
    {
        var path = "/api/metrics/subscribe?ids=" + new string('a', 8_192);

        Assert.False(ResourceManagerEndpointRouteBuilderExtensions
            .TryParseSubscriptionSelector(path, out _, out _));
    }

    [Theory]
    [InlineData("cpu")]
    [InlineData("metric.cpu:1")]
    [InlineData("resource_table-2")]
    [InlineData("A_B.C-D:9")]
    public void RestrictedSubscriptionIdsAreAccepted(string id)
    {
        Assert.True(ResourceManagerEndpointRouteBuilderExtensions
            .IsValidSubscriptionId(id));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("cpu usage")]
    [InlineData("cpu/usage")]
    [InlineData("cpu?usage")]
    [InlineData("cpu占用")]
    public void UnrestrictedSubscriptionIdsAreRejected(string? id)
    {
        Assert.False(ResourceManagerEndpointRouteBuilderExtensions
            .IsValidSubscriptionId(id));
    }

    [Fact]
    public void OversizedSubscriptionIdIsRejected()
    {
        Assert.False(ResourceManagerEndpointRouteBuilderExtensions
            .IsValidSubscriptionId(new string('a', 129)));
    }

    [Fact]
    public void CallbackFrameContainsOnlySubscriptionIdAndCurrentValue()
    {
        var json = JsonSerializer.Serialize(
            new FrontendSubscriptionChannelFrame<object>(
                "metric.cpu:1",
                new { displayValue = "-" }),
            WebJson);
        using var document = JsonDocument.Parse(json);

        Assert.Equal(
            ["subscriptionId", "value"],
            document.RootElement
                .EnumerateObject()
                .Select(static property => property.Name)
                .ToArray());
        Assert.Equal(
            "metric.cpu:1",
            document.RootElement.GetProperty("subscriptionId").GetString());
        Assert.Equal(
            "-",
            document.RootElement
                .GetProperty("value")
                .GetProperty("displayValue")
                .GetString());
    }

    [Fact]
    public async Task FailedLogicalCallbackClosesStreamSoExistingClientCanReconnect()
    {
        using var cancellation = new CancellationTokenSource();
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        using var writer = new ResourceManagerEndpointRouteBuilderExtensions.FrontendSubscriptionChannelWriter(
            context.Response, WebJson);
        var subscription = new ResourceManagerEndpointRouteBuilderExtensions.CompiledFrontendSubscription(
            "cpu", "/api/metrics/subscribe", (_, _) => throw new IOException("Transient source failure."));

        await ResourceManagerEndpointRouteBuilderExtensions.RunSubscriptionChannelWorkerAsync(
            subscription, writer, cancellation, NullLogger.Instance, "fixture").WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(cancellation.IsCancellationRequested);
    }
}

public sealed class ResourceMonitorDetailsSubscriptionTests
{
    [Theory]
    [InlineData("")]
    [InlineData("?items=&intervalMs=1000")]
    [InlineData("?items=,%20,&intervalMs=1000")]
    public async Task EmptyFactsSelectionPublishesCurrentTypedEmptyWithoutSamplingDemand(string query)
    {
        var source = new ResourceMonitorSource(CreateSnapshot(DateTimeOffset.UnixEpoch));
        await using var app = await StartAsync(source);
        using var client = Client(app);
        using var direct = JsonDocument.Parse(await client.GetStringAsync(
            "/api/resource-facts/snapshot" + query));
        Assert.Equal(2, direct.RootElement.GetProperty("version").GetInt32());
        Assert.Equal(JsonValueKind.Null, direct.RootElement.GetProperty("capturedAt").ValueKind);
        foreach (var property in new[] { "software", "processes", "metrics" })
        {
            Assert.Empty(direct.RootElement.GetProperty(property).EnumerateArray());
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/subscriptions/stream")
        {
            Content = JsonContent.Create(new { version = 1, subscriptions = new[] {
                new { id = "empty-facts", path = "/api/resource-facts/subscribe" + query } } })
        };
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(timeout.Token));
        var line = await reader.ReadLineAsync(timeout.Token);
        Assert.NotNull(line);
        using var frame = JsonDocument.Parse(line!);
        Assert.Equal("empty-facts", frame.RootElement.GetProperty("subscriptionId").GetString());
        Assert.Equal(direct.RootElement.GetRawText(), frame.RootElement.GetProperty("value").GetRawText());
        Assert.Empty(source.SnapshotRequests);
        Assert.Empty(source.PushRequests);
        await timeout.CancelAsync();
        response.Dispose();
        await app.StopAsync();
    }

    [Fact]
    public async Task UnsupportedFactsItemsRejectEntireGetAndChannelBeforeAnySourceDemand()
    {
        var source = new ResourceMonitorSource(CreateSnapshot(DateTimeOffset.UnixEpoch));
        await using var app = await StartAsync(source);
        using var client = Client(app);
        var unsupported = ProcessMetricFieldRegistry.Definitions.Select(static field => field.Name)
            .Append("unknown.fact")
            .ToArray();
        Assert.Contains("process_all_working_set_actual", unsupported);
        foreach (var item in unsupported)
        {
            foreach (var items in new[] { item, "memory.usage," + item })
            {
                var query = "?items=" + Uri.EscapeDataString(items);
                using var direct = await client.GetAsync("/api/resource-facts/snapshot" + query);
                Assert.Equal(System.Net.HttpStatusCode.BadRequest, direct.StatusCode);
                using var directError = JsonDocument.Parse(await direct.Content.ReadAsStringAsync());
                Assert.Contains(item, directError.RootElement.GetProperty("error").GetString());
                Assert.Contains("unsupported", directError.RootElement.GetProperty("error").GetString());

                using var request = new HttpRequestMessage(HttpMethod.Post, "/api/subscriptions/stream")
                {
                    Content = JsonContent.Create(new { version = 1, subscriptions = new[] {
                        new { id = "supported-facts", path = "/api/resource-facts/subscribe?items=memory.usage" },
                        new { id = "rejected-facts", path = "/api/resource-facts/subscribe" + query } } })
                };
                using var response = await client.SendAsync(request);
                Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
                using var channelError = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                Assert.Equal("One or more subscription queries are invalid.",
                    channelError.RootElement.GetProperty("error").GetString());
            }
        }
        Assert.Empty(source.SnapshotRequests);
        Assert.Empty(source.PushRequests);
        await app.StopAsync();
    }

    [Fact]
    public async Task ResourceFactsReadCurrentOnlyAndSubscribeThroughExistingDemandOwner()
    {
        var time = DateTimeOffset.Parse("2026-10-24T12:00:00Z");
        var source = new ResourceMonitorSource(CreateSnapshot(time));
        await using var app = await StartAsync(source);
        using var client = Client(app);
        using var direct = JsonDocument.Parse(await client.GetStringAsync(
            "/api/resource-facts/snapshot?items=memory.usage&processDetailLevel=full"));
        Assert.Equal(2, direct.RootElement.GetProperty("version").GetInt32());
        Assert.Equal(2, direct.RootElement.GetProperty("processes").GetArrayLength());
        Assert.False(direct.RootElement.TryGetProperty("rows", out _));
        var currentRequest = Assert.Single(source.SnapshotRequests);
        Assert.Equal(["memory.usage"], currentRequest.MetricIds);
        Assert.Empty(currentRequest.ScaleModes);
        Assert.Equal(ProcessSampleDetailLevel.ResourceTableFull, currentRequest.ProcessDetailLevel);
        using var old = await client.GetAsync("/api/resource-table/snapshot");
        Assert.Equal(System.Net.HttpStatusCode.NotFound, old.StatusCode);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/subscriptions/stream")
        {
            Content = JsonContent.Create(new { version = 1, subscriptions = new[] {
                new { id = "facts", path = "/api/resource-facts/subscribe?items=memory.usage&intervalMs=1700&processDetailLevel=full" } } })
        };
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(timeout.Token));
        using var frame = JsonDocument.Parse((await reader.ReadLineAsync(timeout.Token))!);
        Assert.Equal(direct.RootElement.GetRawText(), frame.RootElement.GetProperty("value").GetRawText());
        var subscription = Assert.Single(source.PushRequests);
        Assert.Equal("resource-facts:facts", subscription.ChannelKey);
        Assert.Equal(TimeSpan.FromMilliseconds(1700), subscription.Interval);
        Assert.Equal(currentRequest.MetricIds, subscription.Request.MetricIds);
        Assert.Equal(currentRequest.ProcessDetailLevel, subscription.Request.ProcessDetailLevel);
        Assert.Single(source.SnapshotRequests);
        await timeout.CancelAsync();
        response.Dispose();
        await app.StopAsync();
    }

    [Fact]
    public async Task DetailsScopeUsesTheExistingPushSourceAndReturnsOnlyRequestedCurrentProcesses()
    {
        var capturedAt = DateTimeOffset.Parse("2026-10-24T12:00:00Z");
        var source = new ResourceMonitorSource(CreateSnapshot(capturedAt));
        await using var app = await StartAsync(source);
        using var client = Client(app);

        using var direct = JsonDocument.Parse(await client.GetStringAsync(
            "/api/resource-monitor/snapshot?scope=details&ids=memory.usage,cpu.usage&modes=memory.usage:capacity&processDetails=software:b"));
        AssertDetailsPayload(direct.RootElement, capturedAt);
        var directRequest = Assert.Single(source.SnapshotRequests);
        Assert.Equal(
            ["memory.usage", "cpu.usage"],
            directRequest.MetricIds);
        Assert.Equal(
            ProcessSampleDetailLevel.ResourceTableBasic,
            directRequest.ProcessDetailLevel);
        Assert.Equal(
            "capacity",
            directRequest.ScaleModes["memory.usage"]);

        using var bars = JsonDocument.Parse(await client.GetStringAsync(
            "/api/resource-monitor/snapshot?scope=bars&ids=memory.usage"));
        Assert.False(bars.RootElement.TryGetProperty("table", out _));
        Assert.Equal(2, source.SnapshotRequests.Count);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/subscriptions/stream")
        {
            Content = JsonContent.Create(new
            {
                version = 1,
                subscriptions = new[]
                {
                    new
                    {
                        id = "resource-details",
                        path = "/api/resource-monitor/subscribe?scope=details&intervalMs=1000&ids=memory.usage,cpu.usage&modes=memory.usage:capacity&processDetails=software:b"
                    }
                }
            })
        };
        using var response = await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            timeout.Token);
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(timeout.Token));
        var line = await reader.ReadLineAsync(timeout.Token);
        Assert.NotNull(line);
        using var frame = JsonDocument.Parse(line!);
        var value = frame.RootElement.GetProperty("value");
        AssertDetailsPayload(value, capturedAt);

        var pushed = Assert.Single(source.PushRequests);
        Assert.Equal("frontend-channel:resource-details", pushed.ChannelKey);
        Assert.Equal(TimeSpan.FromSeconds(1), pushed.Interval);
        Assert.Equal(
            ["memory.usage", "cpu.usage"],
            pushed.Request.MetricIds);
        Assert.Equal(
            ProcessSampleDetailLevel.ResourceTableBasic,
            pushed.Request.ProcessDetailLevel);
        Assert.Equal(
            "capacity",
            pushed.Request.ScaleModes["memory.usage"]);

        await timeout.CancelAsync();
        response.Dispose();
        await app.StopAsync();
    }

    private static void AssertDetailsPayload(JsonElement root, DateTimeOffset capturedAt)
    {
        Assert.Equal(
            ["version", "capturedAt", "metrics"],
            root.EnumerateObject().Select(static property => property.Name).ToArray());
        Assert.Equal(1, root.GetProperty("version").GetInt32());
        Assert.Equal(capturedAt, root.GetProperty("capturedAt").GetDateTimeOffset());
        var metric = Assert.Single(root.GetProperty("metrics").EnumerateArray());
        Assert.Equal("memory.usage", metric.GetProperty("metricId").GetString());
        var software = Assert.Single(metric.GetProperty("software").EnumerateArray());
        Assert.Equal("software:b", software.GetProperty("softwareId").GetString());
        var process = Assert.Single(software.GetProperty("processes").EnumerateArray());
        Assert.Equal(11, process.GetArrayLength());
        Assert.Equal(2002, process[0].GetInt32());
        Assert.Equal("worker-b.exe", process[1].GetString());
        Assert.DoesNotContain("worker-a.exe", root.GetRawText(), StringComparison.Ordinal);
        Assert.False(root.TryGetProperty("bars", out _));
        Assert.False(root.TryGetProperty("table", out _));
        Assert.False(root.TryGetProperty("softwareCatalog", out _));
    }

    private static ResourceBreakdownSnapshot CreateSnapshot(DateTimeOffset capturedAt)
    {
        var softwareA = Software("software:a", 1001, "worker-a.exe");
        var softwareB = Software("software:b", 2002, "worker-b.exe");
        return new ResourceBreakdownSnapshot(
            capturedAt,
            [
                new ResourceBreakdownBar(
                    ResourceBreakdownMetricIds.MemoryUsage,
                    "B",
                    ResourceBreakdownScaleModes.Capacity,
                    3,
                    100,
                    3,
                    [softwareA, softwareB]),
                new ResourceBreakdownBar(
                    ResourceBreakdownMetricIds.CpuUsage,
                    "%",
                    ResourceBreakdownScaleModes.Capacity,
                    5,
                    100,
                    5,
                    [softwareA])
            ])
        {
            Datasets =
            [
                CurrentDataset(ResourceBreakdownMetricIds.MemoryUsage, capturedAt),
                new ResourceBreakdownDatasetSamplingState(
                    SamplingDatasetIds.ForProcessMetric(ResourceBreakdownMetricIds.CpuUsage),
                    ResourceBreakdownSamplingStatuses.Ready,
                    1,
                    1,
                    null,
                    capturedAt,
                    null,
                    null,
                    null,
                    null)
            ]
        };
    }

    private static ResourceSoftwareSegment Software(string id, int processId, string processName)
    {
        var process = new ResourceProcessSegment(
            processId,
            processName,
            $@"C:\Apps\{processName}",
            1,
            1,
            100)
        {
            ProcessStartKey = checked(processId * 1000L)
        };
        return new ResourceSoftwareSegment(id, processName, "other", "general", 1, 1, 1, [process]);
    }

    private static ResourceBreakdownDatasetSamplingState CurrentDataset(
        string metricId,
        DateTimeOffset capturedAt)
        => new(
            SamplingDatasetIds.ForProcessMetric(metricId),
            ResourceBreakdownSamplingStatuses.Ready,
            1,
            1,
            capturedAt,
            capturedAt,
            capturedAt,
            null,
            null,
            null);

    private static async Task<WebApplication> StartAsync(ResourceMonitorSource source)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<IResourceBreakdownSampler>(source);
        builder.Services.AddSingleton<IResourceBreakdownPushSource>(source);
        builder.Services.AddSingleton<IRuntimePlanProvider>(new DefaultRuntimePlanProvider());
        var app = builder.Build();
        foreach (var method in new[]
        {
            "MapResourceMonitorEndpoints",
            "MapSubscriptionChannelEndpoints"
        })
        {
            typeof(ResourceManagerEndpointRouteBuilderExtensions)
                .GetMethod(method, BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, [app]);
        }

        await app.StartAsync();
        return app;
    }

    private static HttpClient Client(WebApplication app)
        => new()
        {
            BaseAddress = new Uri(Assert.Single(app.Services
                .GetRequiredService<IServer>()
                .Features
                .Get<IServerAddressesFeature>()!
                .Addresses)),
            Timeout = TimeSpan.FromSeconds(15)
        };

    private sealed class DefaultRuntimePlanProvider : IRuntimePlanProvider
    {
        public CompiledRuntimePlan Current => CompiledRuntimePlan.Default;

        public RuntimePlanPublicationLease AcquirePublicationLease()
            => RuntimePlanPublicationLease.CreateUntracked(Current, 0);

        public RuntimePlanPublicationResult Publish(CompiledRuntimePlan plan)
            => new(plan, 0, []);
    }

    private sealed class ResourceMonitorSource(ResourceBreakdownSnapshot snapshot)
        : IResourceBreakdownSampler, IResourceBreakdownPushSource
    {
        public ConcurrentQueue<ResourceBreakdownSampleRequest> SnapshotRequests { get; } = new();

        public ConcurrentQueue<PushRequest> PushRequests { get; } = new();

        public Task<ResourceBreakdownSnapshot> GetSnapshotAsync(
            ResourceBreakdownSampleRequest request,
            CancellationToken cancellationToken)
        {
            SnapshotRequests.Enqueue(request);
            return Task.FromResult(snapshot);
        }

        public Task<ResourceBreakdownSnapshot> GetSnapshotAsync(
            IReadOnlyList<string> metricIds,
            IReadOnlyDictionary<string, string> scaleModes,
            CancellationToken cancellationToken)
            => GetSnapshotAsync(
                new ResourceBreakdownSampleRequest(
                    metricIds,
                    scaleModes,
                    ProcessSampleDetailLevel.ResourceTableBasic),
                cancellationToken);

        public Task<ResourceBreakdownSnapshot> CaptureSnapshotAsync(
            ResourceBreakdownSampleRequest request,
            CancellationToken cancellationToken)
            => throw new InvalidOperationException("Reading current facts must never trigger sampling.");

        public async IAsyncEnumerable<ResourceBreakdownSnapshot> SubscribeAsync(
            string subscriptionId,
            ResourceBreakdownSampleRequest request,
            TimeSpan interval,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            PushRequests.Enqueue(new PushRequest(subscriptionId, request, interval));
            // Match the production source: an empty selection ends without publishing.
            if (request.MetricIds.Count == 0)
            {
                yield break;
            }
            await Task.Yield();
            yield return snapshot;
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }

    private sealed record PushRequest(
        string ChannelKey,
        ResourceBreakdownSampleRequest Request,
        TimeSpan Interval);


}
