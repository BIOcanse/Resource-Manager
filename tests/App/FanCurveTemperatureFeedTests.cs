using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Infrastructure.Control;

namespace Resource_Manager_APP.Tests;

public sealed class FanCurveTemperatureFeedTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

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
