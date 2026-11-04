using System.Diagnostics;
using Microsoft.Diagnostics.Tracing.Parsers;

namespace ResourceManager.App.Infrastructure.Telemetry.Etw;

internal interface IKernelEtwNativeRuntimeOperations : IDisposable
{
    int EventsLost { get; }
    void Process();
    void StopProvider();
    void UnblockProcessing();
    void CloseTrace();
    void UpdateKeywords(KernelTraceEventParser.Keywords keywords);
}

internal interface IKernelEtwNativeReadDrain
{
    bool WaitForReaders(TimeSpan timeout);
}

/// <summary>Native child-state use is the union of actual output holds.</summary>
internal interface IKernelEtwNativeOutputControl
{
    void UpdateOutputs(KernelEtwNativeOutputs outputs);
}

internal interface IKernelEtwAllocationDiagnostics
{
    KernelEtwAllocatorUsage? GetAllocationUsage();
}

internal interface IKernelEtwNativeProcessingCompletion
{
    void OnProcessingCompleted(Exception? failure);
}

/// <summary>The native event flow owns state; this shell owns its observed thread and release order.</summary>
internal sealed class NativeKernelEtwSessionRuntime(
    string sessionName,
    IKernelEtwNativeRuntimeOperations session,
    KernelEtwSubscriptionDispatcher? dispatcher = null,
    IKernelEtwConsumerThreadObserver? observer = null) : IKernelEtwSessionRuntime, IKernelEtwAllocationDiagnostics
{
    private readonly object lifetimeGate = new();
    private Task? processingTask;
    private Thread? consumerThread;
    private bool started;
    private int processing;
    private bool providerStopped;
    private bool bindingsCanceled;
    private bool processingUnblocked;
    private bool forceCloseRequired;
    private bool processingJoined;
    private bool traceClosed;
    private bool disposed;

    public bool IsProcessing => Volatile.Read(ref processing) != 0;
    public int EventsLost => disposed ? 0 : session.EventsLost;
    public KernelEtwAllocatorUsage? GetAllocationUsage()
        => (session as NativeKernelEtwSession)?.AllocationUsage;

    public void StartProcessing(Action<Exception?> completed)
    {
        ArgumentNullException.ThrowIfNull(completed);
        lock (lifetimeGate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (started || providerStopped) throw new InvalidOperationException("The native kernel consumer cannot be started twice.");
            var completion = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
            processingTask = completion.Task.ContinueWith(task => completed(task.Result),
                CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
            consumerThread = new Thread(() =>
            {
                Exception? failure = null;
                EtwEventDropSession? eventDrops = null;
                try
                {
                    eventDrops = dispatcher?.RegisterDiagnostics(sessionName, () => session.EventsLost);
                    using (observer?.RegisterCurrentThread(sessionName)) session.Process();
                }
                catch (Exception error)
                {
                    failure = error;
                    try { observer?.ReportFailure(error); }
                    catch (Exception reportError) { failure = new AggregateException(error, reportError); }
                }
                finally
                {
                    eventDrops?.Complete();
                    if (session is IKernelEtwNativeProcessingCompletion completionTarget)
                        completionTarget.OnProcessingCompleted(failure);
                    Volatile.Write(ref processing, 0);
                    completion.TrySetResult(failure);
                }
            }) { IsBackground = true, Name = sessionName };
            started = true;
            Volatile.Write(ref processing, 1);
            try { consumerThread.Start(); }
            catch
            {
                Volatile.Write(ref processing, 0);
                processingTask = null;
                consumerThread = null;
                throw;
            }
        }
    }

    public void UpdateSubscriptions(IReadOnlyList<KernelEtwSubscriptionRegistration> subscriptions, KernelEtwSessionContext context)
    {
        lock (lifetimeGate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (providerStopped) throw new InvalidOperationException("The native provider is stopping.");
            ValidateSubscriptions(subscriptions);
            if (dispatcher is null) throw new InvalidOperationException("No native publications were configured.");
            dispatcher.UpdateSubscriptions(subscriptions, context, session.UpdateKeywords);
        }
    }

    internal static void ValidateSubscriptions(IReadOnlyList<KernelEtwSubscriptionRegistration> subscriptions)
    {
        foreach (var holder in subscriptions) ValidateNativeOutputs(holder.Id, holder.NativeOutputs);
    }

    internal static void ValidateNativeOutputs(string id, KernelEtwNativeOutputs outputs)
    {
        const KernelEtwNativeOutputs supported = KernelEtwNativeOutputs.Residency | KernelEtwNativeOutputs.Interrupts
            | KernelEtwNativeOutputs.Disk | KernelEtwNativeOutputs.Network | KernelEtwNativeOutputs.File
            | KernelEtwNativeOutputs.Renderer | KernelEtwNativeOutputs.Notifications;
        if (outputs == KernelEtwNativeOutputs.None)
            throw new NotSupportedException($"Kernel ETW holder '{id}' has no native output binding.");
        if ((outputs & ~supported) != 0)
            throw new NotSupportedException($"Kernel ETW holder '{id}' requests unsupported native outputs ({outputs}).");
    }

    public KernelEtwSessionStopResult Stop(TimeSpan waitTimeout)
    {
        if (waitTimeout < TimeSpan.Zero && waitTimeout != Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(waitTimeout));
        lock (lifetimeGate)
        {
            if (disposed) return KernelEtwSessionStopResult.Released;
            var failures = new List<string>();
            // No later step can prove release if the provider has not stopped.
            // Keep every successful step so explicit retries do not repeat it.
            if (!providerStopped)
            {
                try { session.StopProvider(); providerStopped = true; }
                catch (Exception failure)
                {
                    return new(false, false, processingJoined,
                        [$"SessionStop: {failure.GetType().Name}: {failure.Message}"])
                    { ControlDenied = KernelEtwSessionKeywordControl.IsAccessDenied(failure) };
                }
            }
            if (!bindingsCanceled)
            {
                try { dispatcher?.CancelAll(); bindingsCanceled = true; }
                catch (Exception failure) { failures.Add($"CancelBindings: {failure.Message}"); }
            }
            // An earlier failed force-close remains an obligation. Do not join
            // until that failed operation has been explicitly retried.
            if (forceCloseRequired && !processingUnblocked)
            {
                try { session.UnblockProcessing(); processingUnblocked = true; }
                catch (Exception failure)
                {
                    failures.Add($"StopProcessing: {failure.Message}");
                    return new(true, false, processingJoined, failures);
                }
            }
            var began = Stopwatch.GetTimestamp();
            // ControlTrace STOP delivers the final buffers. Drain them naturally
            // before calling CloseTrace or canceling ProcessTrace. Task and thread
            // share one budget; a failed stop/cancel/force-close never joins.
            if (!processingJoined && failures.Count == 0)
            {
                try
                {
                    processingJoined = WaitForProcessing(waitTimeout);
                    if (!processingJoined)
                        failures.Add($"ProcessingTask: natural drain did not exit within {waitTimeout}; trace force-close requested, release incomplete.");
                }
                catch (Exception failure) { failures.Add($"ProcessingTask: {failure.Message}"); }
            }
            if (failures.Count > 0)
            {
                forceCloseRequired = true;
                if (!processingUnblocked)
                {
                    try { session.UnblockProcessing(); processingUnblocked = true; }
                    catch (Exception failure) { failures.Add($"StopProcessing: {failure.Message}"); }
                }
                // For real-time input UnblockProcessing forces CloseTrace; ETL
                // cancellation must return at a buffer boundary before its close.
                // Never free callbacks/native state or mark this timed-out drain
                // successful merely because cancellation has now been requested.
                return new(true, false, processingJoined, failures);
            }
            if (!traceClosed)
            {
                try { session.CloseTrace(); traceClosed = true; }
                catch (Exception failure) { return new(true, false, true, [$"CloseTrace: {failure.Message}"]); }
            }
            var readBudget = waitTimeout == Timeout.InfiniteTimeSpan ? Timeout.InfiniteTimeSpan
                : waitTimeout - Stopwatch.GetElapsedTime(began);
            if (session is IKernelEtwNativeReadDrain readDrain
                && !readDrain.WaitForReaders(readBudget > TimeSpan.Zero || readBudget == Timeout.InfiniteTimeSpan
                    ? readBudget : TimeSpan.Zero))
                return new(true, false, true, [$"NativeStateRelease: published read references did not return within {waitTimeout}."]);
            try { session.Dispose(); disposed = true; dispatcher?.ConfirmNativeRelease(); }
            catch (Exception failure) { return new(true, false, true, [$"NativeStateRelease: {failure.Message}"]); }
            return failures.Count == 0 ? KernelEtwSessionStopResult.Released
                : new(true, true, true, failures);
        }
    }

    private bool WaitForProcessing(TimeSpan timeout)
    {
        var began = Stopwatch.GetTimestamp();
        bool completed;
        try { completed = processingTask?.Wait(timeout) ?? true; }
        catch (AggregateException failure)
        {
            if (processingTask?.IsCompleted != true) throw;
            completed = true;
            observer?.ReportFailure(failure.GetBaseException());
        }
        var remaining = timeout == Timeout.InfiniteTimeSpan ? timeout
            : timeout - Stopwatch.GetElapsedTime(began);
        return completed && (consumerThread?.Join(remaining == Timeout.InfiniteTimeSpan
            ? remaining : remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero) ?? true);
    }

    public void Dispose()
    {
        var result = Stop(TimeSpan.FromSeconds(2));
        if (!result.IsReleased)
            throw new InvalidOperationException($"Native kernel ETW cleanup remains incomplete: {string.Join(" | ", result.Failures)}");
    }
}
