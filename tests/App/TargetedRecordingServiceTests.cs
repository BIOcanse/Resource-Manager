using Microsoft.Data.Sqlite;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Application.FrameTiming;
using ResourceManager.App.Application.Metrics;
using ResourceManager.App.Application.ResourceBreakdown;
using ResourceManager.App.Application.Software;
using ResourceManager.App.Domain.FrameTiming;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Domain.Software;
using ResourceManager.App.Infrastructure.Monitoring.TargetedRecording;
using ResourceManager.App.Infrastructure.Persistence;

namespace Resource_Manager_APP.Tests;

public sealed class TargetedRecordingServiceTests : IDisposable
{
    private static readonly int TestProcessId = Environment.ProcessId;
    private static readonly long TestProcessStartKey = System.Diagnostics.Process.GetCurrentProcess().StartTime.ToFileTimeUtc();
    private readonly string root = Path.Combine(Path.GetTempPath(), $"rm-targeted-service-{Guid.NewGuid():N}");

    [Fact]
    public async Task ManualStopCompletesTheRecordingAndReleasesObservationLeases()
    {
        var (service, store, frames, breakdown, metrics) = CreateService();
        await service.StartAsync(CancellationToken.None);
        var started = await service.BeginAsync("software", 600, CancellationToken.None);
        Assert.Equal("recording", started.Status);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.BeginAsync("software", 600, CancellationToken.None));
        var stopped = await service.FinishAsync(started.Id, CancellationToken.None);
        Assert.Equal("user", stopped?.StopReason);
        Assert.Equal(0, frames.ActiveLeases);
        Assert.Equal(0, breakdown.ActiveLeases);
        Assert.Equal(0, metrics.ActiveLeases);
        Assert.Equal("completed", (await store.GetAsync(started.Id, CancellationToken.None))?.Recording.Status);
    }

    [Fact]
    public async Task MaximumDurationStopsAutomaticallyAndSavesTargetFrames()
    {
        var (service, store, _, _, _) = CreateService();
        await service.StartAsync(CancellationToken.None);
        var started = await service.BeginAsync("software", 1, CancellationToken.None);
        var completed = await WaitForCompletionAsync(store, started.Id);
        Assert.Equal("max-duration", completed.Recording.StopReason);
        Assert.NotEmpty(completed.FrameIntervals);
        Assert.NotEmpty(completed.ResourceSamples);
        Assert.NotNull(completed.Summary);
    }

    [Fact]
    public async Task LastProcessExitStopsAutomatically()
    {
        var (service, store, _, breakdown, _) = CreateService();
        breakdown.ExitAfterFirstSample = true;
        await service.StartAsync(CancellationToken.None);
        var started = await service.BeginAsync("software", 600, CancellationToken.None);
        var completed = await WaitForCompletionAsync(store, started.Id);
        Assert.Equal("process-exit", completed.Recording.StopReason);
        Assert.True(breakdown.Reads >= 2);
    }

    private (TargetedRecordingService Service, TargetedRecordingStore Store,
        FakeFrames Frames, FakeBreakdown Breakdown, FakeMetrics Metrics) CreateService()
    {
        var store = new TargetedRecordingStore(new ResourceManagerDatabase(
            new TestHostEnvironment(Path.Combine(root, "Resource Manager-APP"))));
        var frames = new FakeFrames();
        var breakdown = new FakeBreakdown();
        var metrics = new FakeMetrics();
        var service = new TargetedRecordingService(store, new FakeSoftware(), frames, breakdown, metrics,
            NullLogger<TargetedRecordingService>.Instance);
        return (service, store, frames, breakdown, metrics);
    }

    private static async Task<TargetedRecordingReport> WaitForCompletionAsync(TargetedRecordingStore store, string id)
    {
        for (var attempt = 0; attempt < 60; attempt++)
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

    private sealed class TestHostEnvironment(string contentRootPath) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "ResourceManager.Tests";
        public string ContentRootPath { get; set; } = contentRootPath;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class FakeSoftware : ISoftwareRegistryView
    {
        private static readonly IReadOnlyList<SoftwareRecord> Records =
        [new SoftwareRecord("software", "Example", "Game", "Game", "Running", [], [], "",
            new SoftwareOperationCapabilities(false, "", "", ""), null)];
        public Task<IReadOnlyList<SoftwareRecord>> GetSoftwareAsync(CancellationToken cancellationToken)
            => Task.FromResult(Records);
        public Task<IReadOnlyList<SoftwareRecord>> RefreshSoftwareAsync(CancellationToken cancellationToken)
            => Task.FromResult(Records);
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
            => new(through, true, [new FrameIntervalSample(TestProcessId, TestProcessStartKey, FramePresentSource.Dxgi,
                1, through, 10)]);
    }

    private sealed class FakeBreakdown : IResourceBreakdownObservationSource
    {
        public int ActiveLeases;
        public int Reads;
        public bool ExitAfterFirstSample;
        public IDisposable AcquireSubscription(string subscriptionId, ResourceBreakdownSampleRequest request,
            TimeSpan refreshInterval)
        {
            Interlocked.Increment(ref ActiveLeases);
            return new Lease(() => Interlocked.Decrement(ref ActiveLeases));
        }
        public ResourceBreakdownSnapshot ReadLatest(ResourceBreakdownSampleRequest request)
        {
            var hasProcess = !ExitAfterFirstSample || Interlocked.Increment(ref Reads) == 1;
            if (!ExitAfterFirstSample) Interlocked.Increment(ref Reads);
            IReadOnlyList<ResourceSoftwareSegment> software = hasProcess
                ? [new ResourceSoftwareSegment("software", "Example", "Game", "Game", 40, 40, 1,
                    [new ResourceProcessSegment(ExitAfterFirstSample ? int.MaxValue : TestProcessId, "example", null, 40, 40, 100)
                    { ProcessStartKey = ExitAfterFirstSample ? 9876 : TestProcessStartKey }])]
                : [];
            return new ResourceBreakdownSnapshot(DateTimeOffset.UtcNow,
                [new ResourceBreakdownBar("cpu.usage", "CPU", "%", "capacity", 100, 100, 100, software)]);
        }
    }

    private sealed class FakeMetrics : IMetricSnapshotObservationSource
    {
        public int ActiveLeases;
        public IDisposable AcquireSubscription(string subscriptionId, MetricSampleRequest request,
            TimeSpan refreshInterval)
        {
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
