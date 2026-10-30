using System.Collections.Concurrent;
using ResourceManager.ReadWrite.Windows.Frames;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Application.Metrics;
using ResourceManager.App.Application.ResourceBreakdown;
using ResourceManager.App.Application.Software;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Domain.Settings;
using ResourceManager.App.Domain.Software;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Infrastructure.Monitoring.TargetedRecording;
using ResourceManager.App.Infrastructure.Persistence;

namespace Resource_Manager_APP.Tests;

public sealed class TargetedRecordingServiceTests : IDisposable
{
    private static readonly (int Pid, long Key) ProcessA = (Environment.ProcessId,
        System.Diagnostics.Process.GetCurrentProcess().StartTime.ToFileTimeUtc());
    private static readonly (int Pid, long Key) ProcessB = (424242, 777);
    private readonly string root = Path.Combine(Path.GetTempPath(), $"rm-targeted-service-{Guid.NewGuid():N}");

    private static readonly TargetedRecordingOptions FastOptions =
        MonitoringSettings.ProductDefaults.TargetedRecording with { SampleIntervalMilliseconds = 100 };

    internal static readonly IReadOnlyList<TargetedRecordingCatalogItem> TestCatalog =
    [
        new(TargetedRecordingItemKinds.Frames, TargetedRecordingProfileDefaults.FramesItemId, "ms", [], null),
        new(TargetedRecordingItemKinds.Software, "cpu.usage", "%", [TargetedRecordingItemCatalogRules.ResourceBreakdownSource], null),
        new(TargetedRecordingItemKinds.Metric, "cpu.usage", "%", ["load@cpu0"], null)
    ];

