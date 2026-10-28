using ResourceManager.App.Domain.Control;

namespace Resource_Manager_APP.Tests;

public sealed partial class FanControlPlaneNativeLifetimeTests
{
    [Fact]
    public async Task NativeCompletionRetainsTheRunUntilControlPlanePublishesAppliedIntent()
    {
        await using var fixture = new Fixture();
        await fixture.StartAsync();
        var completed = BusinessBlockTestSupport.Signal();
        var publish = BusinessBlockTestSupport.Signal();
        fixture.Layer.AfterWrite = async () =>
        {
            completed.TrySetResult();
            await publish.Task;
        };
        var edges = new List<(bool Pending, bool Applied)>();
        fixture.Core.PendingCommandsChanged += () => edges.Add((fixture.Core.PendingCommands, fixture.Plane.AppliedFanControlExists));
        var apply = fixture.Plane.SetObjectSettingsAsync("fan:oem0", [Duty(45)], default);
        try
        {
            await completed.Task.WaitAsync(BusinessBlockTestSupport.TestTimeout);
            await fixture.WaitRunningAsync();
            Assert.Equal(45d, fixture.Duties[0]);
            Assert.True(fixture.Core.PendingCommands);
            Assert.False(fixture.Plane.AppliedFanControlExists);
            Assert.Equal(0, fixture.Closed);
            Assert.Equal([(true, false)], edges);
        }
        finally { publish.TrySetResult(); }
        Assert.Equal(ControlApplyStatuses.Applied, Assert.Single((await apply).LastApply.Outcomes).Status);
        Assert.True(fixture.Plane.AppliedFanControlExists);
        Assert.False(fixture.Core.PendingCommands);
        Assert.Equal([(true, false), (false, true)], edges);
        Assert.Equal(45d, fixture.Duties[0]);
        Assert.Equal(0, fixture.Closed);
        await fixture.WaitRunningAsync();
    }

    [Fact]
    public async Task MultipleFanWritesKeepPendingContinuousBetweenNativeCommandsAndThroughPublication()
    {
        await using var fixture = new Fixture();
        await fixture.StartAsync();
        var firstCompleted = BusinessBlockTestSupport.Signal();
        var secondAllowed = BusinessBlockTestSupport.Signal();
        var writes = 0;
        fixture.Writer.AfterWrite = async () =>
        {
            if (Interlocked.Increment(ref writes) != 1) return;
            firstCompleted.TrySetResult();
            await secondAllowed.Task;
        };
        var edges = new List<bool>();
        fixture.Core.PendingCommandsChanged += () => edges.Add(fixture.Core.PendingCommands);
        var apply = fixture.Plane.ApplyDesiredStateAsync(new([
            new("fan:oem0", [Duty(40)]), new("fan:oem1", [Duty(65)])]), default);
        try
        {
            await firstCompleted.Task.WaitAsync(BusinessBlockTestSupport.TestTimeout);
            await fixture.WaitRunningAsync();
            Assert.True(fixture.Core.PendingCommands);
            Assert.False(fixture.Plane.AppliedFanControlExists);
            Assert.Equal([true], edges);
            Assert.Equal(40d, fixture.Duties[0]);
            Assert.Null(fixture.Duties[1]);
            Assert.Equal(1, fixture.Opened);
            Assert.Equal(0, fixture.Closed);
        }
        finally { secondAllowed.TrySetResult(); }
        Assert.All((await apply).LastApply.Outcomes, outcome => Assert.Equal(ControlApplyStatuses.Applied, outcome.Status));
        Assert.Equal(2, writes);
        Assert.Equal([true, false], edges);
        Assert.True(fixture.Plane.AppliedFanControlExists);
        Assert.Equal(40d, fixture.Duties[0]);
        Assert.Equal(65d, fixture.Duties[1]);
        Assert.Equal(1, fixture.Opened);
        Assert.Equal(0, fixture.Closed);
    }

    [Fact]
    public async Task FailedFanWritePublishesItsOutcomeThenClearsPendingAndStopsTheNativeBlock()
    {
        await using var fixture = new Fixture();
        await fixture.StartAsync();
        fixture.FailWrites = true;
        var openedBeforePublication = 0;
        var closedBeforePublication = 0;
        fixture.Layer.AfterWrite = () =>
        {
            openedBeforePublication = fixture.Opened;
            closedBeforePublication = fixture.Closed;
            Assert.True(fixture.Core.PendingCommands);
            // Inventory may finish before the run takes its holder. During
            // publication exactly the current run's session must remain open.
            Assert.InRange(openedBeforePublication, 1, 2);
            Assert.Equal(openedBeforePublication - 1, closedBeforePublication);
            return Task.CompletedTask;
        };
        var publicationObserved = false;
        fixture.Plane.AppliedIntentsChanged += () => publicationObserved = true;
        var outcomeWasPublishedAtZero = false;
        fixture.Core.PendingCommandsChanged += () =>
        {
            if (!fixture.Core.PendingCommands)
                outcomeWasPublishedAtZero = publicationObserved;
        };
        var result = await fixture.Plane.SetObjectSettingsAsync("fan:oem0", [Duty(45)], default);
        Assert.Equal(ControlApplyStatuses.Failed, Assert.Single(result.LastApply.Outcomes).Status);
        Assert.True(outcomeWasPublishedAtZero);
        Assert.False(fixture.Plane.AppliedFanControlExists);
        Assert.False(fixture.Core.PendingCommands);
        await fixture.WaitStoppedAsync();
        Assert.Equal(openedBeforePublication, fixture.Opened);
        Assert.Equal(closedBeforePublication + 1, fixture.Closed);
    }

