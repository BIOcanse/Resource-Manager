using System.Net.Http.Json;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ResourceManager.App.Application.Metrics;
using ResourceManager.App.Application.ResourceBreakdown;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Endpoints;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using ResourceManager.Shared.Runtime.TrustedAccess;

namespace Resource_Manager_APP.Tests;

[Collection(SoftwareIdentityCatalogProcessStateCollection.Name)]
public sealed partial class ConditionSubscriptionTests
{
    internal const string MemorySubscription = "/api/metrics/subscribe?ids=memory.usage&intervalMs=100";
    internal const string MemoryFactsSubscription = "/api/resource-facts/subscribe?items=memory.usage&intervalMs=100";
    internal static readonly DateTimeOffset Epoch = DateTimeOffset.Parse("2026-10-26T12:00:00Z");
    private static readonly Lazy<HardwareMetricSnapshot> Baseline = new(StorageMetricSnapshotTestData.CommittedSnapshot);
    internal static Dictionary<string, object> Rule(int top = 0) => new()
    { ["binding"] = "machine", ["metricId"] = "memory.usage", ["quantity"] = "remaining",
      ["comparison"] = "below", ["threshold"] = 100d, ["holdSeconds"] = 1d, ["topOwners"] = top };
    internal static object Hook(object rule, string id = "pressure", int intervalMs = 100) => new { id, rule, intervalMs };

    [Fact]
    public async Task HookDurationOwnersNormalFramesSilenceClearAndReentryThroughHttp()
    {
        var resources = new ResourceSource();
        await using var host = await Host.StartAsync(new MetricSource(), resources);
        await using var stream = await host.OpenAsync([Hook(Rule(2))]);
        await resources.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await DeliverAsync(host, stream, resources, Breakdown(Epoch, 950), 0);
        await DeliverAsync(host, stream, resources, Breakdown(Epoch.AddSeconds(1), 960), 1);
        var frame = await stream.ReadFrameAsync();
        Assert.Equal("pressure", frame.GetProperty("hookId").GetString());
        Assert.False(frame.TryGetProperty("subscriptionId", out _));
        var met = frame.GetProperty("value");
        Assert.Equal("met", met.GetProperty("state").GetString());
        Assert.Equal(40, met.GetProperty("reading").GetDouble());
        Assert.Equal(Epoch, met.GetProperty("since").GetDateTimeOffset());
        var owners = met.GetProperty("owners").EnumerateArray().ToArray();
        Assert.Equal(new[] { 22, 11 }, owners.Select(owner => owner.GetProperty("processId").GetInt32()));
        Assert.Equal(new[] { "large", "small" }, owners.Select(owner => owner.GetProperty("name").GetString()));
        Assert.Equal(new[] { 600d, 200d }, owners.Select(owner => owner.GetProperty("reading").GetDouble()));
        // No subscription frames or repeated met events may reach the caller.
        await DeliverAsync(host, stream, resources, Breakdown(Epoch.AddSeconds(2), 980), 1);
        await DeliverAsync(host, stream, resources, Breakdown(Epoch.AddSeconds(3), 980), 1);
        await DeliverAsync(host, stream, resources, Breakdown(Epoch.AddSeconds(4), 800), 1);
        Assert.Equal("cleared", (await stream.ReadAsync()).GetProperty("state").GetString());
        await DeliverAsync(host, stream, resources, Breakdown(Epoch.AddSeconds(5), 950), 1);
        await DeliverAsync(host, stream, resources, Breakdown(Epoch.AddSeconds(6), 950), 1);
        Assert.Equal("met", (await stream.ReadAsync()).GetProperty("state").GetString());
        Assert.Equal(1, resources.Active);
        Assert.Equal(1, resources.Acquired);
        Assert.Equal("monitoring-hook:pressure", resources.Owner);
        Assert.Equal(TimeSpan.FromMilliseconds(100), resources.Interval);
    }

