using ResourceManager.Shared.BusinessBlocks;

namespace ResourceManager.NativeUi;

/// <summary>Owns connection attempts, health loss and reconnect waits on the UI context.</summary>
internal sealed class NativeUiBackendConnectionBlock : IBusinessBlock
{
    private readonly Func<BackendServiceSession> createSession;
    private readonly Func<BackendConnectionState, BackendServiceSession?, long,
        BackendServiceUnavailableEventArgs?, Task> publish;
    private readonly Func<BackendServiceSession, CancellationToken, Task> connectAsync;
    private readonly Func<TimeSpan, CancellationToken, Task> delayAsync;
    private CancellationTokenSource? reconnectDelay;
    private bool running;

    internal NativeUiBackendConnectionBlock(
        Func<BackendServiceSession> createSession,
        Func<BackendConnectionState, BackendServiceSession?, long,
            BackendServiceUnavailableEventArgs?, Task> publish,
        Func<BackendServiceSession, CancellationToken, Task>? connectAsync = null,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null)
    {
        this.createSession = createSession ?? throw new ArgumentNullException(nameof(createSession));
        this.publish = publish ?? throw new ArgumentNullException(nameof(publish));
        this.connectAsync = connectAsync ?? ((session, token) => session.EnsureConnectedAsync(token));
        this.delayAsync = delayAsync ?? Task.Delay;
    }

    internal BackendServiceSession? CurrentSession { get; private set; }
    internal bool IsReady { get; private set; }
    internal long Generation { get; private set; }

    // A healthy request belongs to the frontend caller; it never replaces a healthy session.
    internal void RequestRetry()
    {
        if (running && !IsReady) reconnectDelay?.Cancel();
    }

    public async Task RunAsync(BusinessBlockRunContext context)
    {
        if (running) throw new InvalidOperationException("The backend connection flow is already running.");
        running = true;
        BackendServiceSession? session = null;
        EventHandler<BackendServiceUnavailableEventArgs>? onLost = null;

        async Task ReleaseSessionAsync()
        {
            if (session is null) return;
            var releasing = session;
            session = null;
            releasing.AvailabilityLost -= onLost;
            onLost = null;
            try { await releasing.DisposeAsync(); }
            finally { CurrentSession = null; }
        }

        try
        {
            context.StopNotification.ThrowIfCancellationRequested();
            context.ReportRunning();
            var reconnecting = false;
            var failedAttemptCount = 0;
            while (!context.StopNotification.IsCancellationRequested)
            {
                if (reconnecting)
                {
                    await WaitForReconnectAsync(BackendReconnectPolicy.GetDelay(failedAttemptCount),
                        context.StopNotification);
                    context.StopNotification.ThrowIfCancellationRequested();
                    await publish(BackendConnectionState.Reconnecting, null, Generation, null);
                }

                session = createSession();
                CurrentSession = session;
                Generation = checked(Generation + 1);
                var loss = new TaskCompletionSource<BackendServiceUnavailableEventArgs>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                onLost = (_, unavailable) => loss.TrySetResult(unavailable);
                session.AvailabilityLost += onLost;
                // Preserve the existing shell's Reconnecting -> Connecting attempt sequence.
                await publish(BackendConnectionState.Connecting, null, Generation, null);

                BackendServiceUnavailableEventArgs? unavailable = null;
                try
                {
                    await connectAsync(session, context.StopNotification);
                    context.StopNotification.ThrowIfCancellationRequested();
                }
                catch (OperationCanceledException) when (context.StopNotification.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception error)
                {
                    unavailable = BackendServiceUnavailableEventArgs.StartupFailed(error);
                }

                var reachedReady = unavailable is null;
                if (reachedReady)
                {
                    IsReady = true;
                    await publish(BackendConnectionState.Ready, session, Generation, null);
                    unavailable = await loss.Task.WaitAsync(context.StopNotification);
                }

                IsReady = false;
                // Completion of this publication acknowledges that data consumers released the session.
                await publish(BackendConnectionState.Degraded, null, Generation, unavailable);
                await ReleaseSessionAsync();
                failedAttemptCount = reachedReady ? 0 : reconnecting ? failedAttemptCount + 1 : 0;
                reconnecting = true;
            }
        }
        catch (OperationCanceledException) when (context.StopNotification.IsCancellationRequested)
        {
        }
        finally
        {
            IsReady = false;
            try
            {
                await publish(BackendConnectionState.ShuttingDown, null, Generation, null);
            }
            finally
            {
                try { await ReleaseSessionAsync(); }
                finally { running = false; }
            }
        }
    }

    private async Task WaitForReconnectAsync(TimeSpan delay, CancellationToken stopNotification)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(stopNotification);
        reconnectDelay = cancellation;
        try { await delayAsync(delay, cancellation.Token); }
        catch (OperationCanceledException) when (!stopNotification.IsCancellationRequested)
        {
            // The existing manual retry wakes this delay; it does not start a second flow.
        }
        finally
        {
            reconnectDelay = null;
        }
    }
}
