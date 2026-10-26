using ResourceManager.NativeUi;

namespace Resource_Manager_APP.Tests;

public sealed class BackendServiceAvailabilityMonitorTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompletionWaitsForRaceLoserCleanupEvenWhenWinnerFaults(bool winnerFaults)
    {
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observed = new List<BackendServiceUnavailableEventArgs>();
        var monitor = new BackendServiceAvailabilityMonitor(
            _ => Task.FromResult(true),
            _ => winnerFaults
                ? Task.FromException<BackendServiceUnavailableEventArgs>(new IOException("Process waiter failed."))
                : Task.FromResult(BackendServiceUnavailableEventArgs.OwnedProcessExited(7)),
            delayAsync: async (_, token) =>
            {
                try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                finally
                {
                    canceled.TrySetResult();
                    await release.Task;
                }
            });
        monitor.AvailabilityLost += (_, unavailable) => observed.Add(unavailable);
        monitor.Start();
        try
        {
            await canceled.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(monitor.Completion.IsCompleted);
            var disposal = monitor.DisposeAsync().AsTask();
            Assert.False(disposal.IsCompleted);
            release.TrySetResult();
            await disposal.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(winnerFaults ? BackendServiceUnavailableReason.MonitorFault
                : BackendServiceUnavailableReason.OwnedProcessExited, Assert.Single(observed).Reason);
        }
        finally
        {
            release.TrySetResult();
            await monitor.DisposeAsync();
        }
    }

    [Fact]
    public async Task ProbeFailures_RequireConsecutiveThresholdAndHealthyResetsCount()
    {
        var outcomes = new Queue<bool>([false, false, true, false, false, false]);
        var observed = new List<BackendServiceUnavailableEventArgs>();
        using var monitor = new BackendServiceAvailabilityMonitor(
            _ => Task.FromResult(outcomes.Dequeue()),
            waitForOwnedProcessExitAsync: null,
            new BackendServiceAvailabilityMonitorOptions(
                TimeSpan.FromSeconds(1),
                ConsecutiveProbeFailureLimit: 3),
            static (_, _) => Task.CompletedTask);
        monitor.AvailabilityLost += (_, unavailable) => observed.Add(unavailable);

        monitor.Start();
        await monitor.Completion.WaitAsync(TimeSpan.FromSeconds(2));

        var unavailable = Assert.Single(observed);
        Assert.Equal(
            BackendServiceUnavailableReason.ConsecutiveProbeFailures,
            unavailable.Reason);
        Assert.Null(unavailable.ExitCode);
        Assert.Empty(outcomes);
    }

    [Fact]
    public async Task OwnedProcessExit_WinsWithoutWaitingForFirstHealthProbe()
    {
        var probeCalls = 0;
        var observed = new List<BackendServiceUnavailableEventArgs>();
        using var monitor = new BackendServiceAvailabilityMonitor(
            _ =>
            {
                Interlocked.Increment(ref probeCalls);
                return Task.FromResult(true);
            },
            _ => Task.FromResult(
                BackendServiceUnavailableEventArgs.OwnedProcessExited(73)),
            delayAsync: static (_, cancellationToken) =>
                Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken));
        monitor.AvailabilityLost += (_, unavailable) => observed.Add(unavailable);

        monitor.Start();
        await monitor.Completion.WaitAsync(TimeSpan.FromSeconds(2));

        var unavailable = Assert.Single(observed);
        Assert.Equal(BackendServiceUnavailableReason.OwnedProcessExited, unavailable.Reason);
        Assert.Equal(73, unavailable.ExitCode);
        Assert.Equal(0, Volatile.Read(ref probeCalls));
    }

    [Fact]
    public async Task RepeatedStart_CreatesOnlyOneHealthMonitor()
    {
        var probeCalls = 0;
        var observed = new List<BackendServiceUnavailableEventArgs>();
        using var monitor = new BackendServiceAvailabilityMonitor(
            _ =>
            {
                Interlocked.Increment(ref probeCalls);
                return Task.FromResult(false);
            },
            waitForOwnedProcessExitAsync: null,
            new BackendServiceAvailabilityMonitorOptions(
                TimeSpan.FromSeconds(1),
                ConsecutiveProbeFailureLimit: 2),
            static (_, _) => Task.CompletedTask);
        monitor.AvailabilityLost += (_, unavailable) => observed.Add(unavailable);

        monitor.Start();
        monitor.Start();
        await monitor.Completion.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Single(observed);
        Assert.Equal(2, Volatile.Read(ref probeCalls));
    }

    [Fact]
    public async Task Dispose_CancelsMonitoringWithoutPublishingBackendLoss()
    {
        var observed = new List<BackendServiceUnavailableEventArgs>();
        var monitor = new BackendServiceAvailabilityMonitor(
            _ => Task.FromResult(false),
            waitForOwnedProcessExitAsync: null,
            delayAsync: static (_, cancellationToken) =>
                Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken));
        monitor.AvailabilityLost += (_, unavailable) => observed.Add(unavailable);
        monitor.Start();
        var completion = monitor.Completion;

        monitor.Dispose();
        await completion.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Empty(observed);
    }

    [Fact]
    public void Options_RejectNonPositiveProbeSettings()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new BackendServiceAvailabilityMonitorOptions(
                TimeSpan.Zero,
                ConsecutiveProbeFailureLimit: 1).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new BackendServiceAvailabilityMonitorOptions(
                TimeSpan.FromSeconds(1),
                ConsecutiveProbeFailureLimit: 0).Validate());
    }

    [Fact]
    public void TerminalGate_NormalStopPreventsLaterFailurePublication()
    {
        var gate = new BackendServiceAvailabilityTerminalGate();

        Assert.True(gate.TryStop());
        Assert.False(gate.TryPublishUnavailable());
        Assert.False(gate.TryStop());
    }

    [Fact]
    public void TerminalGate_FailurePublicationPreventsStopFromReclassifyingTerminalState()
    {
        var gate = new BackendServiceAvailabilityTerminalGate();

        Assert.True(gate.TryPublishUnavailable());
        Assert.False(gate.TryStop());
        Assert.False(gate.TryPublishUnavailable());
    }
}