    [Fact]
    public async Task UnavailableResetsPendingAndDoesNotRepeatThroughHttp()
    {
        var metrics = new MetricSource();
        await using var host = await Host.StartAsync(metrics);
        await using var stream = await host.OpenAsync([Hook(Rule())]);
        await metrics.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await DeliverAsync(host, stream, metrics, Memory(Epoch, 950), 0);
        await DeliverAsync(host, stream, metrics, Memory(Epoch, null), .9);
        var unavailable = await stream.ReadAsync();
        Assert.Equal("unavailable", unavailable.GetProperty("state").GetString());
        Assert.False(string.IsNullOrWhiteSpace(unavailable.GetProperty("reason").GetString()));
        await DeliverAsync(host, stream, metrics, Memory(Epoch, null), 2);
        await DeliverAsync(host, stream, metrics, Memory(Epoch, 950), 1);
        var since = host.Clock.GetUtcNow();
        await DeliverAsync(host, stream, metrics, Memory(Epoch, 950), .5);
        await DeliverAsync(host, stream, metrics, Memory(Epoch, 950), .5);
        var met = await stream.ReadAsync();
        Assert.Equal("met", met.GetProperty("state").GetString());
        Assert.Equal(since, met.GetProperty("since").GetDateTimeOffset());
    }

    [Theory]
    [InlineData("metricId", "unknown")]
    [InlineData("quantity", "unknown")]
    [InlineData("holdSeconds", "0")]
    [InlineData("threshold", "not-a-number")]
    [InlineData("comparison", "above")]
    [InlineData("binding", "unknown")]
    [InlineData("topOwners", "-1")]
    [InlineData("holdSeconds", "oops")]
    [InlineData("intervalMs", "100")]
    [InlineData("softwareId", "unused")]
    public async Task InvalidHookIsRejectedWhileItsSubscriptionAndSiblingHooksContinue(string key, string value)
    {
        var metrics = new MetricSource();
        var invalid = Rule();
        invalid[key] = double.TryParse(value, out var number) ? number : value;
        await using var host = await Host.StartAsync(metrics);
        await using var stream = await host.OpenAsync([Hook(invalid, "invalid"), Hook(Rule())]);
        var rejected = await stream.ReadFrameAsync();
        Assert.Equal("invalid", rejected.GetProperty("hookId").GetString());
        Assert.Equal(400, rejected.GetProperty("value").GetProperty("statusCode").GetInt32());
        await metrics.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await DeliverAsync(host, stream, metrics, Memory(Epoch, 950), 0);
        await DeliverAsync(host, stream, metrics, Memory(Epoch, 950), 1);
        var met = await stream.ReadFrameAsync();
        Assert.Equal("pressure", met.GetProperty("hookId").GetString());
        Assert.Equal("met", met.GetProperty("value").GetProperty("state").GetString());
        Assert.Equal(1, metrics.Acquired);
    }

    [Fact]
    public async Task ClosingHttpChannelReleasesTheSubscriptionAndAllHooks()
    {
        var metrics = new MetricSource();
        await using var host = await Host.StartAsync(metrics);
        var stream = await host.OpenAsync([Hook(Rule()), Hook(Rule(), "second")]);
        await metrics.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await UntilAsync(() => metrics.Active == 2);
        await stream.DisposeAsync();
        await UntilAsync(() => metrics.Active == 0);
        Assert.Equal(0, metrics.Active);
        Assert.Equal(2, metrics.Acquired);
    }

    internal static async Task DeliverAsync(Host host, Stream stream, MetricSource source, HardwareMetricSnapshot value, double elapsed)
    { host.Clock.Advance(elapsed); await source.PublishAsync(value); }
    internal static async Task DeliverAsync(Host host, Stream stream, ResourceSource source, ResourceBreakdownSnapshot value, double elapsed)
    { host.Clock.Advance(elapsed); await source.PublishAsync(value); }

