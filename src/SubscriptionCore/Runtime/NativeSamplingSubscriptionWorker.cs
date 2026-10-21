using ResourceManager.Subscription.Native;

namespace ResourceManager.Subscription;

public enum SubscriptionSampleCompletion { Sampled, Failed, Skipped, Canceled }
public enum SubscriptionWorkerFailureStage { Planning, Demand, Collection, InterruptedSettlement, FaultedSettlement, Cleanup }
public sealed record SubscriptionWorkerFailure(SubscriptionWorkerFailureStage Stage, Exception Error, Exception? OriginalError = null);

/// <summary>The original sampling loop, using the one native-backed tracker.
/// The caller owns the task, cancellation, wake signal and device publication.
/// Demand changes must explicitly signal this worker. It owns no value cache.</summary>
public sealed class NativeSamplingSubscriptionWorker<TRequest, TContext>
{
    // TimeProvider.CreateTimer documents Int32.MaxValue milliseconds as the
    // supported upper bound. Keep each Task.Delay segment within that contract,
    // including when the worker is given a custom TimeProvider.
    private static readonly TimeSpan MaximumTimerDelay = TimeSpan.FromMilliseconds(int.MaxValue);

    private readonly NativeItemSamplingSubscriptionTracker<TRequest, TContext> subscriptions;
    private readonly SemaphoreSlim demandSignal;
    private readonly Action<IReadOnlyList<string>> synchronizeDemand;
    private readonly Action<SubscriptionWorkerFailure> reportFailure;
    private readonly TimeSpan failureRetryDelay;
    private readonly TimeProvider clock;
    private int running;

    public NativeSamplingSubscriptionWorker(NativeItemSamplingSubscriptionTracker<TRequest, TContext> subscriptions,
        SemaphoreSlim demandSignal, Action<IReadOnlyList<string>> synchronizeDemand,
        Action<SubscriptionWorkerFailure> reportFailure, TimeSpan failureRetryDelay, TimeProvider? clock = null)
    {
        this.subscriptions = subscriptions ?? throw new ArgumentNullException(nameof(subscriptions));
        this.demandSignal = demandSignal ?? throw new ArgumentNullException(nameof(demandSignal));
        this.synchronizeDemand = synchronizeDemand ?? throw new ArgumentNullException(nameof(synchronizeDemand));
        this.reportFailure = reportFailure ?? throw new ArgumentNullException(nameof(reportFailure));
        if (failureRetryDelay < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(failureRetryDelay));
        this.failureRetryDelay = failureRetryDelay;
        this.clock = clock ?? TimeProvider.System;
    }

    public void NotifyDemandChanged() => SignalDemand(demandSignal);

    public static void SignalDemand(SemaphoreSlim signal)
    {
        ArgumentNullException.ThrowIfNull(signal);
        try { signal.Release(); }
        catch (SemaphoreFullException) { /* One pending wake covers all observed demand. */ }
        catch (ObjectDisposedException) { }
    }

    public async Task RunAsync(Func<NativeItemSamplingSubscriptionPlan<TRequest, TContext>, CancellationToken,
        Task<SubscriptionSampleCompletion>> collectAndPublish, CancellationToken cancellationToken,
        Action<NativeItemSamplingSubscriptionPlan<TRequest, TContext>>? planCreated = null,
        Action<NativeItemSamplingSubscriptionPlan<TRequest, TContext>, SubscriptionSampleCompletion>? collectionCompleted = null)
    {
        ArgumentNullException.ThrowIfNull(collectAndPublish);
        if (Interlocked.CompareExchange(ref running, 1, 0) != 0)
            throw new InvalidOperationException("The sampling worker is already running.");
        try { await RunLoopAsync(collectAndPublish, cancellationToken, planCreated, collectionCompleted); }
        finally
        {
            try
            {
                try { synchronizeDemand([]); }
                catch (Exception error) { reportFailure(new(SubscriptionWorkerFailureStage.Cleanup, error)); }
            }
            finally { Volatile.Write(ref running, 0); }
        }
    }

