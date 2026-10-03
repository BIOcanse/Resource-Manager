using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using ResourceManager.App.Application.FrameTiming;
using ResourceManager.App.Application.Metrics;
using ResourceManager.App.Application.Overlay;
using ResourceManager.App.Application.ResourceBreakdown;
using ResourceManager.App.Domain.FrameTiming;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Overlay;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Endpoints;

namespace Resource_Manager_APP.Tests;

public sealed class PerformanceOverlaySubscriptionTests
{
    [Fact]
    public async Task FrameLeaseFollowsEnabledRunningTarget()
    {
        var processSource = new ProcessSource();
        var frameSource = new FrameSource();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton<IPerformanceOverlaySettingsStore>(new SettingsStore());
        builder.Services.AddSingleton<ISchedulingProcessFactObservationSource>(processSource);
        builder.Services.AddSingleton<IFrameTimingObservationSource>(frameSource);
        builder.Services.AddSingleton<IResourceBreakdownObservationSource, ResourceSource>();
        builder.Services.AddSingleton<IMetricSnapshotObservationSource, MetricSource>();
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
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/subscriptions/stream")
        {
            Content = JsonContent.Create(new
            {
                version = 1,
                subscriptions = new[]
                {
                    new { id = "overlay", path = "/api/performance-overlay/subscribe?intervalMs=100" }
                }
            })
        };
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(timeout.Token));

        Assert.Empty(await ReadTargetsAsync(reader, timeout.Token));
        Assert.Equal(0, frameSource.ActiveCount);

        processSource.Current = Snapshot(Process(10, 100));
        Assert.Single(await ReadTargetsAsync(reader, timeout.Token));
        Assert.Equal(1, frameSource.ActiveCount);

        processSource.Current = Snapshot();
        Assert.Empty(await ReadTargetsAsync(reader, timeout.Token));
        Assert.Equal(0, frameSource.ActiveCount);

        await timeout.CancelAsync();
        response.Dispose();
        await app.StopAsync();
    }

    private static async Task<JsonElement.ArrayEnumerator> ReadTargetsAsync(
        StreamReader reader, CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse((await reader.ReadLineAsync(cancellationToken))!);
        return document.RootElement.GetProperty("value").GetProperty("targets").Clone().EnumerateArray();
    }

    private static SchedulingProcessFact Process(int pid, ulong start) =>
        new(pid, start, "Game", null, "game", "Game", "Game", "Game", 0,
            SchedulingProcessMetricMask.None, 0, 0, 1, []);

    private static SchedulingProcessFactSnapshot Snapshot(params SchedulingProcessFact[] processes) =>
        new(SamplingObservationStatus.Current, 1, DateTimeOffset.UtcNow.UtcTicks,
            (uint)processes.Length, (uint)processes.Length, 0, 0,
            SchedulingProcessMetricMask.CpuUsage, SchedulingProcessMetricMask.CpuUsage, processes)
        {
            HasSeparateFoundationPayloads = true,
            AttributionProcesses = processes
        };

    private sealed class SettingsStore : IPerformanceOverlaySettingsStore
    {
        private static readonly PerformanceOverlaySettings Settings =
            PerformanceOverlaySettings.Default("game") with
            {
                Enabled = true,
                Metrics = [PerformanceOverlayMetricIds.Fps]
            };

        public Task<PerformanceOverlaySettingsDocument> GetAsync(CancellationToken cancellationToken)
            => Task.FromResult(new PerformanceOverlaySettingsDocument(1, [Settings]));
        public Task<PerformanceOverlaySettings> GetSoftwareAsync(string softwareId, CancellationToken cancellationToken)
            => Task.FromResult(Settings);
        public Task<PerformanceOverlaySettings> SaveSoftwareAsync(PerformanceOverlaySettings settings, CancellationToken cancellationToken)
            => Task.FromResult(settings);
    }

    private sealed class ProcessSource : ISchedulingProcessFactObservationSource
    {
        private SchedulingProcessFactSnapshot? current;
        public SchedulingProcessFactSnapshot? Current
        {
            get => Volatile.Read(ref current);
            set => Volatile.Write(ref current, value);
        }

        public IDisposable AcquireSubscription(string subscriptionId, SchedulingProcessMetricMask metricMask, TimeSpan refreshInterval)
            => new Lease(static () => { });
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
    }

    private sealed class ResourceSource : IResourceBreakdownObservationSource
    {
        public IDisposable AcquireSubscription(string subscriptionId, ResourceBreakdownSampleRequest request, TimeSpan refreshInterval)
            => new Lease(static () => { });
        public ResourceBreakdownSnapshot ReadLatest(ResourceBreakdownSampleRequest request)
            => new(DateTimeOffset.UtcNow, []);
    }

    private sealed class MetricSource : IMetricSnapshotObservationSource
    {
        public IDisposable AcquireSubscription(string subscriptionId, MetricSampleRequest request, TimeSpan refreshInterval)
            => new Lease(static () => { });
        public HardwareMetricSnapshot? ReadLatest(MetricSampleRequest request) => null;
    }

    private sealed class Lease(Action release) : IDisposable
    {
        public void Dispose() => release();
    }
}