    internal static HardwareMetricSnapshot Memory(DateTimeOffset at, double? used)
        => Baseline.Value with { CapturedAt = at,
            Memory = new((ulong)(used ?? 0), 1000, used / 10 ?? 0, true),
            Items = new Dictionary<string, MetricReadingValue>(StringComparer.OrdinalIgnoreCase)
            { ["memory.usage"] = new("memory.usage", used, "B", Total: 1000)
                { Status = used is null ? SamplingObservationStatus.Unavailable : SamplingObservationStatus.Current, ObservedAt = at } },
            Datasets = new Dictionary<string, HardwareMetricDatasetObservation>
            { [SamplingDatasetIds.ForSystemMetric("memory.usage")] = new(SamplingDatasetIds.ForSystemMetric("memory.usage"),
                used is null ? SamplingObservationStatus.Unavailable : SamplingObservationStatus.Current, 1, at.UtcTicks, 1, 1, 1, 1) } };

    internal static ResourceBreakdownSnapshot Breakdown(DateTimeOffset at, double? used = 950)
        => new(at, [new("memory.usage", "B", "capacity", used, 1000, used / 10,
            [new("software:a", "A", "app", "app", 800, 80, 2,
                [new(11, "small", null, 200, 20, 25) { ProcessStartKey = 101 },
                 new(22, "large", null, 600, 60, 75) { ProcessStartKey = 202 }])])])
        { Datasets = [new(SamplingDatasetIds.ForProcessMetric("memory.usage"), used is null ? "failed" : "ready", 1, 1,
            used is null ? null : at, at, at, null, used is null ? "recorded-unavailable" : null, null)] };

