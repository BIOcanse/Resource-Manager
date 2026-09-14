using ResourceManager.App.Infrastructure.ServiceHosting;

namespace Resource_Manager_APP.Tests;

public sealed class InteractiveUserSessionActivationTrackerTests
{
    [Fact]
    public void Observe_AdmitsEachSessionOncePerContinuousActivePeriod()
    {
        var tracker = new InteractiveUserSessionActivationTracker();

        Assert.Equal([3U, 7U], tracker.Observe([7U, 3U, 7U]));
        Assert.Empty(tracker.Observe([3U, 7U]));
        Assert.Empty(tracker.Observe([3U]));
        Assert.Equal([7U], tracker.Observe([3U, 7U]));
    }

    [Fact]
    public void Observe_DoesNotReadmitAUiThatExitsWhileSessionStaysActive()
    {
        var tracker = new InteractiveUserSessionActivationTracker();

        Assert.Equal([11U], tracker.Observe([11U]));
        Assert.Empty(tracker.Observe([11U]));
        Assert.Empty(tracker.Observe([11U]));
    }

    [Fact]
    public void Observe_ReactivatesOnlyAfterSessionLeavesTheActiveSet()
    {
        var tracker = new InteractiveUserSessionActivationTracker();

        Assert.Equal([21U], tracker.Observe([21U]));
        Assert.Empty(tracker.Observe([]));
        Assert.Equal([21U], tracker.Observe([21U]));
        Assert.Empty(tracker.Observe([0U]));
    }
}