    [Fact]
    public async Task RemovingTheLastFanSettingRetainsPendingThroughReleasePublicationThenStops()
    {
        await using var fixture = new Fixture();
        await fixture.StartAsync();
        await fixture.Plane.SetObjectSettingsAsync("fan:oem0", [Duty(45)], default);
        var completed = BusinessBlockTestSupport.Signal();
        var publish = BusinessBlockTestSupport.Signal();
        fixture.Layer.AfterRelease = async () => { completed.TrySetResult(); await publish.Task; };
        var release = fixture.Plane.SetObjectSettingsAsync("fan:oem0", [], default);
        try
        {
            await completed.Task.WaitAsync(BusinessBlockTestSupport.TestTimeout);
            Assert.True(fixture.Core.PendingCommands);
            Assert.True(fixture.Plane.AppliedFanControlExists);
            Assert.Null(fixture.Duties[0]);
            Assert.Equal(0, fixture.Closed);
        }
        finally { publish.TrySetResult(); }
        await release;
        Assert.False(fixture.Plane.AppliedFanControlExists);
        Assert.False(fixture.Core.PendingCommands);
        await fixture.WaitStoppedAsync();
        Assert.Equal(1, fixture.Closed);
    }

    [Fact]
    public async Task FailedReassertionRetainsPendingUntilThePreviousAppliedFactIsWithdrawn()
    {
        await using var fixture = new Fixture();
        await fixture.StartAsync();
        await fixture.Plane.SetObjectSettingsAsync("fan:oem0", [Duty(45)], default);
        // Finite inventory discovery may close before the run takes its first
        // session holder. Only closure after this applied write could undo it.
        var closedBeforeReassertion = fixture.Closed;
        fixture.FailWrites = true;
        var completed = BusinessBlockTestSupport.Signal();
        var publish = BusinessBlockTestSupport.Signal();
        fixture.Layer.AfterWrite = async () => { completed.TrySetResult(); await publish.Task; };
        var reassertion = fixture.Plane.ReassertAsync(default);
        try
        {
            await completed.Task.WaitAsync(BusinessBlockTestSupport.TestTimeout);
            Assert.True(fixture.Core.PendingCommands);
            Assert.True(fixture.Plane.AppliedFanControlExists);
            Assert.Equal(closedBeforeReassertion, fixture.Closed);
            Assert.Equal(45, fixture.Duties[0]);
        }
        finally { publish.TrySetResult(); }
        Assert.Equal(ControlApplyStatuses.Failed, Assert.Single((await reassertion).Outcomes).Status);
        Assert.False(fixture.Plane.AppliedFanControlExists);
        Assert.False(fixture.Core.PendingCommands);
        await fixture.WaitStoppedAsync();
        Assert.Equal(closedBeforeReassertion + 1, fixture.Closed);
    }

    [Fact]
    public async Task ActualRpmSubscriptionKeepsOneRunSessionWithoutPublishingCommandsAndStopsOnWithdrawal()
    {
        await using var fixture = new Fixture(observeActual: true);
        await fixture.StartAsync();
        var pendingEdges = 0;
        fixture.Core.PendingCommandsChanged += () => Interlocked.Increment(ref pendingEdges);
        var actual = fixture.Actual!;
        var subscription = actual.AcquireSubscription();
        try
        {
            await fixture.WaitRunningAsync();
            Assert.True(fixture.Scheduler.Authority.Read<bool>(BackendItems.FanReadingsInUse).Value);
            Assert.False(fixture.Scheduler.Authority.Read<bool>(BackendItems.SoftwareFanCurveEnabled).Value);
            Assert.False(fixture.Plane.AppliedFanControlExists);
            var startup = await fixture.Clock.NextTimerAsync();
            startup.Fire();
            var periodic = await fixture.Clock.NextTimerAsync();
            await BusinessBlockTestSupport.Until(() => actual.Current.Values.Any(value =>
                value.CapabilityId == "fan.rpm" && value.Number == 1800));
            var firstReads = fixture.Reads;
            Assert.True(firstReads > 0);
            periodic.Fire();
            await BusinessBlockTestSupport.Until(() => fixture.Reads > firstReads);
            Assert.False(fixture.Core.PendingCommands);
            Assert.Equal(0, Volatile.Read(ref pendingEdges));
            Assert.Equal(1, fixture.Opened);
            Assert.Equal(0, fixture.Closed);
            subscription.Dispose();
            await fixture.WaitStoppedAsync();
            await BusinessBlockTestSupport.State(fixture.Scheduler, BackendUids.ControlActualStateReader, BusinessBlockState.Stopped);
            Assert.True(periodic.Disposed);
            Assert.False(fixture.Scheduler.Authority.Read<bool>(BackendItems.FanReadingsInUse).Value);
            Assert.Equal(1, fixture.Closed);
        }
        finally { subscription.Dispose(); }
    }

    private static ControlSetting Duty(double percent) => new("fan.duty", Number: percent, Unit: "%");
}