    [Fact]
    public async Task RecordedAllMetricsRecordingHoldsRealNativeSubscriptionUntilUserStops()
    {
        using var subscriptions = new HostManagerSamplingSubscriptionTestFixture();
        using var sampler = WindowsHardwareMetricSamplerLifecycleTests.CreateSampler(
            subscriptions, AllMetricSubscriptionTestData.RecordedCatalog());
        var rig = Create(metrics: sampler);
        await rig.Service.StartAsync(CancellationToken.None);
        try
        {
            var started = await rig.Service.BeginAsync("software", 600, CancellationToken.None);
            Assert.Equal("recording", started.Status);
            Assert.Equal("default", started.ProfileId);
            Assert.Equal("recording", (await rig.Store.GetAsync(started.Id, CancellationToken.None))!.Recording.Status);
            Assert.Equal(1, rig.Frames.ActiveLeases);
            // The process check plus the one software item this catalog offers.
            Assert.Equal(2, rig.Breakdown.ActiveLeases);
            var stopped = await rig.Service.FinishAsync(started.Id, CancellationToken.None);
            Assert.Equal(TargetedRecordingStopReasons.UserStop, stopped!.StopReason);
            Assert.False(stopped.Incomplete);
            Assert.Equal(0, rig.Frames.ActiveLeases);
            Assert.Equal(0, rig.Breakdown.ActiveLeases);
        }
        finally { await rig.Service.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task ServiceStartupAndCompletionPreserveMoreThanFiftyReports()
    {
        var rig = Create();
        for (var index = 0; index < 55; index++)
        {
            var header = new TargetedRecordingHeader($"saved-{index}", "software", "Example",
                DateTimeOffset.UnixEpoch.AddMinutes(index), null, 600, "recording", null, false);
            await rig.Store.CreateAsync(header, CancellationToken.None);
            if (index < 54) await rig.Store.FinishAsync(header.Id, "user", false, CancellationToken.None);
        }
        await rig.Service.StartAsync(CancellationToken.None);
        try
        {
            var recovered = (await rig.Service.ListAsync(CancellationToken.None)).Single(item => item.Id == "saved-54");
            Assert.Equal("interrupted", recovered.StopReason);
            Assert.True(recovered.Incomplete);
            Assert.Equal(55, (await rig.Service.ListAsync(CancellationToken.None)).Count);
            var recording = await rig.Service.BeginAsync("software", 600, CancellationToken.None);
            Assert.False(await rig.Service.DeleteAsync(recording.Id, CancellationToken.None));
            var completed = await rig.Service.FinishAsync(recording.Id, CancellationToken.None);
            Assert.Equal("completed", completed!.Status);
            var all = await rig.Service.ListAsync(CancellationToken.None);
            Assert.Equal(56, all.Count);
            Assert.Contains(all, item => item.Id == "saved-0");
            Assert.True(await rig.Service.DeleteAsync("saved-0", CancellationToken.None));
            Assert.Equal(55, (await rig.Service.ListAsync(CancellationToken.None)).Count);
            Assert.Equal(0, rig.Frames.ActiveLeases);
            Assert.Equal(0, rig.Breakdown.ActiveLeases);
            Assert.Equal(0, rig.Metrics.ActiveLeases);
        }
        finally { await rig.Service.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task ManualStopEndsWithUserStopAndReleasesEveryItemHolder()
    {
        var rig = Create();
        await rig.Service.StartAsync(CancellationToken.None);
        var started = await rig.Service.BeginAsync("software", 600, CancellationToken.None);
        Assert.Equal("recording", started.Status);
        Assert.Equal(1, rig.Metrics.ActiveLeases);
        Assert.Contains(rig.Metrics.Holders, holder => holder.EndsWith(":metric:cpu.usage", StringComparison.Ordinal));
        Assert.Contains(rig.Breakdown.Holders, holder => holder.EndsWith(":software:cpu.usage", StringComparison.Ordinal));
        Assert.Contains(rig.Breakdown.Holders, holder => holder.EndsWith(":processes", StringComparison.Ordinal));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            rig.Service.BeginAsync("software", 600, CancellationToken.None));
        var stopped = await rig.Service.FinishAsync(started.Id, CancellationToken.None);
        Assert.Equal(TargetedRecordingStopReasons.UserStop, stopped?.StopReason);
        Assert.Equal(0, rig.Frames.ActiveLeases);
        Assert.Equal(0, rig.Breakdown.ActiveLeases);
        Assert.Equal(0, rig.Metrics.ActiveLeases);
        var report = await rig.Store.GetAsync(started.Id, CancellationToken.None);
        Assert.Equal("completed", report?.Recording.Status);
        // Default profile items this machine's catalog does not offer are recorded as unavailable.
        var unavailable = report!.Recording.Items.Single(item => item.Kind == "metric" && item.Id == "cpu.temperature");
        Assert.Contains(unavailable.Gaps, gap => gap.Reason == TargetedRecordingGapReasons.ItemUnavailable);
    }

    [Fact]
    public async Task MaximumDurationStopsAndRecordsPerItemSourceIntervalAndPrecision()
    {
        var rig = Create(FastOptions);
        await rig.Service.StartAsync(CancellationToken.None);
        var started = await rig.Service.BeginAsync("software", null,
        [
            new(TargetedRecordingItemKinds.Frames, TargetedRecordingProfileDefaults.FramesItemId),
            new(TargetedRecordingItemKinds.Software, "cpu.usage", "auto", 200, TargetedRecordingSyncPrecisions.High),
            new(TargetedRecordingItemKinds.Metric, "cpu.usage", "load@cpu0", null, TargetedRecordingSyncPrecisions.Low)
        ], 1, CancellationToken.None);
        Assert.Null(started.ProfileId);
        Assert.Equal(3, started.Items.Count);
        var completed = await WaitForCompletionAsync(rig.Store, started.Id);
        Assert.Equal(TargetedRecordingStopReasons.MaximumDuration, completed.Recording.StopReason);
        Assert.NotEmpty(completed.FrameIntervals);
        Assert.NotEmpty(completed.ResourceSamples);
        Assert.NotNull(completed.Summary);
        var software = completed.Recording.Items.Single(item => item.Kind == "software");
        Assert.Equal("auto", software.SelectedSource);
        Assert.Equal(new[] { TargetedRecordingItemCatalogRules.ResourceBreakdownSource }, software.ActualSources);
        Assert.Equal(200, software.SelectedIntervalMs);
        Assert.Equal(TargetedRecordingIntervalOrigins.Item, software.IntervalOrigin);
        Assert.Equal(TargetedRecordingSyncPrecisions.High, software.SyncPrecision);
        Assert.True(software.PointCount > 0);
        var metric = completed.Recording.Items.Single(item => item.Kind == "metric");
        Assert.Equal("load@cpu0", metric.SelectedSource);
        Assert.Equal(TargetedRecordingProfileDefaults.DefaultIntervalMs, metric.SelectedIntervalMs);
        Assert.Equal(TargetedRecordingIntervalOrigins.ProfileDefault, metric.IntervalOrigin);
        Assert.Equal(TargetedRecordingSyncPrecisions.Low, metric.SyncPrecision);
        var frames = completed.Recording.Items.Single(item => item.Kind == "frames");
        Assert.Equal(TargetedRecordingIntervalOrigins.PerPresent, frames.IntervalOrigin);
        Assert.Null(frames.SelectedIntervalMs);
    }

    [Fact]
    public async Task EveryKnownProcessExitingEndsWithAllProcessesExited()
    {
        var rig = Create(FastOptions);
        await rig.Service.StartAsync(CancellationToken.None);
        var started = await rig.Service.BeginAsync("software", 600, CancellationToken.None);
        await Task.Delay(300);
        rig.Breakdown.Processes = [];
        rig.Liveness.Alive.Clear();
        var completed = await WaitForCompletionAsync(rig.Store, started.Id);
        Assert.Equal(TargetedRecordingStopReasons.AllProcessesExited, completed.Recording.StopReason);
        var process = Assert.Single(completed.Recording.Processes);
        Assert.NotNull(process.ExitedAt);
    }

    [Fact]
    public async Task ProcessesThatComeAndGoMergeIntoTheKnownSetUntilTheLastOneExits()
    {
        var rig = Create(FastOptions);
        await rig.Service.StartAsync(CancellationToken.None);
        var started = await rig.Service.BeginAsync("software", 600, CancellationToken.None);
        rig.Liveness.Alive[ProcessB] = true;
        rig.Breakdown.Processes = [ProcessA, ProcessB];
        await Task.Delay(400);
        // A exits; B is still known and running, so the recording continues.
        rig.Breakdown.Processes = [ProcessB];
        rig.Liveness.Alive.TryRemove(ProcessA, out _);
        await Task.Delay(500);
        Assert.Equal("recording", (await rig.Store.GetAsync(started.Id, CancellationToken.None))!.Recording.Status);
        rig.Breakdown.Processes = [];
        rig.Liveness.Alive.Clear();
        var completed = await WaitForCompletionAsync(rig.Store, started.Id);
        Assert.Equal(TargetedRecordingStopReasons.AllProcessesExited, completed.Recording.StopReason);
        Assert.Equal(2, completed.Recording.Processes.Count);
        Assert.All(completed.Recording.Processes, process => Assert.NotNull(process.ExitedAt));
        var exitedA = completed.Recording.Processes.Single(process => process.ProcessId == ProcessA.Pid).ExitedAt!.Value;
        var exitedB = completed.Recording.Processes.Single(process => process.ProcessId == ProcessB.Pid).ExitedAt!.Value;
        Assert.True(exitedA < exitedB);
    }

    [Fact]
    public async Task RecordingThatNeverSeesTheProcessEndsWithNoProcessAndKeepsOnlyItsStatus()
    {
        var rig = Create(FastOptions);
        rig.Breakdown.Processes = [];
        rig.Liveness.Alive.Clear();
        await rig.Service.StartAsync(CancellationToken.None);
        var started = await rig.Service.BeginAsync("software", 600, CancellationToken.None);
        var completed = await WaitForCompletionAsync(rig.Store, started.Id);
        Assert.Equal(TargetedRecordingStopReasons.NoProcess, completed.Recording.StopReason);
        Assert.True(completed.Recording.Incomplete);
        Assert.Empty(completed.FrameIntervals);
        Assert.Empty(completed.ResourceSamples);
        Assert.Equal(0, completed.Recording.StoredBytes);
        Assert.Equal(0, rig.Breakdown.ActiveLeases);
    }

    [Fact]
    public async Task StartRejectsATargetWithoutRunningProcessAndCreatesNoReport()
    {
        var rig = Create();
        rig.StartProcesses = [];
        await rig.Service.StartAsync(CancellationToken.None);
        var error = await Assert.ThrowsAsync<TargetedRecordingStartException>(() =>
            rig.Service.BeginAsync("software", 600, CancellationToken.None));
        Assert.Equal(409, error.StatusCode);
        Assert.Equal("target-not-running", error.Code);
        Assert.Empty(await rig.Service.ListAsync(CancellationToken.None));
        Assert.Equal(0, rig.Breakdown.ActiveLeases);
    }

    [Theory]
    [InlineData("metric", "missing.metric", "auto", null, "mid", "targeted-recording-item-unknown")]
    [InlineData("metric", "cpu.usage", "power@gpu0", null, "mid", "targeted-recording-item-source-invalid")]
    [InlineData("software", "cpu.usage", "load@cpu0", null, "mid", "targeted-recording-item-source-invalid")]
    [InlineData("software", "cpu.usage", "auto", 50, "mid", "targeted-recording-interval-out-of-range")]
    [InlineData("software", "cpu.usage", "auto", 60001, "mid", "targeted-recording-interval-out-of-range")]
    [InlineData("frames", "frames.present", "auto", 1000, "mid", "targeted-recording-interval-out-of-range")]
    [InlineData("software", "cpu.usage", "auto", null, "ultra", "targeted-recording-item-invalid-precision")]
    [InlineData("disk", "cpu.usage", "auto", null, "mid", "targeted-recording-item-invalid-kind")]
    public async Task StartValidatesRequestItemsLikeProfiles(string kind, string id, string source, int? intervalMs,
        string precision, string reason)
    {
        var rig = Create();
        await rig.Service.StartAsync(CancellationToken.None);
        var error = await Assert.ThrowsAsync<TargetedRecordingStartException>(() => rig.Service.BeginAsync(
            "software", null, [new TargetedRecordingItem(kind, id, source, intervalMs, precision)], 600, CancellationToken.None));
        Assert.Equal(400, error.StatusCode);
        Assert.Equal(reason, error.Code);
        Assert.Empty(await rig.Service.ListAsync(CancellationToken.None));
    }

    [Fact]
    public async Task StartRejectsAnUnknownProfileAndAReportOverTheSizeCap()
    {
        var rig = Create(MonitoringSettings.ProductDefaults.TargetedRecording with { MaximumReportKib = 1 });
        await rig.Service.StartAsync(CancellationToken.None);
        var missing = await Assert.ThrowsAsync<TargetedRecordingStartException>(() =>
            rig.Service.BeginAsync("software", "missing", null, 600, CancellationToken.None));
        Assert.Equal((404, TargetedRecordingProfileErrors.ProfileNotFound), (missing.StatusCode, missing.Code));
        var large = await Assert.ThrowsAsync<TargetedRecordingStartException>(() => rig.Service.BeginAsync("software", null,
            [new(TargetedRecordingItemKinds.Software, "cpu.usage", "auto", 100)], 3600, CancellationToken.None));
        Assert.Equal((400, "report-size-limit-exceeded"), (large.StatusCode, large.Code));
    }

    [Fact]
    public async Task TargetsListRunningSoftwareWithTheStartCheckMatcher()
    {
        var rig = Create(records:
        [
            Record("app-google-chrome", "Google Chrome", identity: "app-google-chrome"),
            Record("catalog:app-google-chrome", "Google Chrome", identity: "app-google-chrome"),
            Record("helper", "Helper", role: SoftwareManagementRoles.Support)
        ]);
        rig.StartSegments = [("app-google-chrome", [ProcessA, ProcessB]), ("helper", [(5150, 1)]), ("unknown-runtime", [(6160, 2)])];
        var targets = await rig.Service.ListTargetsAsync(CancellationToken.None);
        Assert.Equal(new[] { ("app-google-chrome", "Google Chrome", 2), ("unknown-runtime", "unknown-runtime", 1) },
            targets.Select(static target => (target.SoftwareId, target.Name, target.ProcessCount)).ToArray());
        // The catalog record the UI may hold finds the processes attributed to the installed record.
        await rig.Service.StartAsync(CancellationToken.None);
        rig.Liveness.Alive[ProcessB] = true;
        var started = await rig.Service.BeginAsync("catalog:app-google-chrome", 600, CancellationToken.None);
        Assert.Equal(2, started.Processes.Count);
        await rig.Service.FinishAsync(started.Id, CancellationToken.None);
    }

    [Fact]
    public void FrameChunkCodecRoundTripsInMicroseconds()
    {
        var at = new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);
        FrameIntervalSample[] samples =
        [
            new(10, 100, FramePresentSource.Dxgi, 1, at, 16.667),
            new(10, 100, FramePresentSource.Dxgi, 1, at.AddMilliseconds(16.667), 16.5),
            new(11, 200, FramePresentSource.Dxgi, 2, at.AddMilliseconds(5), 8.25)
        ];
        var encoded = TargetedFrameChunkCodec.Encode(samples);
        Assert.StartsWith("{\"v\":2", encoded);
        var decoded = TargetedFrameChunkCodec.Decode(encoded);
        Assert.Equal(3, decoded.Count);
        Assert.Equal(new[] { 10, 11, 10 }, decoded.Select(static item => item.ProcessId));
        Assert.All(decoded.Zip(samples.OrderBy(static item => item.EndedAt)), pair =>
        {
            Assert.Equal(pair.Second.ProcessStartKey, pair.First.ProcessStartKey);
            Assert.Equal(pair.Second.SwapChain, pair.First.SwapChain);
            Assert.True(Math.Abs((pair.First.EndedAt - pair.Second.EndedAt).TotalMilliseconds) < 0.001);
            Assert.True(Math.Abs(pair.First.DurationMs - pair.Second.DurationMs) < 0.001);
        });
        var legacy = System.Text.Json.JsonSerializer.Serialize(samples);
        Assert.Equal(3, TargetedFrameChunkCodec.Decode(legacy).Count);
    }

    private static SoftwareRecord Record(string id, string name, string? identity = null, string? role = null)
        => new(id, name, "game", "Running", [], [], new SoftwareOperationCapabilities(false, "None", "missing_uninstall_entry"),
            role, SoftwareIdentityId: identity);

    private Rig Create(TargetedRecordingOptions? options = null, IMetricSnapshotObservationSource? metrics = null,
        IReadOnlyList<SoftwareRecord>? records = null)
    {
        var store = new TargetedRecordingStore(new ResourceManagerDatabase(
            new TestHostEnvironment(Path.Combine(root, "Resource Manager-APP"))));
        var rig = new Rig { Store = store };
        rig.Liveness.Alive[ProcessA] = true;
        rig.Service = new TargetedRecordingService(store,
            new FakeSoftware(records ?? [Record("software", "Example")]), rig.Frames, rig.Breakdown,
            metrics ?? rig.Metrics, new FakeTargets(rig), new FakeCatalog(TestCatalog),
            NullLogger<TargetedRecordingService>.Instance, null, options, rig.Liveness);
        return rig;
    }

    private static async Task<TargetedRecordingReport> WaitForCompletionAsync(TargetedRecordingStore store, string id)
    {
        for (var attempt = 0; attempt < 80; attempt++)
        {
            var report = await store.GetAsync(id, CancellationToken.None);
            if (report?.Recording.Status == "completed") return report;
            await Task.Delay(100);
        }
        throw new TimeoutException("The recording did not finish.");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(root, recursive: true); }
        catch (DirectoryNotFoundException) { }
        catch (IOException) { }
    }

    internal static ResourceBreakdownSnapshot Snapshot(IReadOnlyList<(string SoftwareId, IReadOnlyList<(int Pid, long Key)> Processes)> segments)
        => new(DateTimeOffset.UtcNow,
        [
            new ResourceBreakdownBar("cpu.usage", "%", "capacity", 100, 100, 100, segments
                .Where(static segment => segment.Processes.Count > 0)
                .Select(static segment => new ResourceSoftwareSegment(segment.SoftwareId, segment.SoftwareId, "game", "game",
                    40, 40, segment.Processes.Count, segment.Processes
                        .Select(static process => new ResourceProcessSegment(process.Pid, "example", null, 40, 40, 100)
                        { ProcessStartKey = process.Key }).ToArray())).ToArray()),
            new ResourceBreakdownBar("memory.usage", "B", "capacity", 100, 100, 100, [])
        ]);

    private sealed class Rig
    {
        public TargetedRecordingService Service { get; set; } = null!;
        public TargetedRecordingStore Store { get; init; } = null!;
        public FakeFrames Frames { get; } = new();
        public FakeBreakdown Breakdown { get; } = new();
        public FakeMetrics Metrics { get; } = new();
        public FakeLiveness Liveness { get; } = new();
        /// <summary>Null: the start check sees the recording's own breakdown processes.</summary>
        public IReadOnlyList<(int Pid, long Key)>? StartProcesses { get; set; }
        public IReadOnlyList<(string SoftwareId, IReadOnlyList<(int Pid, long Key)> Processes)>? StartSegments { get; set; }
    }

    private sealed class FakeTargets(Rig rig) : ITargetedRecordingProcessReader
    {
        public Task<ResourceBreakdownSnapshot?> ReadOnceAsync(CancellationToken cancellationToken)
            => Task.FromResult<ResourceBreakdownSnapshot?>(Snapshot(rig.StartSegments
                ?? [("software", rig.StartProcesses ?? [ProcessA])]));
    }

    private sealed class FakeCatalog(IReadOnlyList<TargetedRecordingCatalogItem> items) : ITargetedRecordingItemCatalog
    {
        public Task<IReadOnlyList<TargetedRecordingCatalogItem>> ReadAsync(CancellationToken cancellationToken)
            => Task.FromResult(items);
        public IReadOnlyList<TargetedRecordingCatalogItem> ReadCurrent() => items;
    }

    private sealed class FakeLiveness : ITargetedProcessLiveness
    {
        public ConcurrentDictionary<(int Pid, long Key), bool> Alive { get; } = new();
        public bool IsRunning(TargetedProcessIdentity identity)
            => Alive.ContainsKey((identity.ProcessId, identity.ProcessStartKey));
    }

    private sealed class TestHostEnvironment(string contentRootPath) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "ResourceManager.Tests";
        public string ContentRootPath { get; set; } = contentRootPath;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class FakeSoftware(IReadOnlyList<SoftwareRecord> records) : ISoftwareRegistryView
    {
        public Task<IReadOnlyList<SoftwareRecord>> GetSoftwareAsync(CancellationToken cancellationToken)
            => Task.FromResult(records);
        public Task<IReadOnlyList<SoftwareRecord>> RefreshSoftwareAsync(CancellationToken cancellationToken)
            => Task.FromResult(records);
    }

    private sealed class FakeFrames : IFrameTimingObservationSource
    {
        public int ActiveLeases;
        public IDisposable AcquireSubscription()
        {
            Interlocked.Increment(ref ActiveLeases);
            return new Lease(() => Interlocked.Decrement(ref ActiveLeases));
        }
        public FrameTimingSnapshot? Read(TimeSpan window) => null;
        public FrameIntervalBatch? ReadIntervals(DateTimeOffset after, DateTimeOffset through)
            => new(through, true, [new FrameIntervalSample(ProcessA.Pid, ProcessA.Key, FramePresentSource.Dxgi,
                1, through, 10)]);
    }

    private sealed class FakeBreakdown : IResourceBreakdownObservationSource
    {
        public int ActiveLeases;
        public ConcurrentBag<string> Holders { get; } = [];
        public volatile IReadOnlyList<(int Pid, long Key)> Processes = [ProcessA];
        public IDisposable AcquireSubscription(string subscriptionId, ResourceBreakdownSampleRequest request,
            TimeSpan refreshInterval)
        {
            Holders.Add(subscriptionId);
            Interlocked.Increment(ref ActiveLeases);
            return new Lease(() => Interlocked.Decrement(ref ActiveLeases));
        }
        public ResourceBreakdownSnapshot ReadLatest(ResourceBreakdownSampleRequest request)
            => Snapshot([("software", Processes)]);
    }

    private sealed class FakeMetrics : IMetricSnapshotObservationSource
    {
        public int ActiveLeases;
        public ConcurrentBag<string> Holders { get; } = [];
        public IDisposable AcquireSubscription(string subscriptionId, MetricSampleRequest request,
            TimeSpan refreshInterval)
        {
            Holders.Add(subscriptionId);
            Interlocked.Increment(ref ActiveLeases);
            return new Lease(() => Interlocked.Decrement(ref ActiveLeases));
        }
        public HardwareMetricSnapshot? ReadLatest(MetricSampleRequest request) => null;
    }

    private sealed class Lease(Action dispose) : IDisposable
    {
        private Action? action = dispose;
        public void Dispose() => Interlocked.Exchange(ref action, null)?.Invoke();
    }
}