    private async Task RunLoopAsync(Func<NativeItemSamplingSubscriptionPlan<TRequest, TContext>, CancellationToken,
        Task<SubscriptionSampleCompletion>> collectAndPublish, CancellationToken cancellationToken,
        Action<NativeItemSamplingSubscriptionPlan<TRequest, TContext>>? planCreated,
        Action<NativeItemSamplingSubscriptionPlan<TRequest, TContext>, SubscriptionSampleCompletion>? collectionCompleted)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            NativeItemSamplingSubscriptionPlan<TRequest, TContext> plan;
            try { plan = subscriptions.CreatePlan(clock.GetUtcNow()); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
            catch (Exception error)
            {
                reportFailure(new(SubscriptionWorkerFailureStage.Planning, error));
                await DelayAfterFailureAsync(cancellationToken);
                continue;
            }

            try
            {
                planCreated?.Invoke(plan);
                synchronizeDemand(plan.ActiveItemIds);
            }
            catch (Exception error)
            {
                var settled = false;
                if (HasPendingWork(plan))
                    settled = TrySettlePlan(plan, SubscriptionWorkerFailureStage.FaultedSettlement, error);
                if (settled && !cancellationToken.IsCancellationRequested)
                {
                    if (HasCollectionRequest(plan))
                        InvokeCollectionCompleted(
                            collectionCompleted,
                            plan,
                            SubscriptionSampleCompletion.Skipped);
                    else
                        InvokeFreshCollectionCompleted(
                            collectionCompleted,
                            plan,
                            SubscriptionSampleCompletion.Skipped);
                }
                reportFailure(new(SubscriptionWorkerFailureStage.Demand, error));
                await DelayAfterFailureAsync(cancellationToken);
                continue;
            }
            if (!plan.IsActive)
            {
                try { await demandSignal.WaitAsync(cancellationToken); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
                continue;
            }
            if (!HasCollectionRequest(plan))
            {
                if (HasPendingWork(plan))
                {
                    try
                    {
                        var completion = subscriptions.CompletePlan(
                            plan,
                            clock.GetUtcNow(),
                            NativeSamplingSubscriptionCompletionStatus.Skipped);
                        if (!cancellationToken.IsCancellationRequested)
                            InvokeFreshCollectionCompleted(
                                collectionCompleted,
                                plan,
                                SubscriptionSampleCompletion.Skipped);
                        await WaitForDemandOrDelayAsync(completion.Delay, cancellationToken);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
                    catch (Exception error)
                    {
                        reportFailure(new(SubscriptionWorkerFailureStage.FaultedSettlement, error));
                        await DelayAfterFailureAsync(cancellationToken);
                    }
                    continue;
                }
                try { await WaitForDemandOrDelayAsync(plan.Delay, cancellationToken); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
                continue;
            }

            var settlementAttempted = false;
            try
            {
                var sampled = await collectAndPublish(plan, cancellationToken);
                var status = ToNativeStatus(sampled);
                settlementAttempted = true;
                var completion = subscriptions.CompletePlan(plan, clock.GetUtcNow(), status);
                if (sampled != SubscriptionSampleCompletion.Canceled)
                    InvokeCollectionCompleted(collectionCompleted, plan, sampled);
                await WaitForDemandOrDelayAsync(completion.Delay, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                if (!settlementAttempted)
                    _ = TrySettlePlan(plan, SubscriptionWorkerFailureStage.InterruptedSettlement, null);
                return;
            }
            catch (Exception error)
            {
                if (!settlementAttempted)
                {
                    if (TrySettlePlan(plan, SubscriptionWorkerFailureStage.FaultedSettlement, error))
                        InvokeCollectionCompleted(collectionCompleted, plan, SubscriptionSampleCompletion.Failed);
                }
                reportFailure(new(SubscriptionWorkerFailureStage.Collection, error));
                await DelayAfterFailureAsync(cancellationToken);
            }
        }
    }

    private bool TrySettlePlan(
        NativeItemSamplingSubscriptionPlan<TRequest, TContext> plan,
        SubscriptionWorkerFailureStage stage,
        Exception? originalError)
    {
        try
        {
            _ = subscriptions.CompletePlan(
                plan,
                clock.GetUtcNow(),
                NativeSamplingSubscriptionCompletionStatus.Skipped);
            return true;
        }
        catch (Exception error)
        {
            reportFailure(new(stage, error, originalError));
            return false;
        }
    }

    private static bool HasPendingWork(NativeItemSamplingSubscriptionPlan<TRequest, TContext> plan)
        => plan.DueItemIds.Count != 0 || !plan.Triggers.IsDefaultOrEmpty;

    private static bool HasCollectionRequest(NativeItemSamplingSubscriptionPlan<TRequest, TContext> plan)
        => plan.DueItemIds.Count != 0 && plan.Request is not null;

    private static NativeSamplingSubscriptionCompletionStatus ToNativeStatus(
        SubscriptionSampleCompletion completion)
        => completion switch
        {
            SubscriptionSampleCompletion.Sampled => NativeSamplingSubscriptionCompletionStatus.Sampled,
            SubscriptionSampleCompletion.Failed => NativeSamplingSubscriptionCompletionStatus.Failed,
            SubscriptionSampleCompletion.Skipped or SubscriptionSampleCompletion.Canceled
                => NativeSamplingSubscriptionCompletionStatus.Skipped,
            _ => throw new InvalidOperationException("Sampling returned an unknown completion.")
        };

    private void InvokeCollectionCompleted(
        Action<NativeItemSamplingSubscriptionPlan<TRequest, TContext>, SubscriptionSampleCompletion>? callback,
        NativeItemSamplingSubscriptionPlan<TRequest, TContext> plan,
        SubscriptionSampleCompletion completion)
    {
        if (callback is null) return;
        try { callback(plan, completion); }
        catch (Exception error)
        {
            reportFailure(new(SubscriptionWorkerFailureStage.Collection, error));
        }
    }

    private void InvokeFreshCollectionCompleted(
        Action<NativeItemSamplingSubscriptionPlan<TRequest, TContext>, SubscriptionSampleCompletion>? callback,
        NativeItemSamplingSubscriptionPlan<TRequest, TContext> plan,
        SubscriptionSampleCompletion completion)
    {
        if (callback is null || !plan.Triggers.Any(static trigger => trigger.IsFresh)) return;
        InvokeCollectionCompleted(callback, plan, completion);
    }

    private async Task DelayAfterFailureAsync(CancellationToken cancellationToken)
    {
        try
        {
            var remaining = failureRetryDelay;
            while (remaining > TimeSpan.Zero)
            {
                var segment = LimitTimerDelay(remaining);
                await WaitForDelaySegmentAsync(segment, false, cancellationToken);
                remaining -= segment;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private async Task WaitForDemandOrDelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        if (delay <= TimeSpan.Zero) return;
        await WaitForDelaySegmentAsync(LimitTimerDelay(delay), true, cancellationToken);
    }

    private static TimeSpan LimitTimerDelay(TimeSpan delay)
        => delay > MaximumTimerDelay ? MaximumTimerDelay : delay;

    private async Task WaitForDelaySegmentAsync(TimeSpan delay, bool interruptOnDemand,
        CancellationToken cancellationToken)
    {
        if (!interruptOnDemand)
        {
            await Task.Delay(delay, clock, cancellationToken);
            return;
        }

        using var delayCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var demand = demandSignal.WaitAsync(delayCancellation.Token);
        var timer = Task.Delay(delay, clock, delayCancellation.Token);
        await Task.WhenAny(demand, timer);
        await delayCancellation.CancelAsync();
        try { await Task.WhenAll(demand, timer); }
        catch (OperationCanceledException) when (delayCancellation.IsCancellationRequested) { }
    }
}
