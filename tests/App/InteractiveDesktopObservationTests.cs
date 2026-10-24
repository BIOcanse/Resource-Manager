using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Infrastructure.ResourceBreakdown;
using ResourceManager.Shared.Runtime;

namespace Resource_Manager_APP.Tests;

public sealed class InteractiveDesktopObservationTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);
    private static readonly long Start = Epoch.AddMinutes(-1).UtcDateTime.ToFileTimeUtc();
    private static DesktopProcessWindowObservation Window(int pid, long? start = null)
        => new(pid, start ?? Start, true, false);
    private static InteractiveDesktopObservation Sample(int session, int foreground, int[] windows,
        bool complete = true, bool foregroundKnown = true)
        => new(999, Start, session, Epoch.UtcTicks, complete, foregroundKnown,
            foregroundKnown ? Window(foreground) : null, windows.Select(pid => Window(pid)).ToArray());

    [Fact]
    public void VerifiedHelperUsesHostEvidenceWhenItsOwnWindowObservationIsIncomplete()
    {
        var store = new InteractiveDesktopObservationStore(new Clock(), _ => true, _ => 1);
        Assert.True(store.Publish(Sample(1, 100, [100], complete: false)));
        var result = WindowsProcessRuntimeStateSnapshot.Capture([new(100, Start), new(101, Start), new(102, Start)],
            store, new Dictionary<int, int> { [101] = 100 });
        Assert.True(result.Processes[new(101, Start)].ForegroundFocused);
        Assert.False(result.Processes.ContainsKey(new(102, Start)));
    }

    [Fact]
    public void HelperWithMismatchedWindowCreationCannotInheritAValidHostsState()
    {
        var store = new InteractiveDesktopObservationStore(new Clock(), _ => true, _ => 1);
        Assert.True(store.Publish(Sample(1, 100, [100, 101])));
        var result = WindowsProcessRuntimeStateSnapshot.Capture([new(100, Start), new(101, Start + 1)],
            store, new Dictionary<int, int> { [101] = 100 });
        Assert.True(result.Processes[new(100, Start)].ForegroundFocused);
        Assert.False(result.Processes.ContainsKey(new(101, Start + 1)));
    }

    [Fact]
    public void ProjectsEachProcessFromItsOwnInteractiveSession()
    {
        var clock = new Clock();
        var store = new InteractiveDesktopObservationStore(clock, _ => true, key => key.ProcessId < 200 ? 1 : 3);
        Assert.True(store.Publish(Sample(1, 100, [100, 101])));
        Assert.True(store.Publish(Sample(3, 200, [200, 201])));
        var result = store.Read([new(100, Start), new(101, Start), new(200, Start), new(201, Start)]);
        Assert.True(result.Processes[new(100, Start)].ForegroundFocused);
        Assert.True(result.Processes[new(200, Start)].ForegroundFocused);
        Assert.Equal(HostManagerRuntimeStates.ForegroundUnfocused, result.Processes[new(101, Start)].RuntimeState);
        Assert.False(result.Processes[new(201, Start)].ForegroundFocused);
    }

    [Fact]
    public void StaleSampleCannotBeRenewedByReceiptOrRead()
    {
        var clock = new Clock();
        var store = new InteractiveDesktopObservationStore(clock, _ => true, _ => 1);
        var sample = Sample(1, 100, [100]);
        Assert.True(store.Publish(sample));
        clock.Now = Epoch.AddSeconds(2);
        Assert.False(store.Publish(sample));
        Assert.Null(store.IsForeground(100, Start));
    }

    [Fact]
    public void PidReuseAndUnknownForegroundAreNeverConfirmedBackground()
    {
        var store = new InteractiveDesktopObservationStore(new Clock(), _ => true, _ => 1);
        Assert.True(store.Publish(Sample(1, 100, [100, 101])));
        Assert.Null(store.IsForeground(101, Start + 1));
        Assert.True(store.Publish(Sample(1, 100, [100, 101], foregroundKnown: false)));
        Assert.Null(store.IsForeground(101, Start));
    }

    [Fact]
    public void IncompleteEnumerationDoesNotInventWindowlessBackgroundProcesses()
    {
        var store = new InteractiveDesktopObservationStore(new Clock(), _ => true, _ => 1);
        Assert.True(store.Publish(Sample(1, 100, [100, 101], complete: false)));
        Assert.True(store.IsForeground(100, Start));
        Assert.False(store.IsForeground(101, Start));
        Assert.Null(store.IsForeground(102, Start));
    }

    [Fact]
    public void UnreadableProcessIsMatchedByPidAndKeepsWindowlessProcessesBackground()
    {
        var store = new InteractiveDesktopObservationStore(new Clock(), _ => true, _ => 1);
        var unverified = DesktopProcessWindowObservation.UnverifiedCreationTime;
        Assert.True(store.Publish(new InteractiveDesktopObservation(999, Start, 1, Epoch.UtcTicks, true, true,
            Window(100, unverified), [Window(100, unverified), Window(101)])));
        var result = store.Read([new(100, Start), new(101, Start), new(102, Start)]);
        Assert.True(result.Processes[new(100, Start)].ForegroundFocused);
        Assert.Equal(HostManagerRuntimeStates.ForegroundUnfocused, result.Processes[new(101, Start)].RuntimeState);
        Assert.Equal(HostManagerRuntimeStates.BackgroundProcess, result.Processes[new(102, Start)].RuntimeState);
        // A process that started after the observation cannot inherit the PID-only row.
        Assert.Null(store.IsForeground(100, Epoch.AddSeconds(1).UtcDateTime.ToFileTimeUtc()));
    }

    [Fact]
    public void RejectsUnverifiedPublisherAndDuplicateProcessRows()
    {
        var store = new InteractiveDesktopObservationStore(new Clock(), _ => false, _ => 1);
        Assert.False(store.Publish(Sample(1, 100, [100])));
        store = new(new Clock(), _ => true, _ => 1);
        Assert.False(store.Publish(Sample(1, 100, [100, 100])));
        Assert.Empty(store.Read([new(100, long.MaxValue)]).Processes);
    }

    private sealed class Clock : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = Epoch;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
