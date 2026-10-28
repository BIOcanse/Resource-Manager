using ResourceManager.App.Application.Metrics;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Infrastructure.Control;
using ResourceManager.App.Infrastructure.Control.Writers;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;

namespace Resource_Manager_APP.Tests;

public sealed class FanCurveRunLifetimeTests
{
    [Fact]
    public async Task TemperaturePushAndEntryShareStopAndParentWaitsForHandBack()
    {
        var entered = Signal();
        var stopped = Signal();
        var handBack = Signal();
        var pushed = Signal();
        var session = new FanSoftwareCurveRunTests.RunSession { Run = async stop =>
        {
            entered.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, stop); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            stopped.TrySetResult();
            await handBack.Task;
        }, TemperaturePushed = () => pushed.TrySetResult() };
        await using var client = new FanControlCoreClient(new FanTestEnvironment(), (_, _) => session);
        await client.StartSession();
        var metrics = new Temperatures();
        using var feed = new FanCurveTemperatureFeed(metrics, client, new HardwareMetricCatalogState());
        Task childStop = Task.CompletedTask;
        try
        {
            await feed.StartAsync(default);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await pushed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, metrics.Active);
            childStop = feed.StopAsync(default);
            await stopped.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await BusinessBlockTestSupport.Until(() => metrics.Active == 0);
            var parentStop = client.StopSessionAsync().AsTask();
            Assert.False(childStop.IsCompleted);
            Assert.False(parentStop.IsCompleted);
            Assert.False(session.Disposed);
            handBack.TrySetResult();
            await Task.WhenAll(childStop, parentStop).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(session.Disposed);
            Assert.Equal(0, metrics.Active);
            var reads = metrics.Reads;
            await Task.Delay(FanCurveTemperatureFeed.Interval * 2);
            Assert.Equal(reads, metrics.Reads);
        }
        finally { handBack.TrySetResult(); await feed.StopAsync(default); await childStop; }
    }

    [Fact]
    public async Task EntryFaultFailsFeedAndReleasesTemperatureSubscription()
    {
        var fault = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var session = new FanSoftwareCurveRunTests.RunSession { Run = stop => { calls++; return fault.Task.WaitAsync(stop); } };
        await using var client = new FanControlCoreClient(new FanTestEnvironment(), (_, _) => session);
        await client.StartSession();
        var metrics = new Temperatures();
        using var feed = new FanCurveTemperatureFeed(metrics, client, new HardwareMetricCatalogState());
        await feed.StartAsync(default);
        await metrics.Read.Task.WaitAsync(TimeSpan.FromSeconds(5));
        fault.SetException(new ApplicationException("curve-entry-fault"));
        var error = await Assert.ThrowsAsync<ApplicationException>(() => feed.ExecuteTask!);
        Assert.Equal("curve-entry-fault", error.Message);
        Assert.Equal(0, metrics.Active);
        Assert.Equal(1, calls); // No autonomous restart.
        Assert.False(session.Disposed);
        await feed.StopAsync(default);
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private sealed class Temperatures : IMetricSnapshotObservationSource
    {
        internal int Active, Reads;
        internal TaskCompletionSource Read = Signal();
        public IDisposable AcquireSubscription(string id, MetricSampleRequest request, TimeSpan interval)
        { Active++; return new Lease(this); }
        public HardwareMetricSnapshot? ReadLatest(MetricSampleRequest request)
        {
            Interlocked.Increment(ref Reads);
            Read.TrySetResult();
            var now = DateTimeOffset.UtcNow;
            return new(now, HardwareMetricTestData.EmptyCpu, HardwareMetricTestData.EmptyMemory,
                HardwareMetricTestData.EmptyVirtualMemory, [], HardwareMetricTestData.EmptyGpuInventory,
                new Dictionary<string, MetricReadingValue> { ["cpu.temperature"] = new("cpu.temperature", NumericValue: 60, Unit: "°C", Percent: null) })
            { Datasets = new Dictionary<string, HardwareMetricDatasetObservation> {
                ["cpu.temperature"] = new("cpu.temperature", SamplingObservationStatus.Current, 1, now.UtcTicks, 1, 1, 1, 1) } };
        }
        private sealed class Lease(Temperatures owner) : IDisposable { public void Dispose() => owner.Active--; }
    }
}
