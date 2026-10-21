using ResourceManager.App.Infrastructure.ResourceBreakdown;

namespace Resource_Manager_APP.Tests;

public sealed class ProcessCpuDeltaTrackerTests
{
    [Fact]
    public void RecordedIntervalKeepsExactCaptureWindowAndValidZero()
    {
        var tracker = new ProcessCpuDeltaTracker(); var at = DateTimeOffset.UnixEpoch; var key = new ProcessInstanceKey(42, 100);
        var first = tracker.UpdateInterval(true, at, new() { [key] = TimeSpan.FromSeconds(10) }, 4);
        Assert.Empty(first.Percentages); Assert.Null(first.Interval);
        var second = tracker.UpdateInterval(true, at.AddMilliseconds(2500), new() { [key] = TimeSpan.FromSeconds(15) }, 4);
        Assert.Equal(at.AddMilliseconds(2500), second.CapturedAt); Assert.Equal(TimeSpan.FromMilliseconds(2500), second.Interval);
        Assert.Equal(50, second.Percentages[key], 6);
        var zero = tracker.UpdateInterval(true, at.AddSeconds(5), new() { [key] = TimeSpan.FromSeconds(15) }, 4);
        Assert.Equal(0, zero.Percentages[key]);
    }

    [Theory]
    [InlineData(0)] [InlineData(-1)]
    public void NonpositiveElapsedIsUnavailableAndActualCaptureRebaselines(int seconds)
    {
        var tracker = new ProcessCpuDeltaTracker(); var at = DateTimeOffset.UnixEpoch; var key = new ProcessInstanceKey(42, 100);
        tracker.Update(true, at, new() { [key] = TimeSpan.FromSeconds(10) }, 4);
        var invalid = tracker.UpdateInterval(true, at.AddSeconds(seconds), new() { [key] = TimeSpan.FromSeconds(12) }, 4);
        Assert.Empty(invalid.Percentages); Assert.Null(invalid.Interval);
        var next = tracker.UpdateInterval(true, at.AddSeconds(seconds + 2), new() { [key] = TimeSpan.FromSeconds(16) }, 4);
        Assert.Equal(50, next.Percentages[key], 6); Assert.Equal(TimeSpan.FromSeconds(2), next.Interval);
    }

    [Fact]
    public void CounterDecreaseAndMissingIdentityDoNotInventZeroAndRebaseline()
    {
        var tracker = new ProcessCpuDeltaTracker(); var at = DateTimeOffset.UnixEpoch; var key = new ProcessInstanceKey(42, 100);
        tracker.Update(true, at, new() { [key] = TimeSpan.FromSeconds(10) }, 4);
        Assert.Empty(tracker.Update(true, at.AddSeconds(1), new() { [key] = TimeSpan.FromSeconds(1) }, 4));
        Assert.Equal(50, tracker.Update(true, at.AddSeconds(2), new() { [key] = TimeSpan.FromSeconds(3) }, 4)[key], 6);
        Assert.Empty(tracker.Update(true, at.AddSeconds(3), new(), 4));
        Assert.Empty(tracker.Update(true, at.AddSeconds(4), new() { [key] = TimeSpan.FromSeconds(4) }, 4));
    }

    [Fact]
    public void Update_NonCpuSampleDoesNotAdvanceOrReplaceCpuBaseline()
    {
        var tracker = new ProcessCpuDeltaTracker();
        var firstAt = DateTimeOffset.UnixEpoch;

        Assert.Empty(tracker.Update(
            includeCpu: true,
            firstAt,
            new Dictionary<ProcessInstanceKey, TimeSpan> { [new(42, 100)] = TimeSpan.FromSeconds(10) },
            processorCount: 4));

        Assert.Empty(tracker.Update(
            includeCpu: false,
            firstAt.AddSeconds(1),
            new Dictionary<ProcessInstanceKey, TimeSpan> { [new(42, 100)] = TimeSpan.Zero },
            processorCount: 4));

        var result = tracker.Update(
            includeCpu: true,
            firstAt.AddSeconds(2),
            new Dictionary<ProcessInstanceKey, TimeSpan> { [new(42, 100)] = TimeSpan.FromSeconds(14) },
            processorCount: 4);

        Assert.Equal(50, result[new ProcessInstanceKey(42, 100)], precision: 6);
    }

    [Fact]
    public void Update_ReusedPidDoesNotConsumePreviousProcessBaseline()
    {
        var tracker = new ProcessCpuDeltaTracker();
        var firstAt = DateTimeOffset.UnixEpoch;

        Assert.Empty(tracker.Update(
            true,
            firstAt,
            new Dictionary<ProcessInstanceKey, TimeSpan> { [new(42, 100)] = TimeSpan.FromSeconds(10) },
            4));

        Assert.Empty(tracker.Update(
            true,
            firstAt.AddSeconds(1),
            new Dictionary<ProcessInstanceKey, TimeSpan> { [new(42, 200)] = TimeSpan.FromSeconds(1) },
            4));

        var result = tracker.Update(
            true,
            firstAt.AddSeconds(2),
            new Dictionary<ProcessInstanceKey, TimeSpan> { [new(42, 200)] = TimeSpan.FromSeconds(3) },
            4);

        Assert.DoesNotContain(new ProcessInstanceKey(42, 100), result.Keys);
        Assert.Equal(50, result[new ProcessInstanceKey(42, 200)], precision: 6);
    }
}