    internal class RecordedSource<T>
    {
        private sealed record Delivery(T Value, TaskCompletionSource Completed);
        private readonly object gate = new();
        private readonly List<Channel<Delivery>> channels = [];
        internal readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Active, Acquired;
        internal TimeSpan Interval;
        internal string? Owner;
        internal void Publish(T value) => _ = PublishAsync(value);
        internal Task PublishAsync(T value)
        {
            var pending = new List<Task>();
            lock (gate)
                foreach (var channel in channels)
                {
                    var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    channel.Writer.TryWrite(new(value, completed)); pending.Add(completed.Task);
                }
            return Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(5));
        }
        protected async IAsyncEnumerable<T> ReadAsync(string id,
            TimeSpan interval, [EnumeratorCancellation] CancellationToken token)
        {
            var channel = Channel.CreateUnbounded<Delivery>();
            lock (gate) channels.Add(channel);
            Owner = id; Interval = interval;
            Interlocked.Increment(ref Active); Interlocked.Increment(ref Acquired); Started.TrySetResult();
            try
            {
                await foreach (var delivery in channel.Reader.ReadAllAsync(token))
                    try { yield return delivery.Value; } finally { delivery.Completed.TrySetResult(); }
            }
            finally
            {
                lock (gate) channels.Remove(channel);
                while (channel.Reader.TryRead(out var remaining)) remaining.Completed.TrySetResult();
                Interlocked.Decrement(ref Active); Released.TrySetResult();
            }
        }
    }

    internal sealed class MetricSource : RecordedSource<HardwareMetricSnapshot>, IMetricSnapshotPushSource
    {
        internal string? RejectOwner;
        public IAsyncEnumerable<HardwareMetricSnapshot> SubscribeAsync(string id, MetricSampleRequest request, TimeSpan interval, CancellationToken token)
        {
            if (id == RejectOwner) throw new ArgumentOutOfRangeException(nameof(interval), "The sampling period must meet the current role minimum.");
            return ReadAsync(id, interval, token);
        }
    }

    internal sealed class ResourceSource : RecordedSource<ResourceBreakdownSnapshot>, IResourceBreakdownPushSource, ISchedulingProcessFactObservationSource
    {
        internal SchedulingProcessFactSnapshot? Foundations;
        internal int FoundationReads;
        public SchedulingProcessFactSnapshot? ReadLatest(SchedulingProcessFactRequest request) { Interlocked.Increment(ref FoundationReads); return Foundations; }
        public IDisposable AcquireSubscription(string id, SchedulingProcessMetricMask mask, TimeSpan interval) => throw new NotSupportedException();
        public IAsyncEnumerable<ResourceBreakdownSnapshot> SubscribeAsync(string id, ResourceBreakdownSampleRequest request, TimeSpan interval, CancellationToken token)
            => ReadAsync(id, interval, token);
    }

    internal sealed class HookClock : TimeProvider
    {
        private long ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref ticks);
        public override DateTimeOffset GetUtcNow() => Epoch.AddTicks(GetTimestamp());
        internal void Advance(double seconds) => Interlocked.Add(ref ticks, TimeSpan.FromSeconds(seconds).Ticks);
    }

    internal sealed class Host(WebApplication app, HookClock clock, TrustedAccessEndpointTestAuthentication.AddedCredential credential) : IAsyncDisposable
    {
        internal HookClock Clock => clock;
        internal readonly HttpClient Client = TrustedAccessEndpointTestAuthentication.Client(app);
        internal Task<bool> RevokeAsync() => credential.Owner.RevokeAsync(credential.CredentialId, CancellationToken.None);
        internal static async Task<Host> StartAsync(IMetricSnapshotPushSource metrics, IResourceBreakdownPushSource? resources = null,
            HardwareMetricCatalogState? catalog = null, bool realClock = false, string[]? scopes = null)
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0"); builder.Logging.ClearProviders();
            var credential = TrustedAccessEndpointTestAuthentication.Add(builder.Services, scopes ?? [AccessKeyScopes.MetricsRead, AccessKeyScopes.ProcessesRead]);
            catalog ??= new(); if (catalog.Current is null) catalog.PublishCatalogProbe(Memory(Epoch, 950));
            var clock = new HookClock();
            builder.Services.ConfigureHttpJsonOptions(options =>
                ResourceManagerEndpointRouteBuilderExtensions.ConfigureJsonSerializerOptions(options.SerializerOptions));
            builder.Services.AddSingleton<TimeProvider>(realClock ? TimeProvider.System : clock);
            builder.Services.AddSingleton(catalog); builder.Services.AddSingleton(metrics);
            if (resources is not null) builder.Services.AddSingleton(resources);
            if (resources is ISchedulingProcessFactObservationSource foundations) builder.Services.AddSingleton(foundations);
            var app = TrustedAccessEndpointTestAuthentication.Build(builder);
            typeof(ResourceManagerEndpointRouteBuilderExtensions).GetMethod("MapSubscriptionChannelEndpoints", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [app]);
            await app.StartAsync(); return new(app, clock, credential);
        }
        internal Task<Stream> OpenAsync(object[] hooks, object[]? subscriptions = null)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/api/subscriptions/stream")
            { Content = JsonContent.Create(new { version = 1, hooks, subscriptions }) };
            var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            // SendAsync waits for headers; normal subscriptions flush with their first frame.
            // Start the request now and allow tests to publish their first value after admission.
            var response = Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation.Token);
            return Task.FromResult(new Stream(request, response, cancellation));
        }
        public async ValueTask DisposeAsync() { Client.Dispose(); await app.StopAsync(); await app.DisposeAsync(); }
    }

    internal sealed class Stream(HttpRequestMessage request, Task<HttpResponseMessage> pendingResponse, CancellationTokenSource cancellation) : IAsyncDisposable
    {
        private HttpResponseMessage? response;
        private StreamReader? reader;
        internal async Task<JsonElement> ReadFrameAsync()
        {
            response ??= await pendingResponse; response.EnsureSuccessStatusCode();
            reader ??= new(await response.Content.ReadAsStreamAsync(cancellation.Token));
            var line = await reader.ReadLineAsync(cancellation.Token); Assert.NotNull(line);
            using var document = JsonDocument.Parse(line); return document.RootElement.Clone();
        }
        internal async Task<JsonElement> ReadAsync() => (await ReadFrameAsync()).GetProperty("value");
        public async ValueTask DisposeAsync()
        {
            await cancellation.CancelAsync(); reader?.Dispose();
            try { response ??= await pendingResponse; } catch (OperationCanceledException) { }
            response?.Dispose(); request.Dispose(); cancellation.Dispose();
        }
    }
}
