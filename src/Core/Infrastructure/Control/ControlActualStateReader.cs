using ResourceManager.App.Application.Control;
using ResourceManager.App.Domain.Control;
using ResourceManager.App.Infrastructure.Control.Writers;

namespace ResourceManager.App.Infrastructure.Control;

/// <summary>
/// 实际值和期望值各有所有者。父决策节点按本源的“被用”事实启停本块。
/// 入口只做一次风扇发现；订阅首轮立即到期，以后按各自间隔到期，单次请求立即安排一轮。
/// 没有到期工作时只等待下一次到期或新工作，不采样。单次请求结束后的停止缓冲只保活。
/// 每轮投影已发布的目录并回读写入器，未就绪的能力发布带原因的空值，不等就绪。
/// 新结果完整生成后原子替换，停止后保留当前值。
/// </summary>
public sealed class ControlActualStateReader(
    IControlObjectCatalog catalog,
    IEnumerable<IControlWriter> writers,
    ILogger<ControlActualStateReader>? logger = null,
    HardwareBridgeClient? bridge = null)
    : BackgroundService, IControlActualStateOwner
{
    private readonly IReadOnlyList<IControlWriter> writers = writers.ToArray();
    private readonly TimeProvider clock = TimeProvider.System;
    private readonly TimeSpan stopBuffer = SubscriptionSourceSettings.ProductStopBuffer;
    private readonly object subscriptionGate = new();
    private readonly HashSet<SubscriptionLease> subscriptions = [];
    private readonly HashSet<SingleRequest> requests = [];
    private readonly SemaphoreSlim work = new(0, 1);
    private IDisposable? bridgeReadings;
    // The resident subscription owner keeps this deadline across worker restarts,
    // just as it keeps live leases. Only expiry or owner disposal withdraws it.
    private ITimer? bufferTimer;
    private DateTimeOffset? bufferUntil;
    private bool actualControlValuesInUse;
    private bool fanReadingsInUse;
    private int disposed;
    private volatile ControlActualState current = ControlActualState.Empty;

    internal ControlActualStateReader(IControlObjectCatalog catalog, IEnumerable<IControlWriter> writers,
        TimeProvider clock, HardwareBridgeClient? bridge = null, TimeSpan? stopBuffer = null)
        : this(catalog, writers, bridge: bridge)
    {
        this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
        if (stopBuffer < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(stopBuffer));
        this.stopBuffer = stopBuffer ?? this.stopBuffer;
    }

    public ControlActualState Current => current;
    internal bool ActualControlValuesInUse => Volatile.Read(ref actualControlValuesInUse);
    internal event Action? SubscriptionsChanged;
    internal bool FanReadingsInUse => Volatile.Read(ref fanReadingsInUse);
    internal event Action? FanReadingsChanged;

    public async Task<ControlActualState?> FetchOnceAsync(string holder, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(holder);
        cancellationToken.ThrowIfCancellationRequested();
        var request = new SingleRequest();
        (bool Usage, bool Fan) changes;
        lock (subscriptionGate)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            requests.Add(request);
            changes = RefreshUsageLocked();
            WakeLocked();
        }
        PublishChanges(changes);
        logger?.LogDebug("Control actual-state request {Holder} acquired.", holder);
        try { return await request.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false); }
        finally
        {
            lock (subscriptionGate)
            {
                requests.Remove(request);
                if (Volatile.Read(ref disposed) == 0 && stopBuffer > TimeSpan.Zero)
                {
                    bufferUntil = clock.GetUtcNow() + stopBuffer;
                    bufferTimer ??= clock.CreateTimer(_ => ExpireBuffer(), null,
                        Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                    bufferTimer.Change(stopBuffer, Timeout.InfiniteTimeSpan);
                }
                changes = RefreshUsageLocked();
            }
            PublishChanges(changes);
            logger?.LogDebug("Control actual-state request {Holder} released.", holder);
        }
    }

    public IDisposable AcquireSubscription(TimeSpan interval)
    {
        if (interval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(interval));
        SubscriptionLease lease;
        (bool Usage, bool Fan) changes;
        lock (subscriptionGate)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            lease = new(this, interval, clock.GetUtcNow());
            subscriptions.Add(lease);
            changes = RefreshUsageLocked();
            WakeLocked();
        }
        PublishChanges(changes);
        return lease;
    }

    private void ReleaseSubscription(SubscriptionLease lease)
    {
        (bool Usage, bool Fan) changes;
        lock (subscriptionGate)
        {
            if (!subscriptions.Remove(lease)) return;
            changes = RefreshUsageLocked();
            WakeLocked();
        }
        PublishChanges(changes);
    }

    private void ExpireBuffer()
    {
        (bool Usage, bool Fan) changes;
        lock (subscriptionGate)
        {
            if (bufferUntil is not { } until || Volatile.Read(ref disposed) != 0) return;
            var remaining = until - clock.GetUtcNow();
            if (remaining > TimeSpan.Zero)
            {
                bufferTimer?.Change(remaining, Timeout.InfiniteTimeSpan);
                return;
            }
            bufferUntil = null;
            changes = RefreshUsageLocked();
        }
        PublishChanges(changes);
    }

    private (bool Usage, bool Fan) RefreshUsageLocked()
    {
        var next = subscriptions.Count != 0 || requests.Count != 0 || bufferUntil is not null;
        var changed = actualControlValuesInUse != next;
        Volatile.Write(ref actualControlValuesInUse, next);
        return (changed, RefreshReadingSubscriptionsLocked());
    }

    // event: only subscription/request edges, buffer expiry and declaration reads refresh holds.
    private bool RefreshReadingSubscriptionsLocked()
    {
        if (actualControlValuesInUse && catalog.ActualReadingsUseHardwareBridge)
            bridgeReadings ??= bridge?.SubscribeReadings();
        else
        {
            bridgeReadings?.Dispose();
            bridgeReadings = null;
        }
        var next = actualControlValuesInUse && catalog.ActualReadingsUseFanCore;
        if (fanReadingsInUse == next) return false;
        Volatile.Write(ref fanReadingsInUse, next);
        return true;
    }

    private void PublishChanges((bool Usage, bool Fan) changes)
    {
        if (changes.Usage) SubscriptionsChanged?.Invoke();
        if (changes.Fan) FanReadingsChanged?.Invoke();
    }

    private void WakeLocked()
    {
        if (Volatile.Read(ref disposed) != 0) return;
        if (work.CurrentCount == 0) work.Release();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        lock (subscriptionGate)
        {
            foreach (var subscription in subscriptions) subscription.NextDue = clock.GetUtcNow();
        }
        try
        {
            try { await catalog.DiscoverActualReadingsAsync(stoppingToken).ConfigureAwait(false); }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                logger?.LogWarning(error, "Actual-state fan discovery failed; reading declared capabilities.");
            }
            while (!stoppingToken.IsCancellationRequested)
            {
                SingleRequest[] batch;
                bool due;
                DateTimeOffset? nextDue;
                lock (subscriptionGate)
                {
                    var now = clock.GetUtcNow();
                    batch = requests.Where(request => !request.Assigned && !request.Completion.Task.IsCompleted).ToArray();
                    due = subscriptions.Any(subscription => subscription.NextDue <= now);
                    if (due || batch.Length != 0)
                    {
                        foreach (var request in batch) request.Assigned = true;
                        foreach (var subscription in subscriptions.Where(subscription => subscription.NextDue <= now))
                            subscription.NextDue = now + subscription.Interval;
                    }
                    nextDue = subscriptions.Count == 0 ? null : subscriptions.Min(subscription => subscription.NextDue);
                }
                if (!due && batch.Length == 0)
                {
                    await WaitForWorkAsync(nextDue, stoppingToken).ConfigureAwait(false);
                    continue;
                }
                // Requests admitted after this point belong to the next round.
                var next = await ReadOnceAsync(stoppingToken).ConfigureAwait(false);
                lock (subscriptionGate)
                {
                    stoppingToken.ThrowIfCancellationRequested();
                    current = next;
                    foreach (var request in batch) request.Completion.TrySetResult(next);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            lock (subscriptionGate)
            {
                foreach (var request in requests) request.Assigned = false;
            }
        }
    }

    private async Task WaitForWorkAsync(DateTimeOffset? due, CancellationToken stop)
    {
        if (due is null)
        {
            await work.WaitAsync(stop).ConfigureAwait(false);
            return;
        }
        var delay = due.Value - clock.GetUtcNow();
        if (delay <= TimeSpan.Zero) return;
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(stop);
        var signal = work.WaitAsync(wait.Token);
        var deadline = Task.Delay(delay, clock, wait.Token);
        try { await await Task.WhenAny(signal, deadline).ConfigureAwait(false); }
        finally
        {
            wait.Cancel();
            try { await Task.WhenAll(signal, deadline).ConfigureAwait(false); }
            catch (OperationCanceledException) when (wait.IsCancellationRequested) { }
        }
    }

    public override void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        ITimer? expiry;
        lock (subscriptionGate)
        {
            expiry = bufferTimer;
            bufferTimer = null;
            bufferUntil = null;
        }
        try
        {
            StopAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
        finally
        {
            if (expiry is not null) expiry.DisposeAsync().AsTask().GetAwaiter().GetResult();
            (bool Usage, bool Fan) changes;
            lock (subscriptionGate)
            {
                subscriptions.Clear();
                foreach (var request in requests)
                    request.Completion.TrySetException(new ObjectDisposedException(nameof(ControlActualStateReader)));
                requests.Clear();
                changes = RefreshUsageLocked();
            }
            PublishChanges(changes);
            work.Dispose();
            base.Dispose();
        }
    }

    private sealed class SingleRequest
    {
        internal readonly TaskCompletionSource<ControlActualState?> Completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Assigned;
    }

    private sealed class SubscriptionLease(ControlActualStateReader owner, TimeSpan interval,
        DateTimeOffset nextDue) : IDisposable
    {
        private ControlActualStateReader? current = owner;
        internal TimeSpan Interval { get; } = interval;
        internal DateTimeOffset NextDue { get; set; } = nextDue;
        public void Dispose() => Interlocked.Exchange(ref current, null)?.ReleaseSubscription(this);
    }

    private async Task<ControlActualState> ReadOnceAsync(CancellationToken cancellationToken)
    {
        var values = new List<ControlActualValue>();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await catalog.EnsureActualTopologyAsync(cancellationToken).ConfigureAwait(false);
            // live: project already-published topology and optional current observations.
            var declared = catalog.ReadObjects();
            bool fanChanged;
            lock (subscriptionGate) fanChanged = RefreshReadingSubscriptionsLocked();
            if (fanChanged) FanReadingsChanged?.Invoke();
            foreach (var controlObject in declared.Objects)
            {
                foreach (var capability in controlObject.Capabilities)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    values.Add(await ReadOneAsync(controlObject, capability, cancellationToken).ConfigureAwait(false));
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            // 采样失败不该让这条后台路断掉 —— 下一轮还有机会。
            // 这一轮读到多少算多少，不拿旧值假装是新的。
            logger?.LogWarning(error, "读实际状态时出错，这一轮只取到 {Count} 项。", values.Count);
        }
        return new ControlActualState(values, clock.GetUtcNow());
    }

    private async Task<ControlActualValue> ReadOneAsync(
        ControlObject controlObject,
        ControlCapability capability,
        CancellationToken cancellationToken)
    {
        try
        {
            foreach (var writer in writers)
            {
                var availability = writer.Probe(controlObject, capability);
                if (!availability.IsMine) continue;
                // Read-only thresholds still have actual values. Only skip the hardware
                // call when the owning writer explicitly says it cannot read this capability.
                if (!availability.IsReadable) return Unreadable(availability.Reason);
                return await writer.ReadAsync(controlObject, capability, cancellationToken).ConfigureAwait(false)
                    ?? Unreadable();
            }
            return Unreadable(capability.UnavailableReason);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            logger?.LogWarning(error, "Actual-state read failed: {Object} / {Capability}.", controlObject.Id, capability.Id);
            return Unreadable();
        }

        ControlActualValue Unreadable(ControlApplyReason? reason = null) => new(controlObject.Id, capability.Id,
            Unit: capability.Range?.Unit, UnreadableReason: reason ?? new("control-setting-read-unavailable"));
    }

}
