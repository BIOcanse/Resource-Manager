using ResourceManager.ReadWrite.Windows.Frames;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using ResourceManager.App.Application.Metrics;
using ResourceManager.App.Application.Overlay;
using ResourceManager.App.Application.ResourceBreakdown;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Overlay;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Domain.Settings;
using ResourceManager.App.Endpoints;
using ResourceManager.App.Infrastructure.Monitoring.TargetedRecording;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;

namespace Resource_Manager_APP.Tests;

public sealed class PerformanceOverlaySubscriptionTests
{
    [Theory]
    [InlineData("target.fps")]
    [InlineData("target.cpu")]
    [InlineData("target.memory")]
    [InlineData("target.gpu")]
    [InlineData("target.vram")]
    [InlineData("cpu.usage")]
    [InlineData("field.current")]
    [InlineData("field.unavailable")]
    [InlineData("field.rebound")]
    [InlineData("field.multi")]
    [InlineData("field.pairs")]
    public async Task OverlayProducerLeasesFollowOnlyExplicitRequestedFacts(string metricId)
    {
        var processSource = new ProcessSource();
        var frameSource = new FrameSource();
        var resourceSource = new ResourceSource();
        var metricSource = new MetricSource();
        var useFields = metricId.StartsWith("field.", StringComparison.Ordinal);
        var field = MetricFieldRegistry.Definitions.Single(item => item.Code == MetricFieldCode.MemoryAvailable);
        var binding = new MetricDeviceFieldBinding("memory-device", field, "raw/memory", "windows.memory");
        HardwareMetricSnapshot FieldSnapshot(string address) => metricSource.Current with
        {
            FieldBindings = new Dictionary<string, MetricDeviceFieldBinding> { [address] = binding with { RequestAddress = address } },
            Items = new Dictionary<string, MetricReadingValue>
            { [address] = new(address, null, "B") { IntegerValue = new MetricInteger(9007199254740993) } },
            Datasets = new Dictionary<string, HardwareMetricDatasetObservation>
            { [address] = new(address, metricId == "field.unavailable" ? SamplingObservationStatus.Unavailable : SamplingObservationStatus.Current,
                1, DateTimeOffset.UtcNow.UtcTicks, 1, 1, 1, 1) }
        };
        if (useFields) metricSource.Current = FieldSnapshot(binding.RequestAddress);
        var multipleDevices = metricId is "field.multi" or "field.pairs";
        var secondField = MetricFieldRegistry.Definitions.Single(item => item.Code == MetricFieldCode.CommitPeak);
        if (multipleDevices)
        {
            var bindings = new[] { "device-a", "device-b", "device-c" }.SelectMany(device =>
                new[] { field, secondField }.Select(definition => new MetricDeviceFieldBinding(
                    device, definition, device + "/" + definition.Name, "fixture"))).ToArray();
            metricSource.Current = metricSource.Current with
            {
                FieldBindings = bindings.ToDictionary(item => item.RequestAddress),
                Items = bindings.ToDictionary(item => item.RequestAddress,
                    item => new MetricReadingValue(item.RequestAddress, null, "B") { IntegerValue = new MetricInteger(9007199254740993) }),
                Datasets = bindings.ToDictionary(item => item.RequestAddress, item => new HardwareMetricDatasetObservation(
                    item.RequestAddress, SamplingObservationStatus.Current, 1, DateTimeOffset.UtcNow.UtcTicks, 1, 1, 1, 1))
            };
        }
        var catalog = new HardwareMetricCatalogState();
        catalog.PublishCatalogProbe(metricSource.Current);
        var builder = WebApplication.CreateBuilder();
        TrustedAccessEndpointTestAuthentication.Add(builder.Services, "metrics.read", "processes.read");
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.ConfigureHttpJsonOptions(options =>
            ResourceManagerEndpointRouteBuilderExtensions.ConfigureJsonSerializerOptions(options.SerializerOptions));
        builder.Services.AddSingleton<ISchedulingProcessFactObservationSource>(processSource);
        builder.Services.AddSingleton<IFrameTimingObservationSource>(frameSource);
        builder.Services.AddSingleton<IResourceBreakdownObservationSource>(resourceSource);
        builder.Services.AddSingleton<IMetricSnapshotObservationSource>(metricSource);
        builder.Services.AddSingleton(catalog);
        builder.Services.AddSingleton<ResourceManager.App.Application.Software.ISoftwareRegistryView>(new EmptySoftwareRegistry());
        await using var app = TrustedAccessEndpointTestAuthentication.Build(builder);
        typeof(ResourceManagerEndpointRouteBuilderExtensions)
            .GetMethod("MapSubscriptionChannelEndpoints", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [app]);
        await app.StartAsync();
        using var client = new HttpClient
        {
            BaseAddress = new Uri(Assert.Single(app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses))
        };
        TrustedAccessEndpointTestAuthentication.Apply(client, app.Services);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/subscriptions/stream")
        {
            Content = JsonContent.Create(new
            {
                version = 1,
                subscriptions = new[]
                {
                    new { id = "overlay", path = "/api/performance-overlay/subscribe?intervalMs=100&softwareId=game&" + (useFields
                        ? multipleDevices
                            ? "device=device-a&fields=" + field.Name + "&device=device-b&fields="
                                + (metricId == "field.pairs" ? secondField.Name : field.Name)
                            : "fields=" + field.Name + "&device=" + binding.DeviceIdentity
                        : "ids=" + Uri.EscapeDataString(metricId)) }
                }
            })
        };
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(timeout.Token));

