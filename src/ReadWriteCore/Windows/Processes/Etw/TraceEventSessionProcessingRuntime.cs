using Microsoft.Diagnostics.Tracing.Session;
using ResourceManager.ReadWrite.Windows.Processes.Etw;

namespace ResourceManager.App.Infrastructure.Telemetry.Etw;

internal sealed class TraceEventSessionProcessingRuntime(
    TraceEventSession session,
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

    public void StartProcessing(Action<Exception?> completed)
    {
        ArgumentNullException.ThrowIfNull(completed);
        lock (stopGate)
        {
            ObjectDisposedException.ThrowIf(disposed || stopAttempted, this);
            if (Interlocked.CompareExchange(ref processing, 1, 0) != 0)
                throw new InvalidOperationException("ETW 事件处理已经启动。");
            // Source is lazy and may start the native session. Register the
            // consumer under the stop gate before Stop can inspect its lifetime.
            Microsoft.Diagnostics.Tracing.ETWTraceEventSource processingSource;
            try { processingSource = source = session.Source; }
            catch { Volatile.Write(ref processing, 0); throw; }

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
    }

    // PresentFrames owns the dedicated thread outside this runtime. Register its
    // synchronous Process call so Stop can drain and join that same consumer too.
    internal void Process()
    {
        Microsoft.Diagnostics.Tracing.ETWTraceEventSource processingSource;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (stopGate)
        {
            ObjectDisposedException.ThrowIf(disposed || stopAttempted, this);
            if (Interlocked.CompareExchange(ref processing, 1, 0) != 0)
                throw new InvalidOperationException("ETW event processing has already started.");
            try { processingSource = source = session.Source; }
            catch { Volatile.Write(ref processing, 0); throw; }
            processingTask = completion.Task;
            consumerThread = Thread.CurrentThread;
        }
        try { processingSource.Process(); }
        finally
        {
            Volatile.Write(ref processing, 0);
            completion.TrySetResult();
        }
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
            var result = cleanup.Stop(waitTimeout,
                StopSession,
                static () => { },
                WaitForProcessing,
                () => source?.StopProcessing(),
                () => source?.Dispose(),
                () =>
                {
                    session.StopOnDispose = false;
                    session.Dispose();
                });
            // Only completed release is terminal. A failed explicit Stop may be
            // tried again without repeating the steps already confirmed complete.
            if (result.IsReleased) stopResult = result;
            return result;
        }
    }

    private bool WaitForProcessing(TimeSpan waitTimeout)
    {
        var began = System.Diagnostics.Stopwatch.GetTimestamp();
        if (!(processingTask?.Wait(waitTimeout) ?? true)) return false;
        var remaining = waitTimeout == Timeout.InfiniteTimeSpan ? Timeout.InfiniteTimeSpan
            : waitTimeout - System.Diagnostics.Stopwatch.GetElapsedTime(began);
        return consumerThread?.Join(remaining == Timeout.InfiniteTimeSpan ? remaining
            : remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero) ?? true;
    }

    private void StopSession()
    {
        if (!session.Stop(noThrow: false))
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
                try
                {
                    var release = Stop(TimeSpan.FromSeconds(2));
                    if (!release.IsReleased)
                        failures.Add(new InvalidOperationException(
                            "ETW stop remains incomplete: " + string.Join(" | ", release.Failures)));
                }
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
                    static () => { },
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

/// <summary>Each local cleanup step is retained until it returns successfully.</summary>
internal sealed class KernelEtwSessionRuntimeCleanup
{
    private bool sessionStopped;
    private bool subscriptionsCanceled;
    private bool processingStopped;
    private bool sourceDisposed;
    private bool sessionDisposed;
    private bool processingJoined;
    private Exception? subscriptionStopFailure;
    private Exception? processingStopFailure;
    private Exception? sessionDisposeStopFailure;
    private Exception? nativeStopFailure;

    internal KernelEtwSessionStopResult Stop(TimeSpan waitTimeout, Action stopSession,
        Action cancel, Func<TimeSpan, bool> wait, Action stop, Action disposeSource, Action disposeSession)
    {
        var failures = new List<string>();
        try { StopSession(stopSession); }
        catch (Exception error) when (KernelEtwSessionKeywordControl.IsAccessDenied(error))
        {
            // Keep the denied session and its live consumer intact for retry.
            return new(false, false, processingJoined, [$"SessionStop: {error.GetType().Name}: {error.Message}"])
                { ControlDenied = true };
        }
        catch (Exception error) { failures.Add($"SessionStop: {error.GetType().Name}: {error.Message}"); }
        try { CancelSubscriptions(cancel); }
        catch (Exception error) { failures.Add($"CancelSubscriptions: {error.GetType().Name}: {error.Message}"); }

        // ControlTrace STOP flushes final buffers. Do not cancel ProcessTrace or
        // CloseTrace until it has drained and its thread has exited. The wait
        // callback shares one budget between task completion and thread join.
        if (!processingJoined && failures.Count == 0)
        {
            try
            {
                if (wait(waitTimeout)) ConfirmProcessingJoined();
                else failures.Add($"ProcessingTask: natural drain did not exit within {waitTimeout}; trace forcibly closed, release incomplete.");
            }
            catch (Exception error) { failures.Add($"ProcessingTask: {error.GetType().Name}: {error.Message}"); }
        }

        // Timeout/failure still closes the trace to unblock the consumer. It
        // cannot turn this attempt into a confirmed, successful natural drain.
        try { StopProcessing(stop); }
        catch (Exception error) { failures.Add($"StopProcessing: {error.GetType().Name}: {error.Message}"); }
        try { DisposeSource(disposeSource); }
        catch (Exception error) { failures.Add($"SourceDispose: {error.GetType().Name}: {error.Message}"); }
        // TraceEventSession.Dispose also disposes its source. Do not let it
        // implicitly retry a source-close failure in this same explicit Stop.
        if (sourceDisposed)
        {
            try { DisposeSession(disposeSession); }
            catch (Exception error) { failures.Add($"SessionDispose: {error.GetType().Name}: {error.Message}"); }
        }
        return new(sessionStopped, sessionDisposed, processingJoined, failures.ToArray());
    }

    internal void ConfirmProcessingJoined() => processingJoined = true;

    internal void StopSession(Action stop)
    {
        if (sessionStopped) return;
        try { stop(); sessionStopped = true; nativeStopFailure = null; }
        catch (Exception failure) { nativeStopFailure = failure; throw; }
    }

    internal void StopProcessing(Action stop)
    {
        if (processingStopped) return;
        try { stop(); processingStopped = true; processingStopFailure = null; }
        catch (Exception failure) { processingStopFailure = failure; throw; }
    }

    internal void CancelSubscriptions(Action cancel)
    {
        if (subscriptionsCanceled) return;
        try { cancel(); subscriptionsCanceled = true; subscriptionStopFailure = null; }
        catch (Exception failure) { subscriptionStopFailure = failure; throw; }
    }

    internal void DisposeSession(Action dispose)
    {
        if (sessionDisposed) return;
        try { dispose(); sessionDisposed = true; sessionDisposeStopFailure = null; }
        catch (Exception failure) { sessionDisposeStopFailure = failure; throw; }
    }

    private void DisposeSource(Action dispose)
    {
        if (sourceDisposed) return;
        dispose();
        sourceDisposed = true;
    }

    internal void Dispose(Action stopSession, Action cancel, Action stop,
        Action disposeSource, Action disposeSession, Action join)
    {
        var failures = new List<Exception>();
        // Stop and terminal disposal are one release attempt. A step that failed
        // during Stop is retained for the next explicit Dispose, not retried here.
        CompleteAfterStop(ref sessionStopped, ref nativeStopFailure, stopSession, failures);
        CompleteAfterStop(ref subscriptionsCanceled, ref subscriptionStopFailure, cancel, failures);
        CompleteAfterStop(ref processingStopped, ref processingStopFailure, stop, failures);
        // Closing the trace handle remains an unblock attempt even if stopping
        // failed. A failed step keeps this attempt from entering an unbounded join.
        TryComplete(ref sourceDisposed, disposeSource, failures);
        CompleteAfterStop(ref sessionDisposed, ref sessionDisposeStopFailure, disposeSession, failures);
        if (failures.Count == 0) TryComplete(ref processingJoined, join, failures);
        if (failures.Count > 0)
            throw new AggregateException("Kernel ETW local cleanup remains incomplete.", failures);
    }

    private static void CompleteAfterStop(ref bool completed, ref Exception? stopFailure,
        Action action, List<Exception> failures)
    {
        if (stopFailure is not null)
        {
            failures.Add(stopFailure);
            stopFailure = null;
            return;
        }
        TryComplete(ref completed, action, failures);
    }

    private static void TryComplete(ref bool completed, Action action, List<Exception> failures)
    {
        if (completed) return;
        try { action(); completed = true; }
        catch (Exception failure) { failures.Add(failure); }
    }
}
