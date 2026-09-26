using ResourceManager.App.Infrastructure.ResourceBreakdown;

namespace Resource_Manager_APP.Tests;

public sealed class ProcessCpuDeltaTrackerTests
{
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
