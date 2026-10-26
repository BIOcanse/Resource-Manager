using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Infrastructure.Control;
using ResourceManager.App.Application.Metrics;
using ResourceManager.App.Infrastructure.Control.Writers;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using System.Text.Json;
using System.Threading.Channels;

namespace Resource_Manager_APP.Tests;

public sealed class FanCurveTemperatureFeedTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task SameFeedRestartsAndRestoresTemperatureSubscriptionAndPush()
    {
        var pushed = Channel.CreateUnbounded<double>();
        using var core = new FanControlCoreClient(new FanTestEnvironment(), (_, _) => new DelegateFanSession(request =>
        {
            if (request.GetProperty("op").GetString() == "temp")
                pushed.Writer.TryWrite(request.GetProperty("samples").GetProperty("cpu").GetDouble());
            return JsonSerializer.SerializeToElement(new { ok = true, execution = "software",
                curve = new[] { new { temperatureCelsius = 60, percent = 35 } } });
        }));
        await core.SendAsync("setcurve", new Dictionary<string, object?> { ["fan"] = 0 }, default);
        Assert.True(core.NeedsTemperatures());
        var metrics = new RestartTemperatureSource();
        using var feed = new FanCurveTemperatureFeed(metrics, core, new HardwareMetricCatalogState());
        for (var run = 1; run <= 2; run++)
        {
            metrics.Celsius = 40 + run;
            await feed.StartAsync(default);
            try
            {
                await metrics.Acquired.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
                double actual;
                do { actual = await pushed.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)); }
                while (actual != metrics.Celsius);
                Assert.Equal(metrics.Celsius, actual);
                Assert.Equal(1, metrics.Active);
            }
            finally { await feed.StopAsync(default); }
            Assert.Equal(0, metrics.Active);
            Assert.True(core.NeedsTemperatures());
        }
    }

    private sealed class RestartTemperatureSource : IMetricSnapshotObservationSource
    {
        internal readonly Channel<bool> Acquired = Channel.CreateUnbounded<bool>();
        internal double Celsius;
        internal int Active;
        public IDisposable AcquireSubscription(string id, MetricSampleRequest request, TimeSpan refreshInterval)
        {
            Assert.True(request.Includes("cpu.temperature"));
            Interlocked.Increment(ref Active);
            Acquired.Writer.TryWrite(true);
            return new Lease(this);
        }
        public HardwareMetricSnapshot ReadLatest(MetricSampleRequest request)
            => Snapshot(("cpu.temperature", Celsius, DateTimeOffset.UtcNow));
        private sealed class Lease(RestartTemperatureSource owner) : IDisposable
        {
            private int disposed;
            public void Dispose()
            {
                if (Interlocked.Exchange(ref disposed, 1) == 0) Interlocked.Decrement(ref owner.Active);
            }
        }
    }

    [Fact]
    public void TemperatureRequestsAvoidThePerTickConstantArrayAllocation()
    {
        var snapshot = Snapshot(("cpu.temperature", 50d, Now), ("gpu.0.temperature", 60d, Now));
        MetricSampleRequest PriorRequest() => MetricSampleRequest.ForIds(new[] { "cpu.temperature" }.Concat(
            snapshot.Items.Keys.Where(id => id.StartsWith("gpu.", StringComparison.OrdinalIgnoreCase)
                && id.EndsWith(".temperature", StringComparison.OrdinalIgnoreCase))));
        for (var i = 0; i < 128; i++) { _ = PriorRequest(); _ = FanCurveTemperatureFeed.TemperatureRequest(snapshot); }
        const int count = 256;
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < count; i++) _ = PriorRequest();
        var priorBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < count; i++) _ = FanCurveTemperatureFeed.TemperatureRequest(snapshot);
        var currentBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(currentBytes <= priorBytes - count * 32,
            $"Expected to remove a one-element reference array per tick: prior={priorBytes}, current={currentBytes}.");
    }

    [Fact]
    public void SubscriptionContainsEachGpuTemperatureAndNoOtherGpuMetrics()
    {
        var snapshot = Snapshot(("cpu.temperature", 50d, Now), ("gpu.0.temperature", 60d, Now), ("gpu.2.temperature", 70d, Now), ("gpu.0.usage", 99d, Now));
        var request = FanCurveTemperatureFeed.TemperatureRequest(snapshot);
        Assert.True(request.Includes("cpu.temperature"));
        Assert.True(request.Includes("gpu.0.temperature"));
        Assert.True(request.Includes("gpu.2.temperature"));
        Assert.False(request.Includes("gpu.temperature"));
        Assert.False(request.Includes("gpu.0.usage"));
        var samples = FanCurveTemperatureFeed.Collect(snapshot, Now);
        Assert.Equal(60, samples["gpu0"].Celsius);
        Assert.Equal(70, samples["gpu2"].Celsius);
        Assert.Equal(Now.UtcTicks, samples["gpu2"].ObservedAtUtcTicks);
    }

    [Fact]
    public void RecentFrameCannotRenewOldOrUnknownTemperature()
    {
        var snapshot = Snapshot(("cpu.temperature", 50d, Now), ("gpu.0.temperature", 45d, Now.AddSeconds(-10)), ("gpu.1.temperature", 55d, Now.AddSeconds(1)));
        Assert.Equal(["cpu"], FanCurveTemperatureFeed.Collect(snapshot, Now).Keys);
        Assert.Empty(FanCurveTemperatureFeed.Collect(snapshot, Now.AddSeconds(20)));
        Assert.Empty(FanCurveTemperatureFeed.Collect(snapshot with { Datasets = new Dictionary<string, HardwareMetricDatasetObservation>() }, Now));
    }

    private static HardwareMetricSnapshot Snapshot(params (string Id, double Value, DateTimeOffset At)[] values)
        => new(Now, HardwareMetricTestData.EmptyCpu, HardwareMetricTestData.EmptyMemory, HardwareMetricTestData.EmptyVirtualMemory, [], HardwareMetricTestData.EmptyGpuInventory, values.ToDictionary(v => v.Id,
            v => new MetricReadingValue(Id: v.Id, NumericValue: v.Value, Unit: "°C", Percent: null)))
        {
            Datasets = values.ToDictionary(v => v.Id, v => new HardwareMetricDatasetObservation(v.Id,
                SamplingObservationStatus.Current, 1, v.At.UtcTicks, 1, 1, 1, 1))
        };
}
