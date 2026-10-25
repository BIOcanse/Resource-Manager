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
using ResourceManager.App.Endpoints;

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
    public async Task OverlayProducerLeasesFollowOnlyExplicitRequestedFacts(string metricId)
    {
        var processSource = new ProcessSource();
        var frameSource = new FrameSource();
        var resourceSource = new ResourceSource();
        var metricSource = new MetricSource();
        var builder = WebApplication.CreateBuilder();
        TrustedAccessEndpointTestAuthentication.Add(builder.Services, "metrics.read");
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var settings = new SettingsStore();
        builder.Services.AddSingleton<IPerformanceOverlaySettingsStore>(settings);
        builder.Services.AddSingleton<ISchedulingProcessFactObservationSource>(processSource);
        builder.Services.AddSingleton<IFrameTimingObservationSource>(frameSource);
        builder.Services.AddSingleton<IResourceBreakdownObservationSource>(resourceSource);
        builder.Services.AddSingleton<IMetricSnapshotObservationSource>(metricSource);
        await using var app = builder.Build();
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
                    new { id = "overlay", path = "/api/performance-overlay/subscribe?intervalMs=100&softwareId=game&ids=" + Uri.EscapeDataString(metricId) }
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
        Assert.Equal(needsGpu || metricId == "cpu.usage" ? 1 : 0, metricSource.ActiveCount);
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
        Assert.Equal(metricId, Assert.Single(target.GetProperty("metrics").EnumerateArray()).GetProperty("metricId").GetString());

        processSource.Current = Snapshot(Process(10, 100));
        await ReadUntilTargetCountAsync(reader, 1, timeout.Token);

        processSource.Current = Snapshot();
        await ReadUntilTargetCountAsync(reader, 0, timeout.Token);
        Assert.Equal(0, frameSource.ActiveCount);
        Assert.Equal(0, resourceSource.ActiveCount);
        Assert.Equal(0, metricSource.ActiveCount);
        Assert.Equal(0, processSource.ActiveCount);
        settings.Enabled = false;
        do { Assert.Empty(await ReadTargetsAsync(reader, timeout.Token)); }
        while (resourceSource.IdentityActiveCount > 0);
        Assert.Equal(0, resourceSource.IdentityActiveCount);

        await timeout.CancelAsync();
        response.Dispose();
        await app.StopAsync();
        Assert.Equal(0, resourceSource.IdentityActiveCount);
    }

    // 推送间隔只有 100 ms，改动数据之前可能已经排好下一行；读到目标数符合为止（受测试超时限制）。
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

    private sealed class SettingsStore : IPerformanceOverlaySettingsStore
    {
        public bool Enabled = true;
        private static readonly PerformanceOverlaySettings GameSettings =
            PerformanceOverlaySettings.Default("game") with
            {
                Enabled = true
            };
        private static readonly PerformanceOverlaySettings OtherGameSettings =
            PerformanceOverlaySettings.Default("other-game") with
            {
                Enabled = true
            };

        public Task<PerformanceOverlaySettingsDocument> GetAsync(CancellationToken cancellationToken)
            => Task.FromResult(new PerformanceOverlaySettingsDocument(1, [GameSettings, OtherGameSettings]));
        public Task<PerformanceOverlaySettings> GetSoftwareAsync(string softwareId, CancellationToken cancellationToken)
            => Task.FromResult(softwareId == "game" ? GameSettings with { Enabled = Enabled } : OtherGameSettings);
        public Task<PerformanceOverlaySettings> SaveSoftwareAsync(PerformanceOverlaySettings settings, CancellationToken cancellationToken)
            => Task.FromResult(settings);
    }

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
        public IDisposable AcquireSubscription()
        {
            Interlocked.Increment(ref activeCount);
            return new Lease(() => Interlocked.Decrement(ref activeCount));
        }
        public FrameTimingSnapshot? Read(TimeSpan window) => null;
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
        public HardwareMetricSnapshot Current { get; } = CreateHardwareSnapshot();
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
}
