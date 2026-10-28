using FanControlCore;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Infrastructure.Control;
using ResourceManager.App.Application.Metrics;
using ResourceManager.App.Infrastructure.Control.Writers;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using System.Text.Json;
using System.Threading.Channels;
using Xunit.Abstractions;

namespace Resource_Manager_APP.Tests;

public sealed class FanCurveTemperatureFeedTests(ITestOutputHelper output)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task SameFeedRestartsAndRestoresTemperatureSubscriptionAndPush()
    {
        var pushed = Channel.CreateUnbounded<double>();
        await using var core = new FanControlCoreClient(new FanTestEnvironment(), (_, _) => new DelegateFanSession(_ =>
            JsonSerializer.SerializeToElement(new { ok = true, execution = "software",
                curve = new[] { new { temperatureCelsius = 60, percent = 35 } } }),
            push: samples => pushed.Writer.TryWrite(Assert.Single(samples).Celsius)));
        await core.StartSession();
        await core.SendAsync("setcurve", new Dictionary<string, object?> { ["fan"] = 0 }, default);
        Assert.True(core.NeedsTemperatures());
        var metrics = new RecordedTemperatureSource(output);
        using var feed = new FanCurveTemperatureFeed(metrics, core, new HardwareMetricCatalogState());
        for (var run = 1; run <= 2; run++)
        {
            metrics.Celsius = 40 + run;
            await feed.StartAsync(default);
            try
            {
                double actual;
                do { actual = await pushed.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)); }
                while (actual != metrics.Celsius);
                Assert.Equal(metrics.Celsius, actual);
                Assert.Equal(["cpu.temperature"], metrics.Held);
            }
            finally { await feed.StopAsync(default); }
            Assert.Empty(metrics.Held);
            Assert.True(core.NeedsTemperatures());
        }
    }

    [Theory]
    [InlineData("", false, false)]
    [InlineData("cpu", true, false)]
    [InlineData("gpu", false, true)]
    [InlineData("any", true, true)]
    public void SubscriptionSelectsOnlyRequiredTemperatureKinds(string kind, bool cpu, bool gpu)
    {
        var snapshot = Snapshot(("cpu.temperature", 50d, Now), ("gpu.0.temperature", 60d, Now),
            ("gpu.2.temperature", 70d, Now), ("gpu.0.usage", 99d, Now));
        var request = FanCurveTemperatureFeed.TemperatureRequest(snapshot, kind.Length == 0 ? [] : [kind]);
        Assert.Equal(cpu, request.Includes("cpu.temperature"));
        Assert.Equal(gpu, request.Includes("gpu.0.temperature"));
        Assert.Equal(gpu, request.Includes("gpu.2.temperature"));
        Assert.False(request.Includes("gpu.temperature"));
        Assert.False(request.Includes("gpu.0.usage"));
        var samples = FanCurveTemperatureFeed.Collect(snapshot, request, Now);
        Assert.Equal((cpu ? 1 : 0) + (gpu ? 2 : 0), samples.Count);
    }

    [Fact]
    public void RecentFrameCannotRenewOldOrUnknownTemperature()
    {
        var snapshot = Snapshot(("cpu.temperature", 50d, Now), ("gpu.0.temperature", 45d, Now.AddSeconds(-10)),
            ("gpu.1.temperature", 55d, Now.AddSeconds(1)), ("gpu.2.temperature", double.NaN, Now));
        var request = FanCurveTemperatureFeed.TemperatureRequest(snapshot, ["any"]);
        Assert.Equal("cpu", Assert.Single(FanCurveTemperatureFeed.Collect(snapshot, request, Now)).Name);
        Assert.Empty(FanCurveTemperatureFeed.Collect(snapshot, request, Now.AddSeconds(20)));
        Assert.Empty(FanCurveTemperatureFeed.Collect(snapshot with { Datasets = new Dictionary<string, HardwareMetricDatasetObservation>() }, request, Now));
    }

    [Fact]
    public async Task TypedPushEqualsFormerBatchAndPassesTheSameListWithoutProtocolCalls()
    {
        var snapshot = Snapshot(("cpu.temperature", 51d, Now), ("gpu.0.temperature", 63d, Now),
            ("gpu.2.temperature", 72d, Now), ("gpu.3.temperature", 80d, Now.AddSeconds(-10)));
        var request = FanCurveTemperatureFeed.TemperatureRequest(snapshot, ["any"]);
        var samples = FanCurveTemperatureFeed.Collect(snapshot, request, Now);
        // The former transport encoded these two maps. This reference stays in the test only.
        var oldValues = new Dictionary<string, double> { ["cpu"] = 51, ["gpu0"] = 63, ["gpu2"] = 72 };
        var oldTimes = oldValues.Keys.ToDictionary(name => name, _ => Now.UtcTicks);
        using var oldJson = JsonDocument.Parse(JsonSerializer.Serialize(new { samples = oldValues, observedAtUtcTicks = oldTimes }));
        foreach (var sample in samples)
        {
            Assert.Equal(oldJson.RootElement.GetProperty("samples").GetProperty(sample.Name).GetDouble(), sample.Celsius);
            Assert.Equal(oldJson.RootElement.GetProperty("observedAtUtcTicks").GetProperty(sample.Name).GetInt64(), sample.ObservedAtUtcTicks);
        }
        Assert.Equal(oldValues.Count, samples.Count);
        IReadOnlyList<FanTemperatureSample>? received = null;
        await using var client = new FanControlCoreClient(new FanTestEnvironment(), (_, _) => new DelegateFanSession(r =>
        {
            Assert.Equal("describe", r.GetProperty("op").GetString()); // temp must never enter Handle.
            return JsonSerializer.SerializeToElement(new { ok = true });
        }, push: batch => received = batch));
        await client.StartSession();
        Assert.True(await client.PushTemperaturesAsync(samples, default));
        Assert.Same(samples, received);
        Assert.True(await client.PushTemperaturesAsync(samples, default, skipIfBusy: false));
        Assert.Same(samples, received);
    }

    [Fact]
    public async Task FailedTypedPushReturnsFalseAndReleasesTheAcceptedOwner()
    {
        var calls = 0;
        var log = new FanBoundaryLogger<FanControlCoreClient>();
        await using var client = new FanControlCoreClient(new FanTestEnvironment(), (_, _) => new DelegateFanSession(_ =>
            JsonSerializer.SerializeToElement(new { ok = true }), push: _ =>
            {
                if (Interlocked.Increment(ref calls) == 1) throw new ApplicationException("typed-push-failure");
            }), log);
        await client.StartSession();
        Assert.False(await client.PushTemperaturesAsync([new("cpu", 60, DateTime.UtcNow.Ticks)], default));
        Assert.Equal("fan-core-failed", client.UnavailableReason);
        Assert.Single(log.Messages, message => message.Contains("temperature push failed"));
        Assert.True(await client.PushTemperaturesAsync([new("cpu", 61, DateTime.UtcNow.Ticks)], default));
        await client.StopSessionAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task TimedHoldersFollowKindsAndFirstCompleteBatchArrivesWithinFiveSeconds()
    {
        var pushes = Channel.CreateUnbounded<(long At, FanTemperatureSample[] Samples)>();
        var session = new FanSoftwareCurveRunTests.RunSession();
        session.SetKinds();
        session.SetNeeded(true); // A hand-back retry can need the run with no temperature kind.
        session.Push = samples => pushes.Writer.TryWrite((DateTime.UtcNow.Ticks, samples.ToArray()));
        await using var client = new FanControlCoreClient(new FanTestEnvironment(), (_, _) => session);
        await client.StartSession();
        var metrics = new RecordedTemperatureSource(output);
        var catalog = new HardwareMetricCatalogState();
        catalog.PublishCatalogProbe(Snapshot(("cpu.temperature", 50d, Now), ("gpu.0.temperature", 60d, Now), ("gpu.2.temperature", 70d, Now)));
        using var feed = new FanCurveTemperatureFeed(metrics, client, catalog);
        await feed.StartAsync(default);
        try
        {
            Assert.Empty(metrics.Held);
            Assert.True(client.SoftwareCurveRunNeeded);
            output.WriteLine($"{DateTime.UtcNow:O} kind=empty; held=[]; run-needed=true");
            await Task.Delay(FanCurveTemperatureFeed.Interval * 2);
            Assert.False(pushes.Reader.TryRead(out _));
            foreach (var kind in new[] { "cpu", "gpu", "any" })
            {
                var changedAt = DateTime.UtcNow.Ticks;
                session.SetKinds(kind);
                var expected = kind == "cpu" ? new[] { "cpu.temperature" }
                    : kind == "gpu" ? ["gpu.0.temperature", "gpu.2.temperature"]
                    : ["cpu.temperature", "gpu.0.temperature", "gpu.2.temperature"];
                Assert.Equal(expected, metrics.Held);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                (long At, FanTemperatureSample[] Samples) batch;
                var names = expected.Select(id => id == "cpu.temperature" ? "cpu" : "gpu" + id.Split('.')[1]).Order().ToArray();
                do { batch = await pushes.Reader.ReadAsync(timeout.Token); }
                while (batch.At < changedAt || !batch.Samples.Select(s => s.Name).Order().SequenceEqual(names));
                var latency = TimeSpan.FromTicks(batch.At - changedAt);
                output.WriteLine($"{new DateTime(changedAt, DateTimeKind.Utc):O} kind={kind}; first-complete-push={latency.TotalMilliseconds:F3} ms");
                Assert.InRange(latency, TimeSpan.Zero, TimeSpan.FromSeconds(5));
                Assert.All(batch.Samples, sample => Assert.InRange(batch.At - sample.ObservedAtUtcTicks, 0, FanCurveTemperatureFeed.MaximumSampleAge.Ticks));
            }
            session.SetKinds();
            Assert.Empty(metrics.Held);
            Assert.True(client.SoftwareCurveRunNeeded);
            while (pushes.Reader.TryRead(out _)) { }
            await Task.Delay(FanCurveTemperatureFeed.Interval * 3);
            Assert.False(pushes.Reader.TryRead(out _));
            session.SetKinds("cpu");
            Assert.Equal(["cpu.temperature"], metrics.Held);
        }
        finally { await feed.StopAsync(default); }
        Assert.Empty(metrics.Held);
        output.WriteLine($"{DateTime.UtcNow:O} feed=stopped; held=[]");
        var changes = metrics.Changes;
        var reads = metrics.Reads;
        session.SetKinds("any");
        catalog.PublishCatalogProbe(Snapshot(("gpu.4.temperature", 80d, Now)));
        await Task.Delay(FanCurveTemperatureFeed.Interval * 2);
        Assert.Equal(changes, metrics.Changes);
        Assert.Equal(reads, metrics.Reads);
        Assert.Empty(metrics.Held);
    }

    private sealed class RecordedTemperatureSource(ITestOutputHelper output) : IMetricSnapshotObservationSource
    {
        internal double Celsius = 50;
        internal string[] Held = [];
        internal int Changes, Reads;
        public IDisposable AcquireSubscription(string id, MetricSampleRequest request, TimeSpan refreshInterval)
        {
            Assert.Empty(Held);
            Assert.Equal("control.fan-curve.temperature", id);
            Assert.Equal(FanCurveTemperatureFeed.Interval, refreshInterval);
            Held = request.Ids.Order().ToArray();
            Record();
            return new Lease(this);
        }
        private void Record()
        {
            Changes++;
            output.WriteLine($"{DateTime.UtcNow:O} held=[{string.Join(',', Held)}]");
        }
        public HardwareMetricSnapshot ReadLatest(MetricSampleRequest request)
        {
            Interlocked.Increment(ref Reads);
            var now = DateTimeOffset.UtcNow;
            return Snapshot(("cpu.temperature", Celsius, now), ("gpu.0.temperature", 60, now), ("gpu.2.temperature", 70, now));
        }
        private sealed class Lease(RecordedTemperatureSource owner) : IDisposable
        {
            private int disposed;
            public void Dispose()
            {
                if (Interlocked.Exchange(ref disposed, 1) != 0) return;
                owner.Held = [];
                owner.Record();
            }
        }
    }

    private static HardwareMetricSnapshot Snapshot(params (string Id, double Value, DateTimeOffset At)[] values)
        => new(Now, HardwareMetricTestData.EmptyCpu, HardwareMetricTestData.EmptyMemory, HardwareMetricTestData.EmptyVirtualMemory, [], HardwareMetricTestData.EmptyGpuInventory, values.ToDictionary(v => v.Id,
            v => new MetricReadingValue(Id: v.Id, NumericValue: v.Value, Unit: "°C", Percent: null)))
        {
            Datasets = values.ToDictionary(v => v.Id, v => new HardwareMetricDatasetObservation(v.Id,
                SamplingObservationStatus.Current, 1, v.At.UtcTicks, 1, 1, 1, 1))
        };
}