        Assert.Empty(await ReadTargetsAsync(reader, timeout.Token));
        Assert.Equal(0, processSource.ActiveCount);
        Assert.Equal(1, resourceSource.IdentityActiveCount);
        Assert.Equal(0, frameSource.ActiveCount);
        Assert.Equal(0, resourceSource.ActiveCount);
        Assert.Equal(0, metricSource.ActiveCount);

        processSource.Current = Snapshot(Process(10, 100));
        await ReadUntilTargetCountAsync(reader, 1, timeout.Token);
        Assert.Equal(metricId == "target.fps" ? 1 : 0, frameSource.ActiveCount);
        Assert.Equal(metricId == "target.cpu" ? 1 : 0, processSource.ActiveCount);
        var needsGpu = metricId is "target.gpu" or "target.vram";
        var needsResource = needsGpu || metricId == "target.memory";
        Assert.Equal(needsResource ? 1 : 0, resourceSource.ActiveCount);
        Assert.Equal(needsGpu || metricId == "cpu.usage" || useFields ? 1 : 0, metricSource.ActiveCount);
        if (multipleDevices)
        {
            var selectedSecond = metricId == "field.pairs" ? secondField.Name : field.Name;
            string[] expected = ["device-a/" + field.Name, "device-b/" + selectedSecond];
            Assert.Equal(expected.Order(), metricSource.LastRequest!.Ids.Order());
            using var rawFrame = JsonDocument.Parse((await reader.ReadLineAsync(timeout.Token))!);
            var rows = rawFrame.RootElement.GetProperty("value").GetProperty("fields").EnumerateArray()
                .ToDictionary(row => row.GetProperty("deviceIdentity").GetString()!);
            Assert.Equal(2, rows.Count);
            Assert.Equal(field.Name, Assert.Single(rows["device-a"].GetProperty("fields").EnumerateArray()).GetProperty("name").GetString());
            Assert.Equal(selectedSecond, Assert.Single(rows["device-b"].GetProperty("fields").EnumerateArray()).GetProperty("name").GetString());
            var datasets = metricSource.Current.Datasets.ToDictionary(item => item.Key, item => item.Value);
            datasets[expected[1]] = datasets[expected[1]] with { Status = SamplingObservationStatus.Unavailable };
            metricSource.Current = metricSource.Current with { Datasets = datasets };
            while (true)
            {
                using var next = JsonDocument.Parse((await reader.ReadLineAsync(timeout.Token))!);
                var failed = Assert.Single(next.RootElement.GetProperty("value").GetProperty("fields").EnumerateArray(),
                    row => row.GetProperty("deviceIdentity").GetString() == "device-b").GetProperty("fields")[0];
                if (failed.GetProperty("status").GetInt32() != (int)SamplingObservationStatus.Unavailable) continue;
                Assert.Equal(JsonValueKind.Null, failed.GetProperty("integerValue").ValueKind);
                break;
            }
            Assert.Equal(expected.Order(), metricSource.LastRequest.Ids.Order());
        }
        else if (useFields)
        {
            Assert.Equal([binding.RequestAddress], metricSource.LastRequest!.Ids);
            using var rawFrame = JsonDocument.Parse((await reader.ReadLineAsync(timeout.Token))!);
            var raw = Assert.Single(Assert.Single(rawFrame.RootElement.GetProperty("value").GetProperty("fields").EnumerateArray())
                .GetProperty("fields").EnumerateArray());
            Assert.Equal(field.Name, raw.GetProperty("name").GetString());
            Assert.Equal((int)(metricId == "field.unavailable" ? SamplingObservationStatus.Unavailable : SamplingObservationStatus.Current),
                raw.GetProperty("status").GetInt32());
            if (metricId == "field.unavailable") Assert.Equal(JsonValueKind.Null, raw.GetProperty("integerValue").ValueKind);
            else Assert.Equal("9007199254740993", raw.GetProperty("integerValue").GetString());
            if (metricId == "field.rebound")
            {
                metricSource.Current = FieldSnapshot("raw/rebound");
                catalog.PublishCatalogProbe(metricSource.Current);
                while (!metricSource.LastRequest!.Ids.Contains("raw/rebound"))
                    await ReadTargetsAsync(reader, timeout.Token);
                Assert.Equal(["raw/rebound"], metricSource.LastRequest.Ids);
                Assert.Equal(1, metricSource.ActiveCount);
            }
            Assert.Equal(0, resourceSource.ActiveCount);
            Assert.Equal(0, processSource.ActiveCount);
            Assert.Equal(0, frameSource.ActiveCount);
        }
        if (needsResource)
            Assert.Equal(new[] { metricId == "target.memory" ? "memory.usage" : metricId == "target.gpu" ? "gpu.0.usage" : "gpu.0.vram" }, resourceSource.LastRequest!.MetricIds);
        if (needsGpu)
        {
            Assert.True(metricSource.LastRequest!.IncludesCatalog);
            Assert.False(metricSource.LastRequest.IncludesAllGpuCoreMetrics);
            Assert.Empty(metricSource.LastRequest.Ids);
        }
        if (metricId == "cpu.usage") Assert.Equal(new[] { "cpu.usage" }, metricSource.LastRequest!.Ids);

