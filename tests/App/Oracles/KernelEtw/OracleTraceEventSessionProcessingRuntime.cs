using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;
using ResourceManager.ReadWrite.Windows.Processes.Etw;

using ResourceManager.App.Infrastructure.Telemetry.Etw;
namespace Resource_Manager_APP.Tests.Oracles.KernelEtw;

internal sealed class OracleTraceEventSessionProcessingRuntime(
    TraceEventSession session,
    KernelEtwSessionKeywordControl? keywordControl = null,
    OracleKernelEtwSubscriptionDispatcher? dispatcher = null,
    IEtwConsumerThreadObserver? consumerThreadObserver = null,
    int? blockId = null) : IDisposable
{
    private readonly int? consumerBlockId = DedicatedEtwConsumerThread.ValidateBlockId(blockId, consumerThreadObserver);
    private Microsoft.Diagnostics.Tracing.ETWTraceEventSource? source;
    private Task? processingTask;
    private Thread? consumerThread;
    private int processing;
    private readonly object stopGate = new();
    private KernelEtwSessionStopResult? stopResult;
    private bool stopAttempted;
    private bool disposed;
    private readonly KernelEtwSessionRuntimeCleanup cleanup = new();

    public bool IsProcessing => Volatile.Read(ref processing) != 0;

    public int EventsLost => session.EventsLost;

    public void UpdateSubscriptions(
        IReadOnlyList<OracleKernelEtwSubscriptionRegistration> subscriptions,
        KernelEtwSessionContext context)
    {
        lock (stopGate)
        {
            ObjectDisposedException.ThrowIf(disposed || stopResult is not null, this);
            if (keywordControl is null || dispatcher is null)
            {
                throw new InvalidOperationException("This processing runtime has no kernel keyword controller.");
            }
            dispatcher.UpdateSubscriptions(subscriptions, context, keywordControl.UpdateKeywords);
        }
    }

    public void StartProcessing(Action<Exception?> completed)
    {
        ArgumentNullException.ThrowIfNull(completed);
        // Source is lazy and may start the native session. Capture it only when
        // processing is requested, then retain it for terminal consumer cleanup.
        var processingSource = session.Source;
        if (Interlocked.CompareExchange(ref processing, 1, 0) != 0)
        {
            throw new InvalidOperationException("ETW 事件处理已经启动。");
        }

        source = processingSource;
        processingTask = DedicatedEtwConsumerThread.Start(
            consumerBlockId,
            session.SessionName,
            () => processingSource.Process(),
            out consumerThread,
            consumerThreadObserver).ContinueWith(
                task =>
                {
                    var failure = task.IsFaulted
                        ? task.Exception?.GetBaseException()
                        : task.IsCanceled ? new TaskCanceledException(task) : null;
                    Volatile.Write(ref processing, 0);
                    completed(failure);
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
    }

    public KernelEtwSessionStopResult Stop(TimeSpan waitTimeout)
    {
        if (waitTimeout < TimeSpan.Zero && waitTimeout != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(waitTimeout));
        }

        lock (stopGate)
        {
            if (stopResult is not null)
            {
                return stopResult;
            }

            ObjectDisposedException.ThrowIf(disposed, this);
            stopAttempted = true;
            var failures = new List<string>();
            var sessionStopSucceeded = false;
            try
            {
                // TraceEvent.Stop marks itself stopped even when ControlTrace is denied.
                // Use the owned native controller so a later reconcile really retries.
                cleanup.StopSession(StopSession);
                sessionStopSucceeded = true;
            }
            catch (Exception ex) when (KernelEtwSessionKeywordControl.IsAccessDenied(ex))
            {
                // A denied control operation did not stop the owned session. Leave
                // callbacks, Process and the session handle alive; do not cache this result.
                return new(false, false, processingTask?.IsCompleted ?? true,
                    [$"SessionStop: {ex.GetType().Name}: {ex.Message}"]) { ControlDenied = true };
            }
            catch (Exception ex)
            {
                failures.Add($"SessionStop: {ex.GetType().Name}: {ex.Message}");
            }

            cleanup.CancelSubscriptions(() => dispatcher?.CancelAll());
            try
            {
                cleanup.StopProcessing(() => source?.StopProcessing());
            }
            catch (Exception ex)
            {
                failures.Add($"StopProcessing: {ex.GetType().Name}: {ex.Message}");
            }

            var sessionDisposeSucceeded = false;
            try
            {
                cleanup.DisposeSession(() =>
                {
                    session.StopOnDispose = false;
                    session.Dispose();
                });
                sessionDisposeSucceeded = true;
            }
            catch (Exception ex)
            {
                failures.Add($"SessionDispose: {ex.GetType().Name}: {ex.Message}");
            }

            var processingCompleted = false;
            var drainStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                processingCompleted = failures.Count == 0 && (processingTask?.Wait(waitTimeout) ?? true);
                if (!processingCompleted)
                {
                    failures.Add(failures.Count > 0
                        ? "ProcessingTask: cleanup failed; drain was not attempted."
                        : $"ProcessingTask: did not exit within {waitTimeout}.");
                }
            }
            catch (Exception ex)
            {
                failures.Add($"ProcessingTask: {ex.GetType().Name}: {ex.Message}");
            }

            // Completion records Process() and observer cleanup. Join the original
            // thread too, using only the remainder of the same drain budget.
            if (failures.Count == 0 && consumerThread is { } thread)
            {
                var unlimited = waitTimeout == Timeout.InfiniteTimeSpan;
                var remaining = unlimited ? Timeout.InfiniteTimeSpan
                    : waitTimeout - System.Diagnostics.Stopwatch.GetElapsedTime(drainStarted);
                var joined = thread.Join(unlimited ? Timeout.InfiniteTimeSpan
                    : remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
                processingCompleted &= joined;
                if (!joined) failures.Add($"ConsumerThread: did not exit within {waitTimeout}.");
            }

            if (processingCompleted) cleanup.ConfirmProcessingJoined();

            var result = new KernelEtwSessionStopResult(
                sessionStopSucceeded,
                sessionDisposeSucceeded,
                processingCompleted,
                failures.ToArray());
            // Only completed release is terminal. A failed explicit Stop may be
            // tried again without repeating the steps already confirmed complete.
            if (result.IsReleased) stopResult = result;
            return result;
        }
    }

    private void StopSession()
    {
        if (keywordControl is not null) keywordControl.StopSession();
        else if (!session.Stop(noThrow: false))
            throw new InvalidOperationException("The ETW controller did not confirm stop.");
        session.StopOnDispose = false;
    }

    internal void JoinProcessing()
    {
        try { processingTask?.GetAwaiter().GetResult(); }
        finally { consumerThread?.Join(); }
    }

    public void Dispose()
    {
        lock (stopGate)
        {
            if (disposed) return;
            var failures = new List<Exception>();
            if (!stopAttempted)
            {
                try { _ = Stop(TimeSpan.FromSeconds(2)); }
                catch (Exception ex) { failures.Add(ex); }
            }

            // Terminal disposal releases this process's consumer even when native
            // session control was denied. Closing its trace handle is safe while
            // Process runs; StopProcessing alone may wait indefinitely for an event.
            // This does not prove that the provider's native session was stopped.
            try
            {
                cleanup.Dispose(
                    StopSession,
                    () => dispatcher?.CancelAll(),
                    () => source?.StopProcessing(),
                    () => source?.Dispose(),
                    () => { session.StopOnDispose = false; session.Dispose(); },
                    JoinProcessing);
            }
            catch (Exception ex) { failures.Add(ex); }
            if (failures.Count > 0)
                throw new AggregateException("The local ETW consumer failed during terminal disposal.", failures);
            disposed = true;
        }
    }
}

