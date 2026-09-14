using ResourceManager.NativeUi;

namespace Resource_Manager_APP.Tests;

public sealed class BackendServiceLossHandlerTests
{
    [Fact]
    public void AvailabilityEvent_IsDispatchedOnceToUnavailableTransition()
    {
        var source = new FakeAvailabilitySource();
        var dispatched = new Queue<Action>();
        var transitionReasons = new List<BackendServiceUnavailableReason>();
        using var handler = new BackendServiceLossHandler(
            source,
            isShuttingDown: static () => false,
            dispatched.Enqueue,
            unavailable => transitionReasons.Add(unavailable.Reason));

        source.Raise(BackendServiceUnavailableEventArgs.OwnedProcessExited(12));
        source.Raise(BackendServiceUnavailableEventArgs.ConsecutiveProbeFailures(3));

        var work = Assert.Single(dispatched);
        Assert.Empty(transitionReasons);
        work();
        Assert.Equal(
            [BackendServiceUnavailableReason.OwnedProcessExited],
            transitionReasons);
    }

    [Fact]
    public void Dispose_UnsubscribesAndInvalidatesAlreadyQueuedTransition()
    {
        var source = new FakeAvailabilitySource();
        var dispatched = new Queue<Action>();
        var transitionCount = 0;
        var handler = new BackendServiceLossHandler(
            source,
            isShuttingDown: static () => false,
            dispatched.Enqueue,
            _ => transitionCount++);
        source.Raise(BackendServiceUnavailableEventArgs.ConsecutiveProbeFailures(3));
        Assert.Single(dispatched);
        var queued = dispatched.Dequeue();

        handler.Dispose();
        queued();
        source.Raise(BackendServiceUnavailableEventArgs.OwnedProcessExited(9));

        Assert.Equal(0, transitionCount);
        Assert.Empty(dispatched);
    }

    [Fact]
    public void ExistingApplicationShutdown_DoesNotQueueBackendLossWork()
    {
        var source = new FakeAvailabilitySource();
        var dispatched = new Queue<Action>();
        using var handler = new BackendServiceLossHandler(
            source,
            isShuttingDown: static () => true,
            dispatched.Enqueue,
            _ => throw new InvalidOperationException("Shutdown must not be invoked."));

        source.Raise(BackendServiceUnavailableEventArgs.OwnedProcessExited(1));

        Assert.Empty(dispatched);
    }

    [Fact]
    public void FailedDispatch_ReopensHandlerForNextAvailabilityEvent()
    {
        var source = new FakeAvailabilitySource();
        var dispatchAttempts = 0;
        var transitionCount = 0;
        using var handler = new BackendServiceLossHandler(
            source,
            isShuttingDown: static () => false,
            action =>
            {
                dispatchAttempts++;
                if (dispatchAttempts == 1)
                {
                    throw new InvalidOperationException("Dispatcher is not ready.");
                }

                action();
            },
            _ => transitionCount++);

        source.Raise(BackendServiceUnavailableEventArgs.ConsecutiveProbeFailures(3));
        source.Raise(BackendServiceUnavailableEventArgs.OwnedProcessExited(5));

        Assert.Equal(2, dispatchAttempts);
        Assert.Equal(1, transitionCount);
    }

    private sealed class FakeAvailabilitySource : IBackendServiceAvailabilitySource
    {
        public event EventHandler<BackendServiceUnavailableEventArgs>? AvailabilityLost;

        public void Raise(BackendServiceUnavailableEventArgs unavailable) =>
            AvailabilityLost?.Invoke(this, unavailable);
    }
}