        processSource.Current = Snapshot(Process(10, 100), Process(20, 200, "other-game"));
        var target = Assert.Single(await ReadTargetsAsync(reader, timeout.Token));
        Assert.Equal("game", target.GetProperty("softwareId").GetString());
        if (useFields) Assert.Empty(target.GetProperty("metrics").EnumerateArray());
        else Assert.Equal(metricId, Assert.Single(target.GetProperty("metrics").EnumerateArray()).GetProperty("metricId").GetString());

        processSource.Current = Snapshot(Process(10, 100));
        await ReadUntilTargetCountAsync(reader, 1, timeout.Token);

        processSource.Current = Snapshot();
        await ReadUntilTargetCountAsync(reader, 0, timeout.Token);
        Assert.Equal(0, frameSource.ActiveCount);
        Assert.Equal(0, resourceSource.ActiveCount);
        Assert.Equal(0, metricSource.ActiveCount);
        Assert.Equal(0, processSource.ActiveCount);
        Assert.Equal(1, resourceSource.IdentityActiveCount); // The explicit subscription owns identity demand.

        await timeout.CancelAsync();
        response.Dispose();
        await app.StopAsync();
        Assert.Equal(0, resourceSource.IdentityActiveCount);
    }

    // 推送间隔只有 100 ms，改动数据之前可能已经排好下一行；读到目标数符合为止（受测试超时限制）。
    private const string OverlayItems =
        """[{"kind":"metric","id":"cpu.usage","source":"auto","intervalMs":200,"syncPrecision":"high"},"""
        + """{"kind":"frames","id":"frames.present"},"""
        + """{"kind":"software","id":"cpu.usage","source":"resource-breakdown","intervalMs":300,"syncPrecision":"low"}]""";

    [Fact]
    public async Task OverlayItemsUseTheRecordingItemContractAndHoldEachItemOnlyWhileTheTargetRuns()
    {
        await using var rig = await OverlayItemRig.StartAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var response = await rig.SubscribeAsync(
            "intervalMs=100&softwareId=game&items=" + Uri.EscapeDataString(OverlayItems), timeout.Token);
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(timeout.Token));

        var items = await ReadItemsAsync(reader, timeout.Token);
        Assert.Equal(new[] { "metric", "frames", "software" }, items.Select(static item => item.GetProperty("kind").GetString()));
        Assert.All(items, item =>
        {
            Assert.Equal("unavailable", item.GetProperty("state").GetString());
            Assert.Equal("target-not-running", item.GetProperty("reason").GetString());
        });
        Assert.Equal(new[] { 200, 100, 300 }, items.Select(static item => item.GetProperty("intervalMs").GetInt32()));
        Assert.Equal(new[] { "item", "profile-default", "item" },
            items.Select(static item => item.GetProperty("intervalOrigin").GetString()));
        Assert.Equal(new[] { "high", "mid", "low" }, items.Select(static item => item.GetProperty("syncPrecision").GetString()));
        Assert.Equal(new[] { "auto", "auto", "resource-breakdown" },
            items.Select(static item => item.GetProperty("selectedSource").GetString()));
        Assert.Equal(0, rig.Metrics.ActiveCount);
        Assert.Equal(0, rig.Resources.ActiveCount);
        Assert.Equal(0, rig.Frames.ActiveCount);

        rig.Processes.Current = Snapshot(Process(10, 100));
        while ((await ReadItemsAsync(reader, timeout.Token))[0].GetProperty("reason").GetString() == "target-not-running")
        {
        }
        // One named holder per item; the subscription asked for no legacy ids, so nothing else is held.
        Assert.Equal(1, rig.Metrics.ActiveCount);
        Assert.Equal(1, rig.Resources.ActiveCount);
        Assert.Equal(1, rig.Frames.ActiveCount);
        Assert.Equal(new[] { "cpu.usage" }, rig.Resources.LastRequest!.MetricIds);

        rig.Processes.Current = Snapshot();
        while ((await ReadItemsAsync(reader, timeout.Token))[0].GetProperty("reason").GetString() != "target-not-running")
        {
        }
        Assert.Equal(0, rig.Metrics.ActiveCount);
        Assert.Equal(0, rig.Resources.ActiveCount);
        Assert.Equal(0, rig.Frames.ActiveCount);
        await timeout.CancelAsync();
    }

    [Fact]
    public async Task OverlayWithoutItemsKeepsTheSingleIntervalReplyAndAnEmptyItemList()
    {
        await using var rig = await OverlayItemRig.StartAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var response = await rig.SubscribeAsync("intervalMs=100&softwareId=game&ids=cpu.usage", timeout.Token);
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(timeout.Token));
        using var document = JsonDocument.Parse((await reader.ReadLineAsync(timeout.Token))!);
        var value = document.RootElement.GetProperty("value");
        Assert.Equal(3, value.GetProperty("version").GetInt32());
        Assert.Empty(value.GetProperty("items").EnumerateArray());
        Assert.Empty(value.GetProperty("targets").EnumerateArray());
        await timeout.CancelAsync();
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("""[]""")]
    [InlineData("""[{"kind":"metric","id":"cpu.usage","intervalMs":50}]""")]
    [InlineData("""[{"kind":"metric","id":"cpu.usage","syncPrecision":"ultra"}]""")]
    [InlineData("""[{"kind":"frames","id":"frames.present","intervalMs":500}]""")]
    [InlineData("""[{"kind":"metric","id":"cpu.usage"},{"kind":"metric","id":"cpu.usage"}]""")]
    public async Task OverlayRejectsItemsTheRecordingContractRejects(string items)
    {
        await using var rig = await OverlayItemRig.StartAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var response = await rig.SubscribeAsync(
            "intervalMs=100&softwareId=game&items=" + Uri.EscapeDataString(items), timeout.Token);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, rig.Resources.IdentityActiveCount);
    }

    [Fact]
    public async Task OverlayItemsInTheBodyReadLikeTheQueryValue()
    {
        await using var rig = await OverlayItemRig.StartAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var body = JsonDocument.Parse(OverlayItems);
        using var response = await rig.SubscribeAsync("intervalMs=100&softwareId=game", body.RootElement, timeout.Token);
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(timeout.Token));

        var items = await ReadItemsAsync(reader, timeout.Token);
        Assert.Equal(new[] { "metric", "frames", "software" }, items.Select(static item => item.GetProperty("kind").GetString()));
        Assert.Equal(new[] { 200, 100, 300 }, items.Select(static item => item.GetProperty("intervalMs").GetInt32()));
        Assert.Equal(new[] { "high", "mid", "low" }, items.Select(static item => item.GetProperty("syncPrecision").GetString()));
        await timeout.CancelAsync();
    }

    [Theory]
    [InlineData("""null""")]
    [InlineData("""{"kind":"metric","id":"cpu.usage"}""")]
    [InlineData("""[{"kind":"metric","id":"cpu.usage","syncPrecision":"ultra"}]""")]
    public async Task OverlayRejectsBodyItemsTheRecordingContractRejects(string items)
    {
        await using var rig = await OverlayItemRig.StartAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var body = JsonDocument.Parse(items);
        using var response = await rig.SubscribeAsync("intervalMs=100&softwareId=game", body.RootElement, timeout.Token);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, rig.Resources.IdentityActiveCount);
    }

    // A device that left takes its catalog entries with it; the overlay keeps its other items.
    [Theory]
    [InlineData("""[{"kind":"metric","id":"nope"},{"kind":"metric","id":"cpu.usage"}]""")]
    [InlineData("""[{"kind":"metric","id":"cpu.usage","source":"load@cpu9"},{"kind":"software","id":"cpu.usage"}]""")]
    public async Task OverlayAnswersItemsTheCatalogNoLongerHasAsUnknownAndKeepsTheRest(string items)
    {
        await using var rig = await OverlayItemRig.StartAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var response = await rig.SubscribeAsync(
            "intervalMs=100&softwareId=game&items=" + Uri.EscapeDataString(items), timeout.Token);
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(timeout.Token));
        rig.Processes.Current = Snapshot(Process(10, 100));
        var values = await ReadItemsAsync(reader, timeout.Token);
        Assert.Equal(2, values.Length);
        Assert.Equal("unavailable", values[0].GetProperty("state").GetString());
        Assert.Equal("unknown-metric", values[0].GetProperty("reason").GetString());
        Assert.NotEqual("unknown-metric", values[1].GetProperty("reason").GetString());
        await timeout.CancelAsync();
    }

    [Fact]
    public void GpuTotalsSumThePerGpuBarsAndAreListedForRecordingAndTheOverlay()
    {
        IReadOnlyList<TargetedRecordingCatalogItem> catalog =
        [
            new(TargetedRecordingItemKinds.Software, "gpu.0.usage", "%", [TargetedRecordingItemCatalogRules.ResourceBreakdownSource], null, TargetedRecordingItemUses.Both),
            new(TargetedRecordingItemKinds.Software, "gpu.1.usage", "%", [TargetedRecordingItemCatalogRules.ResourceBreakdownSource], null, TargetedRecordingItemUses.Both),
            new(TargetedRecordingItemKinds.Software, "gpu.0.vram", "B", [TargetedRecordingItemCatalogRules.ResourceBreakdownSource], null, TargetedRecordingItemUses.Both)
        ];
        Assert.Equal(new[] { "gpu.0.usage", "gpu.1.usage" },
            TargetedRecordingItemCatalogRules.TotalParts(TargetedRecordingItemCatalogRules.GpuUsageTotalId, catalog));
        Assert.Equal(new[] { "gpu.0.vram" },
            TargetedRecordingItemCatalogRules.TotalParts(TargetedRecordingItemCatalogRules.GpuVramTotalId, catalog));
        Assert.Null(TargetedRecordingItemCatalogRules.TotalParts("gpu.0.usage", catalog));
        var request = TargetedRecordingItemReader.SoftwareRequest(
            new TargetedRecordingItem(TargetedRecordingItemKinds.Software, TargetedRecordingItemCatalogRules.GpuUsageTotalId), catalog);
        Assert.Equal(new[] { "gpu.0.usage", "gpu.1.usage" }, request.MetricIds);

        static ResourceBreakdownBar Bar(string id, string unit, double value) => new(id, unit, "capacity", 100, 100, 100,
            [new ResourceSoftwareSegment("game", "Game", "game", "game", value, value, 1, [])]);
        var snapshot = new ResourceBreakdownSnapshot(DateTimeOffset.UtcNow,
            [Bar("gpu.0.usage", "%", 70), Bar("gpu.1.usage", "%", 50), Bar("gpu.0.vram", "B", 1e9), Bar("gpu.1.vram", "B", 2e9)]);
        var ids = new HashSet<string>(["game"], StringComparer.OrdinalIgnoreCase);
        Assert.Equal(100, TargetedRecordingItemReader.ReadSoftware(snapshot,
            new TargetedRecordingItem(TargetedRecordingItemKinds.Software, TargetedRecordingItemCatalogRules.GpuUsageTotalId),
            ids, static (_, _) => false).Value);
        Assert.Equal(3e9, TargetedRecordingItemReader.ReadSoftware(snapshot,
            new TargetedRecordingItem(TargetedRecordingItemKinds.Software, TargetedRecordingItemCatalogRules.GpuVramTotalId),
            ids, static (_, _) => false).Value);
    }

    [Fact]
    public async Task OverlayRejectsItemsInBothTheBodyAndTheQuery()
    {
        await using var rig = await OverlayItemRig.StartAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var body = JsonDocument.Parse(OverlayItems);
        using var response = await rig.SubscribeAsync(
            "intervalMs=100&softwareId=game&items=" + Uri.EscapeDataString(OverlayItems), body.RootElement, timeout.Token);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task BodyItemsOnAnotherSubscriptionRejectTheChannel()
    {
        await using var rig = await OverlayItemRig.StartAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var body = JsonDocument.Parse(OverlayItems);
        using var response = await rig.SubscribeAsync("intervalMs=100&softwareId=game", body.RootElement, timeout.Token,
            path: "/api/metrics/subscribe?");
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public void OverlayFrameItemsCarryTheFourFrameStatisticsAndTheOverlayUnavailableReasons()
    {
        var statistics = FrameIntervalStatistics.Compute([10, 10, 10, 20, 40])!;
        var frames = new FrameSource
        {
            Snapshot = new FrameTimingSnapshot(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(1), true,
                [new(10, 100, [new(FramePresentSource.Dxgi, 1, DateTimeOffset.UtcNow, statistics) { PresentCount = 6 }])])
        };
        var catalog = TargetedRecordingItemCatalogRules.Build([], []);
        Assert.Equal(new[] { "ms", "FPS", "FPS", "FPS" }, TargetedRecordingProfileDefaults.FramesItemIds
            .Select(id => TargetedRecordingItemCatalogRules.Find(catalog, TargetedRecordingItemKinds.Frames, id)!.Unit));

        var values = Update(frames);
        Assert.Equal(new double?[]
        {
            statistics.DurationMs / statistics.IntervalCount, statistics.AverageFps,
            statistics.OnePercentLowFps, statistics.PointOnePercentLowFps, null
        }, values.Select(static value => value.Value));
        Assert.Equal(new[] { "ms", "FPS", "FPS", "FPS" }, values.Take(4).Select(static value => value.Unit));
        // gpu.9.usage has no field binding in the publication: the reader names that, not a generic reason.
        Assert.Equal(TargetedRecordingReadReasons.MetricUnknown, values[4].Reason);

        frames.Snapshot = frames.Snapshot with { Complete = false };
        Assert.All(Update(frames).Take(4), static value => Assert.Equal("frame-events-lost", value.Reason));
        frames.Snapshot = frames.Snapshot with { Complete = true, Processes = [] };
        Assert.All(Update(frames).Take(4), static value => Assert.Equal("no-frame-data", value.Reason));

        IReadOnlyList<PerformanceOverlayItemValue> Update(FrameSource source)
        {
            var plans = TargetedRecordingProfileDefaults.FramesItemIds
                .Select(id => new TargetedRecordingItem(TargetedRecordingItemKinds.Frames, id))
                .Append(new TargetedRecordingItem(TargetedRecordingItemKinds.Metric, "gpu.9.usage"))
                .Select(item => new ResourceManagerEndpointRouteBuilderExtensions.PerformanceOverlayItemPlan(item,
                    TargetedRecordingItemCatalogRules.Find(catalog, item.Kind, item.Id), 1000, "profile-default"))
                .ToArray();
            var result = ResourceManagerEndpointRouteBuilderExtensions.UpdatePerformanceOverlayItems(plans, "overlay", ["game"],
                [Process(10, 100)], new MetricSource(), new ResourceSource(), source, TimeSpan.FromSeconds(1),
                DateTimeOffset.UtcNow, TimeSpan.FromMilliseconds(50), out _);
            ResourceManagerEndpointRouteBuilderExtensions.ReleasePerformanceOverlayItems(plans);
            return result;
        }
    }

    [Fact]
    public void OverlayOnlyFrameStatisticsAreRejectedForARecordingAndAcceptedForTheOverlay()
    {
        var catalog = TargetedRecordingItemCatalogRules.Build([], []);
        IReadOnlyList<TargetedRecordingItem> items = [new(TargetedRecordingItemKinds.Frames, TargetedRecordingProfileDefaults.FramesFpsItemId)];
        Assert.Equal("targeted-recording-item-unknown", TargetedRecordingItemReader.ValidateList(items, catalog));
        Assert.Null(TargetedRecordingItemReader.ValidateList(items, catalog,
            TargetedRecordingItemReader.MaximumOverlayItems, TargetedRecordingItemUses.Overlay));
        Assert.Equal(TargetedRecordingItemUses.OverlayOnly,
            TargetedRecordingItemCatalogRules.Find(catalog, TargetedRecordingItemKinds.Frames, TargetedRecordingProfileDefaults.FramesFpsItemId)!.Uses);
    }

    private static async Task<JsonElement[]> ReadItemsAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse((await reader.ReadLineAsync(cancellationToken))!);
        return document.RootElement.GetProperty("value").GetProperty("items").EnumerateArray()
            .Select(static item => item.Clone()).ToArray();
    }

    private sealed class OverlayItemRig(WebApplication app, HttpClient client) : IAsyncDisposable
    {
        public ProcessSource Processes { get; private init; } = null!;
        public FrameSource Frames { get; private init; } = null!;
        public ResourceSource Resources { get; private init; } = null!;
        public MetricSource Metrics { get; private init; } = null!;

        public static async Task<OverlayItemRig> StartAsync()
        {
            var processes = new ProcessSource();
            var frames = new FrameSource();
            var resources = new ResourceSource();
            var metrics = new MetricSource();
            var builder = WebApplication.CreateBuilder();
            TrustedAccessEndpointTestAuthentication.Add(builder.Services, "metrics.read", "processes.read");
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.ConfigureHttpJsonOptions(options =>
                ResourceManagerEndpointRouteBuilderExtensions.ConfigureJsonSerializerOptions(options.SerializerOptions));
            builder.Services.AddSingleton<ISchedulingProcessFactObservationSource>(processes);
            builder.Services.AddSingleton<IFrameTimingObservationSource>(frames);
            builder.Services.AddSingleton<IResourceBreakdownObservationSource>(resources);
            builder.Services.AddSingleton<IMetricSnapshotObservationSource>(metrics);
            builder.Services.AddSingleton(new HardwareMetricCatalogState());
            builder.Services.AddSingleton<ResourceManager.App.Application.Software.ISoftwareRegistryView>(new EmptySoftwareRegistry());
            builder.Services.AddSingleton<ITargetedRecordingItemCatalog>(new ItemCatalog());
            var app = TrustedAccessEndpointTestAuthentication.Build(builder);
            typeof(ResourceManagerEndpointRouteBuilderExtensions)
                .GetMethod("MapSubscriptionChannelEndpoints", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, [app]);
            await app.StartAsync();
            var client = new HttpClient
            {
                BaseAddress = new Uri(Assert.Single(app.Services.GetRequiredService<IServer>()
                    .Features.Get<IServerAddressesFeature>()!.Addresses))
            };
            TrustedAccessEndpointTestAuthentication.Apply(client, app.Services);
            return new OverlayItemRig(app, client)
            {
                Processes = processes, Frames = frames, Resources = resources, Metrics = metrics
            };
        }

        public Task<HttpResponseMessage> SubscribeAsync(string query, CancellationToken cancellationToken)
            => client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/subscriptions/stream")
            {
                Content = JsonContent.Create(new
                {
                    version = 1,
                    subscriptions = new[] { new { id = "overlay", path = "/api/performance-overlay/subscribe?" + query } }
                })
            }, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        public Task<HttpResponseMessage> SubscribeAsync(string query, JsonElement items, CancellationToken cancellationToken,
            string path = "/api/performance-overlay/subscribe?")
            => client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/subscriptions/stream")
            {
                Content = JsonContent.Create(new
                {
                    version = 1,
                    subscriptions = new[] { new { id = "overlay", path = path + query, items } }
                })
            }, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        public async ValueTask DisposeAsync()
        {
            client.Dispose();
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    private sealed class ItemCatalog : ITargetedRecordingItemCatalog
    {
        private static readonly IReadOnlyList<TargetedRecordingCatalogItem> Items =
        [
            new(TargetedRecordingItemKinds.Frames, TargetedRecordingProfileDefaults.FramesItemId, "ms", [], null, TargetedRecordingItemUses.Both),
            new(TargetedRecordingItemKinds.Software, "cpu.usage", "%", [TargetedRecordingItemCatalogRules.ResourceBreakdownSource], null, TargetedRecordingItemUses.Both),
            new(TargetedRecordingItemKinds.Metric, "cpu.usage", "%", ["load@cpu0"], null, TargetedRecordingItemUses.Both)
        ];
        public Task<IReadOnlyList<TargetedRecordingCatalogItem>> ReadAsync(CancellationToken cancellationToken)
            => Task.FromResult(Items);
        public IReadOnlyList<TargetedRecordingCatalogItem> ReadCurrent() => Items;
    }

    private static async Task ReadUntilTargetCountAsync(StreamReader reader, int expected, CancellationToken cancellationToken)
    {
        while ((await ReadTargetsAsync(reader, cancellationToken)).Count() != expected)
        {
        }
    }

    private static async Task<JsonElement.ArrayEnumerator> ReadTargetsAsync(
        StreamReader reader, CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse((await reader.ReadLineAsync(cancellationToken))!);
        return document.RootElement.GetProperty("value").GetProperty("targets").Clone().EnumerateArray();
    }

    private static SchedulingProcessFact Process(int pid, ulong start) =>
        Process(pid, start, "game");

    private static SchedulingProcessFact Process(int pid, ulong start, string softwareId) =>
        new(pid, start, "Game", null, softwareId, "Game", softwareId, softwareId, 0,
            SchedulingProcessMetricMask.None, 0, 0, 1, []);

    private static SchedulingProcessFactSnapshot Snapshot(params SchedulingProcessFact[] processes) =>
        new(SamplingObservationStatus.Current, 1, DateTimeOffset.UtcNow.UtcTicks,
            (uint)processes.Length, (uint)processes.Length, 0, 0,
            SchedulingProcessMetricMask.CpuUsage, SchedulingProcessMetricMask.CpuUsage, processes)
        {
            HasSeparateFoundationPayloads = true,
            InventoryProcesses = processes,
            AttributionProcesses = processes
        };

    private sealed class ProcessSource : ISchedulingProcessFactObservationSource
    {
        private SchedulingProcessFactSnapshot? current;
        private int activeCount;
        public int ActiveCount => Volatile.Read(ref activeCount);
        public SchedulingProcessFactSnapshot? Current
        {
            get => Volatile.Read(ref current);
            set => Volatile.Write(ref current, value);
        }

        public IDisposable AcquireSubscription(string subscriptionId, SchedulingProcessMetricMask metricMask, TimeSpan refreshInterval)
        {
            Interlocked.Increment(ref activeCount);
            return new Lease(() => Interlocked.Decrement(ref activeCount));
        }
        public SchedulingProcessFactSnapshot? ReadLatest(SchedulingProcessFactRequest request) => Current;
    }

    private sealed class FrameSource : IFrameTimingObservationSource
    {
        private int activeCount;
        public int ActiveCount => Volatile.Read(ref activeCount);
        public TimeSpan Retention => ResourceManager.App.Infrastructure.Monitoring.MonitoringSettings.ProductDefaults.FramePipeline.Retention;
        public IFrameSubscriptionLease AcquireSubscription(FrameSubscriptionRequest request) => new FrameLeaseAdapter(AcquireSubscription());

        public IDisposable AcquireSubscription()
        {
            Interlocked.Increment(ref activeCount);
            return new Lease(() => Interlocked.Decrement(ref activeCount));
        }
        public FrameTimingSnapshot? Snapshot { get; set; }
        public FrameTimingSnapshot? Read(TimeSpan window) => Snapshot;
        public FrameIntervalBatch? ReadIntervals(DateTimeOffset after, DateTimeOffset through) => null;
    }

    private sealed class ResourceSource : IResourceBreakdownObservationSource
    {
        private int activeCount;
        private int identityActiveCount;
        private int readCount;
        public int ActiveCount => Volatile.Read(ref activeCount);
        public int IdentityActiveCount => Volatile.Read(ref identityActiveCount);
        public int ReadCount => Volatile.Read(ref readCount);
        public ResourceBreakdownSampleRequest? LastRequest { get; private set; }
        public ResourceBreakdownSnapshot Current { get; } = new(DateTimeOffset.UtcNow, []);
        public IDisposable AcquireSubscription(string subscriptionId, ResourceBreakdownSampleRequest request, TimeSpan refreshInterval)
        {
            if (request.PublicationDatasetIds.Count > 0)
            {
                Assert.Empty(request.MetricIds);
                Assert.Equal(SchedulingProcessMetricMask.None, request.SchedulingMetricMask);
                Interlocked.Increment(ref identityActiveCount);
                return new Lease(() => Interlocked.Decrement(ref identityActiveCount));
            }
            LastRequest = request;
            Interlocked.Increment(ref activeCount);
            return new Lease(() => Interlocked.Decrement(ref activeCount));
        }
        public ResourceBreakdownSnapshot ReadLatest(ResourceBreakdownSampleRequest request)
        {
            Interlocked.Increment(ref readCount);
            return Current;
        }
    }

    private sealed class MetricSource : IMetricSnapshotObservationSource
    {
        private int activeCount;
        private int readCount;
        public int ActiveCount => Volatile.Read(ref activeCount);
        public int ReadCount => Volatile.Read(ref readCount);
        public MetricSampleRequest? LastRequest { get; private set; }
        public HardwareMetricSnapshot Current { get; set; } = CreateHardwareSnapshot();
        public IDisposable AcquireSubscription(string subscriptionId, MetricSampleRequest request, TimeSpan refreshInterval)
        {
            LastRequest = request;
            Interlocked.Increment(ref activeCount);
            return new Lease(() => Interlocked.Decrement(ref activeCount));
        }
        public HardwareMetricSnapshot? ReadLatest(MetricSampleRequest request)
        {
            Interlocked.Increment(ref readCount);
            return Current;
        }
    }

    private static HardwareMetricSnapshot CreateHardwareSnapshot()
    {
        var now = DateTimeOffset.UtcNow;
        var gpuInventory = new SchedulingGpuInventorySnapshot(
            SamplingObservationStatus.NotRequested, 0, 0, 0, 0, 0, 0, []);
        return new HardwareMetricSnapshot(
            now,
            new CpuMetrics(
                "CPU", 0, false, CpuMetricObservationStatus.Unavailable, 0, 0,
                0, 0, 0, "fixture",
                new CpuSensorMetrics(
                    new HardwareSensorProviderState("fixture", HardwareSensorProviderStates.Unavailable, null),
                    null, null, null, null)),
            new MemoryMetrics(UsedBytes: 0, TotalBytes: 0, UsagePercent: 0, IsUsageAvailable: false),
            new VirtualMemoryMetrics(UsedBytes: 0, TotalBytes: 0, UsagePercent: 0, IsSelectable: false),
            [new GpuMetrics(0, "GPU", 0, 0, 0, 0, 0, 0, 0, 0,
                new GpuSensorMetrics(new("fixture", HardwareSensorProviderStates.Unavailable, null),
                    null, null, null, null, null, null, null, null, null, null))],
            gpuInventory,
            new Dictionary<string, MetricReadingValue>());
    }

    private sealed class Lease(Action release) : IDisposable
    {
        public void Dispose() => release();
    }

    // No registered records: the overlay target is the requested software id itself.
    private sealed class EmptySoftwareRegistry : ResourceManager.App.Application.Software.ISoftwareRegistryView
    {
        public Task<IReadOnlyList<ResourceManager.App.Domain.Software.SoftwareRecord>> GetSoftwareAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<ResourceManager.App.Domain.Software.SoftwareRecord>>([]);

        public Task<IReadOnlyList<ResourceManager.App.Domain.Software.SoftwareRecord>> RefreshSoftwareAsync(CancellationToken cancellationToken)
            => GetSoftwareAsync(cancellationToken);
    }
}
