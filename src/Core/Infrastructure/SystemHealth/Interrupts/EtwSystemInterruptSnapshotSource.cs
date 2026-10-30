using Microsoft.Diagnostics.Tracing.Parsers;
using ResourceManager.App.Application.SystemHealth;
using ResourceManager.App.Domain.SystemHealth;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.Telemetry.Etw;
using ResourceManager.App.Infrastructure.Adaptation;
using System.Threading.Channels;

namespace ResourceManager.App.Infrastructure.SystemHealth.Interrupts;

public sealed partial class EtwSystemInterruptSnapshotSource(
    IKernelEtwSessionBroker kernelEtwBroker,
    HostManagerSamplingSubscriptionOwner subscriptionOwner,
    ILogger<EtwSystemInterruptSnapshotSource> logger) : BackgroundService, ISystemInterruptSnapshotSource
{
    private const KernelTraceEventParser.Keywords DurationKeywords = KernelTraceEventParser.Keywords.Interrupt
        | KernelTraceEventParser.Keywords.DeferedProcedureCalls;

    private const string InterruptItemId = "system-interrupts";
    private const string UnknownModuleName = "未知内核模块";

    private readonly object subscriptionGate = new();
    private readonly object resourceDisposeGate = new();
    private readonly object eventGate = new();
    private readonly Func<DateTimeOffset> readUtcNow = static () => DateTimeOffset.UtcNow;
    private readonly Func<long> readTickMilliseconds = static () => Environment.TickCount64;
    private readonly SemaphoreSlim demandSignal = new(0, 1);
    private InterruptSessionState? currentSession;
    private readonly NativeItemSamplingSubscriptionTracker<InterruptSampleRequest> subscriptions =
        CreateSubscriptions(subscriptionOwner, null);
    private IKernelEtwSubscription? kernelSubscription;
    private bool disposed;
    private bool ownedResourcesDisposed;
    private long nextLeaseId;
    private volatile string state = SystemInterruptProviderStates.Idle;
    private volatile string message = "ETW 系统中断监控按报告需求启动。";
    private SystemInterruptSnapshot latestSnapshot =
        SystemInterruptSnapshot.Unavailable(
            SystemInterruptProviderStates.Idle,
            "ETW 系统中断监控按报告需求启动。");

    private static NativeItemSamplingSubscriptionTracker<InterruptSampleRequest> CreateSubscriptions(
        HostManagerSamplingSubscriptionOwner owner,
        Func<ulong, uint, ResourceManager.Subscription.INativeSamplingOutputScratch>? createOutputScratch)
        => new(owner, roleId: 5, static request => request.SourceKey,
            static _ => [InterruptItemId],
            static (itemIds, _) => itemIds.Contains(InterruptItemId, StringComparer.OrdinalIgnoreCase)
                ? new InterruptSampleRequest("provider") : null,
            coalesceItemsIntoLatestCapture: true, createOutputScratch);

    internal EtwSystemInterruptSnapshotSource(
        IKernelEtwSessionBroker kernelEtwBroker,
        HostManagerSamplingSubscriptionOwner subscriptionOwner,
        ILogger<EtwSystemInterruptSnapshotSource> logger,
        ResourceManagerLedgerOwner ledgerOwner)
        : this(kernelEtwBroker, subscriptionOwner, logger)
    {
        ArgumentNullException.ThrowIfNull(ledgerOwner);
        subscriptions = CreateSubscriptions(subscriptionOwner,
            (length, alignment) => SamplingOutputScratch.Create(ledgerOwner.CreateScratch, length, alignment));
    }

    internal EtwSystemInterruptSnapshotSource(IKernelEtwSessionBroker kernelEtwBroker,
        HostManagerSamplingSubscriptionOwner subscriptionOwner,
        ILogger<EtwSystemInterruptSnapshotSource> logger, Func<DateTimeOffset> readUtcNow,
        Func<long> readTickMilliseconds)
        : this(kernelEtwBroker, subscriptionOwner, logger)
    {
        this.readUtcNow = readUtcNow ?? throw new ArgumentNullException(nameof(readUtcNow));
        this.readTickMilliseconds = readTickMilliseconds ?? throw new ArgumentNullException(nameof(readTickMilliseconds));
    }


    public SystemInterruptSnapshot Read()
        => Volatile.Read(ref latestSnapshot);

    public IDisposable AcquireSubscription(
        string subscriptionId,
        TimeSpan refreshInterval)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subscriptionId);
        var leaseId = Interlocked.Increment(ref nextLeaseId);
        var sourceKey = $"lease:{leaseId}:{subscriptionId.Trim()}";
        lock (subscriptionGate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            subscriptions.TrackPersistent(
                new InterruptSampleRequest(sourceKey),
                DateTimeOffset.UtcNow,
                refreshInterval);
        }
        SignalDemand();
        return new InterruptSubscriptionLease(this, sourceKey);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var worker = new NativeSamplingSubscriptionWorker<InterruptSampleRequest, SamplingOwnerToken>(
            subscriptions, demandSignal,
            static _ => { },
            static failure => System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure.Error).Throw());
        try
        {
            EnsureStarted();
            await worker.RunAsync(async (plan, token) =>
            {
                var sampled = await TryPublishCurrentSnapshotAsync(readUtcNow(), plan.OwnerToken, plan.Schedule.FreshnessGrace, token);
                return !plan.OwnerToken.IsActive ? SubscriptionSampleCompletion.Skipped
                    : sampled ? SubscriptionSampleCompletion.Sampled : SubscriptionSampleCompletion.Failed;
            }, stoppingToken);
        }
        finally { StopSession("ETW 系统中断监控正在随程序停止。", publishStoppedValue: false); }
    }

    public override void Dispose()
    {
        lock (subscriptionGate)
        {
            disposed = true;
        }
        try
        {
            base.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException) when (ExecuteTask?.IsCompleted == true)
        {
        }
        catch (Exception error)
        {
            logger.LogError(error, "ETW system-interrupt worker failed while draining for disposal.");
        }
        lock (resourceDisposeGate)
        {
            if (ownedResourcesDisposed) return;
            if (ExecuteTask is null || ExecuteTask.IsCompleted)
                StopSession("ETW 系统中断监控正在随程序停止。");
            subscriptions.Clear(DateTimeOffset.UtcNow);
            subscriptions.CloseOutputScratch();
            demandSignal.Dispose();
            base.Dispose();
            ownedResourcesDisposed = true;
        }
    }



    private void ReleaseLease(string sourceKey)
    {
        lock (subscriptionGate)
        {
            if (disposed) return;
            subscriptions.Remove(sourceKey, DateTimeOffset.UtcNow);
        }
        SignalDemand();
    }

    private void SignalDemand()
    {
        try
        {
            demandSignal.Release();
        }
        catch (SemaphoreFullException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private bool HasSessionLease
    {
        get
        {
            lock (subscriptionGate)
                return kernelSubscription is not null;
        }
    }

    private void EnsureStarted()
    {
        lock (metadataResourceGate)
        {
            bool needed;
            long epoch;
            lock (subscriptionGate)
            {
                if (disposed) return;
                needed = kernelSubscription is null;
                epoch = needed ? ++metadataEpoch : metadataEpoch;
            }
            if (needed)
            try
            {
                var created = kernelEtwBroker.Acquire(new KernelEtwSubscriptionRequest(
                    "system-interrupts",
                    KernelTraceEventParser.Keywords.ImageLoad,
                    (source, context) =>
                    {
                        lock (subscriptionGate)
                            if (!disposed && epoch == metadataEpoch)
                                BindKernelEvents(source, context);
                    }) { NativeOutputs = KernelEtwNativeOutputs.Interrupts });
                var accepted = false;
                lock (subscriptionGate)
                    if (!disposed && epoch == metadataEpoch) { kernelSubscription = created; accepted = true; }
                if (!accepted) { created.Dispose(); return; }
            }
            catch (Exception ex)
            {
                lock (subscriptionGate)
                    if (disposed || epoch != metadataEpoch) return;
                state = SystemInterruptProviderStates.Unavailable;
                message = $"ETW 系统中断监控启动失败：{ex.Message}";
                PublishUnavailable(state, message);
                logger.LogWarning(ex, "Failed to start ETW system interrupt source.");
            }
            SynchronizeDurationHold();
            RefreshBrokerState();
        }
    }

    private void BindKernelEvents(
        KernelEtwEventSource kernel,
        KernelEtwSessionContext context)
    {
        var nativeSession = kernel.NativeSession ?? throw new InvalidOperationException("Interrupt capture requires a native kernel ETW session.");
        var previous = Volatile.Read(ref currentSession);
        var session = new InterruptSessionState(context.Generation, kernel, nativeSession, readTickMilliseconds());
        if (previous is not null && previous.Generation == context.Generation && ReferenceEquals(previous.Native, nativeSession))
            session.DurationSource = Volatile.Read(ref previous.DurationSource);
        KernelEtwNativeReaders.ConfigureInterruptProcessors(session.Native, Environment.ProcessorCount);
        session.NativeRequiredTicket = KernelEtwNativeReaders.ResetInterruptBinding(session.Native, session.StartedTick);
        DeclareInterruptCounters(kernel);
        kernel.ConfigureAdmission(true, session.StartedTick);
        lock (eventGate)
        {
            Volatile.Write(ref currentSession, session);
            state = SystemInterruptProviderStates.Running;
            message = "ETW 系统中断监控正在采集 ISR/DPC，并解析驱动模块。";
        }
    }

    private void BindDurationEvents(KernelEtwEventSource kernel, KernelEtwSessionContext context)
    {
        var session = Volatile.Read(ref currentSession);
        if (session is null || session.Generation != context.Generation) return;
        if (kernel.NativeSession is null)
            throw new InvalidOperationException("Interrupt duration requires a native kernel ETW session.");
        if (!ReferenceEquals(kernel.NativeSession, session.Native))
            throw new InvalidOperationException("Interrupt duration binding does not belong to its metadata session.");
        Volatile.Write(ref session.DurationSource, kernel);
    }

    private static void DeclareInterruptCounters(KernelEtwEventSource source)
    {
        foreach (var kind in new[] { SystemInterruptEventKinds.Isr, SystemInterruptEventKinds.Dpc,
            SystemInterruptEventKinds.ThreadedDpc, SystemInterruptEventKinds.TimerDpc })
        {
            _ = source.ConsumerCounter(kind);
            foreach (var reason in new[] { "invalid-duration", "inactive-binding", "generation-mismatch", "before-window" })
                _ = source.ConsumerRejectionCounter(kind, reason);
        }
        foreach (var kind in new[] { "ImageLoadOrRundown", "ImageUnload" })
        {
            _ = source.ConsumerCounter(kind);
            foreach (var reason in new[] { "user-mode-image", "inactive-binding", "generation-mismatch", "before-window" })
                _ = source.ConsumerRejectionCounter(kind, reason);
        }
    }

    private bool RefreshBrokerState(KernelEtwSessionSnapshot? observedSnapshot = null, bool publishUnavailable = true)
    {
        var snapshot = (observedSnapshot ?? ReadBrokerSnapshot());
        var session = Volatile.Read(ref currentSession);
        if (snapshot.IsRunning && session is not null && !IsBindingActive(session))
        {
            state = SystemInterruptProviderStates.Unavailable;
            message = "ETW 系统中断绑定已取消，当前没有可发布窗口。";
            if (publishUnavailable) PublishUnavailable(state, message, snapshot.EventsLost);
            return true;
        }
        if (snapshot.IsRunning
            && session is not null
            && snapshot.Generation == session.Generation)
        {
            lock (eventGate)
            {
                if (!ReferenceEquals(session, Volatile.Read(ref currentSession))) return false;
                state = SystemInterruptProviderStates.Running;
                message = "ETW 系统中断监控正在采集 ISR/DPC，并解析驱动模块。";
            }
            return false;
        }

        state = snapshot.State switch
        {
            "Running" => SystemInterruptProviderStates.Running,
            "Idle" => SystemInterruptProviderStates.Idle,
            "Starting" => SystemInterruptProviderStates.Starting,
            "Stopped" => SystemInterruptProviderStates.Stopped,
            "StopFailed" => SystemInterruptProviderStates.StopFailed,
            "Available" => SystemInterruptProviderStates.Available,
            "Unavailable" => SystemInterruptProviderStates.Unavailable,
            _ => throw new InvalidDataException($"Unknown kernel ETW state '{snapshot.State}'.")
        };
        message = snapshot.Message;
        if (publishUnavailable) PublishUnavailable(state, message);
        return true;
    }

    private bool TryPublishCurrentSnapshot(
        DateTimeOffset now,
        SamplingOwnerToken ownerToken)
    {
        lock (subscriptionGate)
        {
            var brokerSnapshot = ReadBrokerSnapshot();
            RefreshBrokerState(brokerSnapshot, publishUnavailable: false);
            var session = Volatile.Read(ref currentSession);
            if (kernelSubscription is null
                || session is null
                || session.Generation <= 0
                || !IsBindingActive(session)
                || !brokerSnapshot.IsRunning
                || brokerSnapshot.Generation != session.Generation)
            {
                _ = ownerToken.TryPublish(() => PublishUnavailable(state, message));
                return false;
            }

            return TryPublishNativeSnapshot(now, session, brokerSnapshot, ownerToken);
        }
    }

    private async Task<bool> TryPublishCurrentSnapshotAsync(DateTimeOffset now, SamplingOwnerToken ownerToken,
        TimeSpan deliveryGrace, CancellationToken cancellationToken)
    {
        var session = Volatile.Read(ref currentSession);
        if (session is null) return TryPublishCurrentSnapshot(now, ownerToken);
        var native = session.Native;
        var notices = Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
        { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, AllowSynchronousContinuations = false });
        using var observation = session.Source.ObservePublications(() => notices.Writer.TryWrite(0));
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var tick = readTickMilliseconds();
        var began = System.Diagnostics.Stopwatch.GetTimestamp();
        ulong observedSequence = 0;
        try
        {
            KernelEtwNativeReaders.RequestRefresh(native, tick);
            while (true)
            {
                if (!ReferenceEquals(session, Volatile.Read(ref currentSession))) return false;
                if (!IsBindingActive(session)) return TryPublishCurrentSnapshot(now, ownerToken);
                if (KernelEtwNativeReaders.ReadInterrupts(native, Volatile.Read(ref session.NativeRequiredTicket), ref observedSequence,
                        static view => view.EndedTick, out long through, out _)
                    && through >= tick) return TryPublishCurrentSnapshot(now, ownerToken);
                var remaining = deliveryGrace - System.Diagnostics.Stopwatch.GetElapsedTime(began);
                if (remaining <= TimeSpan.Zero) throw new TimeoutException("The native interrupt publication exceeded its configured delivery grace.");
                var signal = notices.Reader.ReadAsync(wait.Token).AsTask();
                if (await Task.WhenAny(signal, native.ProcessingCompletion).WaitAsync(remaining, cancellationToken) == native.ProcessingCompletion)
                {
                    _ = ownerToken.TryPublish(() => PublishUnavailable(SystemInterruptProviderStates.Unavailable,
                        native.ProcessingFailure?.Message ?? "Native ETW processing completed before this interrupt window was published."));
                    return false;
                }
                await signal;
            }
        }
        catch (TimeoutException error)
        {
            logger.LogWarning(error, "Native ETW interrupt window publication was not delivered before the sampling deadline.");
            _ = ownerToken.TryPublish(() => PublishUnavailable(SystemInterruptProviderStates.Unavailable, error.Message));
            return false;
        }
        catch (KernelEtwNativeReaders.CaptureFailure error)
        {
            logger.LogWarning(error, "Native ETW interrupt capture failed.");
            state = SystemInterruptProviderStates.Unavailable; message = error.Message;
            session.NativeSnapshot = null;
            _ = ownerToken.TryPublish(() => PublishUnavailable(state, message));
            return false;
        }
        finally { wait.Cancel(); notices.Writer.TryComplete(); }
    }

    private bool TryPublishNativeSnapshot(DateTimeOffset now, InterruptSessionState session,
        KernelEtwSessionSnapshot before, SamplingOwnerToken ownerToken)
    {
        var native = session.Native;
        if (KernelEtwNativeReaders.ReadInterrupts(native, Volatile.Read(ref session.NativeRequiredTicket), ref session.NativeSequence,
                view => CopyNativeSnapshot(native.GetPublicationTimestamp(view.EndedTick), view), out SystemInterruptSnapshot? candidate, out var hasPublication))
            session.NativeSnapshot = candidate;
        else if (!hasPublication) session.NativeSnapshot = null;
        var snapshot = session.NativeSnapshot;
        if (snapshot is null || snapshot.ProviderState.EventsLost != before.EventsLost)
        {
            _ = ownerToken.TryPublish(() => PublishUnavailable(SystemInterruptProviderStates.Warming,
                "ETW 系统中断监控正在形成完整原生窗口。",
                Math.Max(before.EventsLost, snapshot?.ProviderState.EventsLost ?? before.EventsLost)));
            return false;
        }
        var after = ReadBrokerSnapshot();
        if (!after.IsRunning || after.Generation != session.Generation || after.EventsLost != before.EventsLost)
        {
            if (RefreshBrokerState(after, publishUnavailable: false))
                _ = ownerToken.TryPublish(() => PublishUnavailable(state, message));
            if (after.IsRunning && after.Generation == session.Generation && after.EventsLost != before.EventsLost)
            {
                session.NativeSnapshot = null;
                _ = ownerToken.TryPublish(() => PublishUnavailable(SystemInterruptProviderStates.Warming,
                    "ETW 系统中断监控正在形成完整原生窗口。", after.EventsLost));
            }
            return false;
        }
        lock (eventGate)
            return ReferenceEquals(session, Volatile.Read(ref currentSession))
                && IsBindingActive(session)
                && ownerToken.TryPublish(() => Volatile.Write(ref latestSnapshot, snapshot));
    }

    private static bool IsBindingActive(InterruptSessionState session)
        => session.Source.IsActive && Volatile.Read(ref session.DurationSource) is { IsActive: true };

    internal static unsafe SystemInterruptSnapshot CopyNativeSnapshot(DateTimeOffset now, KernelEtwNativeReaders.InterruptView view)
    {
        if (view.DriverCount != 0 && view.Drivers == 0 || view.BucketCount != 0 && view.Buckets == 0)
            throw new InvalidDataException("Native interrupt publication rows are absent.");
        var drivers = new SystemInterruptDriverSnapshot[checked((int)view.DriverCount)];
        for (var i = 0; i < drivers.Length; i++)
        {
            var row = ((KernelEtwNativeReaders.InterruptDriver*)view.Drivers)[i];
            drivers[i] = new(KernelEtwNativeReaders.Utf16(row.Module.Name, row.Module.NameLength) ?? UnknownModuleName,
                KernelEtwNativeReaders.Utf16(row.Module.Path, row.Module.PathLength), row.TotalDuration, row.MaximumDuration,
                checked((int)row.EventCount), checked((int)row.LongCount), row.CapacityPercent);
        }
        var buckets = new SystemInterruptBucketSnapshot[checked((int)view.BucketCount)];
        for (var i = 0; i < buckets.Length; i++)
        {
            var row = ((KernelEtwNativeReaders.InterruptBucket*)view.Buckets)[i];
            buckets[i] = new(now.AddMilliseconds(row.StartedTick - view.EndedTick), now.AddMilliseconds(row.EndedTick - view.EndedTick),
                row.TotalDuration, row.CapacityPercent, checked((int)row.EventCount));
        }
        return new(now, (view.Flags & 1) != 0, checked((long)view.Generation), TimeSpan.FromMilliseconds(view.WindowMilliseconds),
            checked((int)view.ProcessorCount), view.TotalDuration, view.MaximumDuration, view.MaximumKind switch
            {
                0 => null, 1 => SystemInterruptEventKinds.Isr, 2 => SystemInterruptEventKinds.Dpc,
                3 => SystemInterruptEventKinds.ThreadedDpc, 4 => SystemInterruptEventKinds.TimerDpc,
                _ => throw new InvalidDataException($"Unknown native interrupt event kind '{view.MaximumKind}'.")
            }, view.EventCount == 0 ? null : KernelEtwNativeReaders.Utf16(view.MaximumModule.Name, view.MaximumModule.NameLength) ?? UnknownModuleName,
            checked((int)view.EventCount),
            checked((int)view.LongCount), view.CapacityPercent, buckets, drivers,
            new("etw-system-interrupts", (view.Flags & 1) != 0 ? SystemInterruptProviderStates.Running : SystemInterruptProviderStates.Warming,
                (view.Flags & 1) != 0 ? "ETW 系统中断监控正在采集 ISR/DPC，并解析驱动模块。"
                    : "ETW 系统中断监控正在收集第一个完整 10 秒桶。", checked((int)view.EventsLost)));
    }

    private void PublishUnavailable(string snapshotState, string snapshotMessage, int eventsLost = 0)
    {
        var empty = SystemInterruptSnapshot.Unavailable(snapshotState, snapshotMessage);
        if (eventsLost != 0)
            empty = empty with { ProviderState = empty.ProviderState with { EventsLost = eventsLost } };
        Volatile.Write(ref latestSnapshot, empty);
    }

    private void StopSession(string idleMessage, string idleState = SystemInterruptProviderStates.Idle, bool publishStoppedValue = true)
    {
        IKernelEtwSubscription? subscriptionToRelease;
        IKernelEtwSubscription? durationToRelease;
        lock (subscriptionGate)
        {
            ++metadataEpoch;
            ++durationEpoch;
            acquiringDuration = false;
            subscriptionToRelease = kernelSubscription;
            kernelSubscription = null;
            durationToRelease = durationSubscription;
            durationSubscription = null;
            lock (eventGate)
            {
                Volatile.Write(ref currentSession, null);
            }
        }

        try { durationToRelease?.Dispose(); } finally { subscriptionToRelease?.Dispose(); }
        lock (subscriptionGate)
        {
            if (kernelSubscription is not null) return;
            lock (eventGate)
            {
                state = idleState;
                message = idleMessage;
                if (publishStoppedValue) PublishUnavailable(state, message);
            }
        }
    }

    private sealed class InterruptSessionState(long generation, KernelEtwEventSource source, NativeKernelEtwSession native, long startedTick)
    {
        public long Generation { get; } = generation;
        public KernelEtwEventSource Source { get; } = source;
        public NativeKernelEtwSession Native { get; } = native;
        public long StartedTick = startedTick;
        public KernelEtwEventSource? DurationSource;
        public ulong NativeSequence;
        public ulong NativeRequiredTicket;
        public SystemInterruptSnapshot? NativeSnapshot;
    }

    private sealed record InterruptSampleRequest(string SourceKey);

    private sealed class InterruptSubscriptionLease(
        EtwSystemInterruptSnapshotSource owner,
        string sourceKey) : IDisposable
    {
        private int disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
            {
                owner.ReleaseLease(sourceKey);
            }
        }
    }
}
