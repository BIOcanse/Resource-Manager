using ResourceManager.ReadWrite.Windows.Processes;
using System.Diagnostics;
using System.Globalization;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using ResourceManager.App.Domain.CpuTopology;
using ResourceManager.App.Infrastructure.CpuTopology;
using ResourceManager.App.Infrastructure.Telemetry.Etw;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite.Publications;
using ResourceManager.ReadWrite.Windows.Devices;
namespace Resource_Manager_APP.Tests.Oracles.KernelEtw;
#pragma warning disable CS0618
internal sealed record OracleCpuResidencyOwnerDiagnostics(string State, string Message, long BindingEpoch,
    long? SessionGeneration, CpuResidencyCaptureConfiguration? SessionPlan, CpuResidencyAggregationDiagnostics? Aggregation);
/// <summary>The original shared-broker session, closed-window buffer and published raw current.
/// No worker or global broker. EnsureStarted/Collect/Stop are explicit; borrowed broker is never disposed.</summary>
internal sealed partial class OracleCpuResidencyCaptureOwner : IDisposable
{
    private readonly OracleCpuThreadDiagnosticOwner threadDiagnostics = new();
    private void BindDiagnosticSource(OracleKernelEtwEventSource source, long generation, Func<bool> current)
        => threadDiagnostics.BindDiagnosticSource(source, generation, current);
    private void ClearDiagnosticSource() => threadDiagnostics.ClearDiagnosticSource();
    private void DisposeDiagnosticRequests() => threadDiagnostics.Dispose();
    private const KernelTraceEventParser.Keywords MetadataKeywords = KernelTraceEventParser.Keywords.Process | KernelTraceEventParser.Keywords.Thread;

    private readonly IOracleKernelEtwSessionBroker kernelEtwBroker;
    private readonly Func<CpuResidencyCaptureConfiguration> readConfiguration;
    private readonly KernelEtwDiagnostics logger;
    private readonly Action<OracleCpuResidencyAggregationBuffer.InventorySeed, Func<bool>> seedProcessInventory;
    private readonly Action? published;
    private readonly Func<int, OracleCaptureBucketMemory>? allocateBuckets;
    private readonly object subscriptionGate = new(), snapshotGate = new();
    public SamplingPublicationSignal Publications { get; } = new();
    private IOracleKernelEtwSubscription? kernelSubscription;
    private ResidencySessionState? activeSession;
    private readonly HashSet<OracleCpuResidencyAggregationBuffer> retiredBuffers = [];
    private long bindingEpoch;
    private long revision;
    public long Revision => Interlocked.Read(ref revision);
    private CpuCoreResidencyObservation currentObservation = new CpuCoreResidencyUnavailable(CpuCoreResidencyUnavailableReasons.NotRequested);
    private CpuCoreResidencySnapshot? cachedSnapshot => currentObservation as CpuCoreResidencySnapshot;
    private bool stopped;
    private volatile string state = "Idle", message = "ETW CPU execution-time attribution starts on explicit demand.";
    public string State => state;
    public string Message => message;
    public OracleCpuResidencyCaptureOwner(IOracleKernelEtwSessionBroker broker,
        Func<CpuResidencyCaptureConfiguration> configuration, KernelEtwDiagnostics? diagnostics = null)
        : this(broker, configuration, diagnostics ?? new(), SeedExistingProcesses) { }
    internal OracleCpuResidencyCaptureOwner(IOracleKernelEtwSessionBroker broker, Func<CpuResidencyCaptureConfiguration> configuration,
        KernelEtwDiagnostics diagnostics, Action<OracleCpuResidencyAggregationBuffer.InventorySeed, Func<bool>> seed, Action? published = null, Func<int, OracleCaptureBucketMemory>? allocateBuckets = null)
    {
        ArgumentNullException.ThrowIfNull(broker); ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(diagnostics); ArgumentNullException.ThrowIfNull(seed);
        kernelEtwBroker = broker; readConfiguration = configuration; logger = diagnostics;
        seedProcessInventory = seed; this.published = published; this.allocateBuckets = allocateBuckets;
    }
    public CpuCoreResidencySnapshot? Read()
    {
        lock (snapshotGate)
        {
            return cachedSnapshot;
        }
    }

    public CpuCoreResidencyObservation ReadObservation()
    {
        lock (snapshotGate) return currentObservation;
    }

    internal OracleCpuResidencyOwnerDiagnostics GetDiagnostics()
    {
        lock (subscriptionGate)
        {
            var session = Volatile.Read(ref activeSession);
            return new OracleCpuResidencyOwnerDiagnostics(
                state,
                message,
                bindingEpoch,
                session?.Generation,
                session?.Plan,
                session?.ManagedBuffer?.GetDiagnostics());
        }
    }

    public IReadOnlyList<CpuLogicalProcessorResidencyScope> ReadScopes()
    {
        lock (snapshotGate)
            if (cachedSnapshot is not null) return cachedSnapshot.LogicalProcessorScopes;
        var configuration = Volatile.Read(ref activeSession)?.Plan;
        return configuration is null ? [] : CreateScopes(configuration);
    }
    private static IReadOnlyList<CpuLogicalProcessorResidencyScope> CreateScopes(CpuResidencyCaptureConfiguration configuration)
        => configuration.PhysicalCoreByLogicalProcessor.OrderBy(pair => pair.Key)
            .Select(pair => new CpuLogicalProcessorResidencyScope(pair.Key, pair.Value.PhysicalCoreId,
                string.IsNullOrEmpty(pair.Value.CcdId) ? null : pair.Value.CcdId, configuration.MappingProvenance)).ToArray();

    private bool IsRunning
    {
        get
        {
            var session = Volatile.Read(ref activeSession);
            if (session is null)
            {
                return false;
            }

            var broker = ReadBrokerSnapshot();
            return broker.IsRunning
                && broker.Generation == session.Generation
                && broker.EventsLost == Volatile.Read(ref session.EventsLost);
        }
    }

    public bool IsWindowActive
    {
        get { lock (subscriptionGate) return activeSession is { WindowActive: true }; }
    }

    /// <summary>Opens observation coverage and its duration-keyword hold on the run's metadata session.</summary>
    public void BeginWindow()
    {
        lock (durationResourceGate)
        {
            lock (subscriptionGate) BeginWindowCore();
            SynchronizeDurationHold();
        }
    }

    internal async Task<T> RunWindowAsync<T>(Func<Task<T>> sample)
    {
        try
        {
            BeginWindow();
            return await sample().ConfigureAwait(false);
        }
        finally { PauseWindow(); }
    }
    private void BeginWindowCore()
    {
        if (stopped) return;
        windowRequested = true;
        if (activeSession is not { } session || session.WindowActive) return;
        session.Window.Invalidate();
        session.ResetContinuity();
        session.Window = session.CreateWindow();
        session.WindowStartQpc = Stopwatch.GetTimestamp();
        session.UnavailableReason = CpuCoreResidencyUnavailableReasons.WindowWarming;
        state = "Warming";
        message = WarmingMessage(session);
        session.WindowActive = true;
    }

    /// <summary>Stops finite CSwitch aggregation; process/thread metadata and the resource holder remain live.</summary>
    public void PauseWindow(string idleMessage = "CPU residency is waiting for its next finite observation window.")
    {
        lock (durationResourceGate)
        {
            try { lock (subscriptionGate) PauseWindowCore(idleMessage); }
            finally { SynchronizeDurationHold(); }
        }
    }

    private void PauseWindowCore(string idleMessage)
    {
        if (stopped) return;
        windowRequested = false;
        if (activeSession is { } session)
        {
            session.WindowActive = false;

            session.Window.Invalidate();
            session.Buffer.ReleaseWindowBuckets();
        }
        state = "Idle";
        message = idleMessage;
    }
    public bool HasSessionLease
    {
        get
        {
            lock (subscriptionGate)
            {
                return kernelSubscription is not null
                    || activeSession is not null || retiredBuffers.Count != 0;
            }
        }
    }

    public void EnsureStarted()
    {
        lock (durationResourceGate)
        {
            EnsureMetadataStarted();
            SynchronizeDurationHold();
        }
    }

    private void EnsureMetadataStarted()
    {
        long expectedBindingEpoch;
        lock (subscriptionGate)
        {
            if (stopped)
            {
                state = "Idle";
                message = "ETW CPU execution-time attribution owner has been disposed.";
                return;
            }

            if (kernelSubscription is not null)
            {
                RefreshBrokerState();
                return;
            }

            expectedBindingEpoch = checked(++bindingEpoch);
            windowRequested = true;
        }

        // The containing run is the starter. Broker reconciliation can bind
        // this reader on another thread, so do not hold its subscription lock here.
        try
        {
            var created = kernelEtwBroker.Acquire(new OracleKernelEtwSubscriptionRequest(
                "cpu-core-residency",
                MetadataKeywords,
                (kernel, context) => BindKernelEvents(kernel, context, expectedBindingEpoch))
               );
            var accepted = false;
            lock (subscriptionGate)
            {
                if (!stopped
                    && bindingEpoch == expectedBindingEpoch)
                {
                    kernelSubscription = created;
                    accepted = true;
                    RefreshBrokerState();
                }
            }
            if (!accepted) created.Dispose();
        }
        catch (Exception ex)
        {
            lock (subscriptionGate)
            {
                if (stopped || bindingEpoch != expectedBindingEpoch) return;
                var failed = Volatile.Read(ref activeSession);
                if (failed?.ManagedBuffer is { } failedBuffer) retiredBuffers.Add(failedBuffer);
                Volatile.Write(ref activeSession, null);
                state = "Unavailable";
                message = $"ETW CPU 执行时长归因启动失败：{ex.Message}";
                logger.LogWarning(ex, "Failed to start ETW CPU execution-time reader.");
            }
        }
    }

    private void BindKernelEvents(
        OracleKernelEtwEventSource kernel,
        KernelEtwSessionContext context,
        long expectedBindingEpoch)
    {
        lock (subscriptionGate)
        {
            if (bindingEpoch != expectedBindingEpoch)
            {
                return;
            }
        }

        var plan = readConfiguration().RequirePublished();
        var session = new ResidencySessionState(
            context.Generation,
            context.StartedAt,
            expectedBindingEpoch,
            plan,
            CreateAggregationBuffer(plan, Stopwatch.Frequency, allocateBuckets));
        var processorIds = plan.PhysicalCoreByLogicalProcessor.Keys.Order().ToArray();
        BindKernelEventCallbacks(
            kernel,
            session.Buffer,
            () => IsCurrentSession(session, expectedBindingEpoch),
            ReadEventCreation,
            index => ResolveEventLogicalProcessorId(index, processorIds),
            observeContextSwitch: (qpc, time, processor, oldPid, oldTid, newPid, name, newTid) =>
                ObserveContextSwitch(session, qpc, time, processor, oldPid, oldTid, newPid, name, newTid),
            includeContextSwitch: false);
        BindDiagnosticSource(kernel, context.Generation, () => IsCurrentSession(session, expectedBindingEpoch));

        ResidencySessionState? previous;
        lock (subscriptionGate)
        {
            if (bindingEpoch != expectedBindingEpoch)
            {
                return;
            }

            previous = Volatile.Read(ref activeSession);
            if (previous is not null) session.UnavailableReason = CpuCoreResidencyUnavailableReasons.SessionRestarted;
            if (previous?.ManagedBuffer is { } previousBuffer) retiredBuffers.Add(previousBuffer);
            Volatile.Write(ref activeSession, null);
            ClearRetiredBuffersCore();
            session.WindowActive = windowRequested;

            Volatile.Write(ref activeSession, session);
            state = "Warming";
            message = $"ETW CPU 执行时长归因正在形成最近 {plan.ObservationWindow.TotalSeconds:0.###} 秒完整窗口。";
        }

        DrainInventorySeed(previous);
        StartProcessInventorySeed(session);
    }

    private void ObserveContextSwitch(ResidencySessionState session, long qpc, DateTimeOffset time,
        int processor, int oldPid, int oldTid, int newPid, string? name, int newTid)
    {
        lock (subscriptionGate)
        {
            if (!IsCurrentSessionCore(session) || !session.WindowActive || qpc < session.WindowStartQpc) return;
            session.Buffer.ObserveContextSwitch(qpc, time, processor, oldPid, oldTid, newPid, name, newTid);
            session.Window.Observe(processor, qpc);
        }
    }

    private void StartProcessInventorySeed(ResidencySessionState session)
    {
        lock (subscriptionGate)
        {
            if (!IsCurrentSessionCore(session) || session.InventorySeedTask is not null) return;
            session.InventorySeedTask = Task.Run(() =>
            {
                while (true)
                {
                    OracleCpuResidencyAggregationBuffer.InventorySeed inventory;
                    int expectedEventsLost;
                    lock (subscriptionGate)
                    {
                        if (!IsCurrentSessionCore(session))
                        {
                            session.InventorySeedTask = null;
                            return;
                        }
                        inventory = session.Buffer.CaptureInventorySeed();
                        expectedEventsLost = session.EventsLost;
                    }
                    try
                    {
                        seedProcessInventory(inventory,
                            () => IsCurrentSession(session, session.BindingEpoch)
                                && Volatile.Read(ref session.EventsLost) == expectedEventsLost);
                    }
                    catch (Exception error)
                    {
                        logger.LogWarning(error, "Failed to seed ETW CPU process inventory.");
                    }
                    lock (subscriptionGate)
                    {
                        if (IsCurrentSessionCore(session) && session.EventsLost != expectedEventsLost) continue;
                        session.InventorySeedTask = null;
                        return;
                    }
                }
            }, CancellationToken.None);
        }
    }

    private void RefreshEventLoss(ResidencySessionState session, KernelEtwSessionSnapshot broker)
    {
        if (!broker.IsRunning || broker.Generation != session.Generation) return;
        lock (subscriptionGate)
        {
            if (!IsCurrentSessionCore(session) || session.EventsLost == broker.EventsLost) return;
            session.ManagedBuffer?.ResetAfterEventLoss(); // Native loss was already consumed on the event flow.

            session.UnavailableReason = CpuCoreResidencyUnavailableReasons.EventsLost;
            session.Window.Invalidate();
            Volatile.Write(ref session.EventsLost, broker.EventsLost);
            state = "Warming";
            message = WarmingMessage(session);
        }
        StartProcessInventorySeed(session);
    }

    internal static OracleCpuResidencyAggregationBuffer CreateAggregationBuffer(
        CpuResidencyCaptureConfiguration plan,
        long qpcFrequency, Func<int, OracleCaptureBucketMemory>? allocateBuckets = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var published = plan.RequirePublished();
        return new OracleCpuResidencyAggregationBuffer(
            qpcFrequency,
            published.ObservationWindowMilliseconds,
            published.MaximumActivePairs, published.BucketWidthMilliseconds, allocateBuckets);
    }

    internal static void BindKernelEventCallbacks(
        OracleKernelEtwEventSource kernel,
        OracleCpuResidencyAggregationBuffer buffer,
        Func<bool> shouldObserve,
        Func<int, long?> readProcessStartKey,
        Func<int, int?>? resolveProcessorId = null)
    {
        ArgumentNullException.ThrowIfNull(readProcessStartKey);
        BindKernelEventCallbacks(kernel, buffer, shouldObserve, (pid, _) => readProcessStartKey(pid), resolveProcessorId);
    }

    internal static void BindKernelEventCallbacks(
        OracleKernelEtwEventSource kernel,
        OracleCpuResidencyAggregationBuffer buffer,
        Func<bool> shouldObserve,
        Func<int, DateTimeOffset, long?> readProcessStartKey,
        Func<int, int?>? resolveProcessorId = null,
        Action<int, long>? boundaryObserved = null,
        Action<long, DateTimeOffset, int, int, int, int, string?, int>? observeContextSwitch = null,
        bool includeContextSwitch = true)
    {
        ArgumentNullException.ThrowIfNull(kernel);
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentNullException.ThrowIfNull(shouldObserve);
        ArgumentNullException.ThrowIfNull(readProcessStartKey);
        kernel.ProcessStartGroup += data =>
        {
            if (!shouldObserve())
            {
                return;
            }

            buffer.ObserveProcessStart(
                data.TimeStampQPC,
                ToTimestamp(data.TimeStamp),
                data.ProcessID,
                unchecked((ulong)data.UniqueProcessKey),
                readProcessStartKey(data.ProcessID, ToTimestamp(data.TimeStamp)),
                FirstNonEmpty(data.ProcessName, data.ImageFileName));
        };
        kernel.ProcessEndGroup += data =>
        {
            if (!shouldObserve())
            {
                return;
            }

            buffer.ObserveProcessEnd(
                data.TimeStampQPC,
                ToTimestamp(data.TimeStamp),
                data.ProcessID,
                unchecked((ulong)data.UniqueProcessKey));
        };
        kernel.ThreadStartGroup += data =>
        {
            if (shouldObserve())
            {
                buffer.ObserveThreadStart(
                    data.TimeStampQPC,
                    ToTimestamp(data.TimeStamp),
                    data.ProcessID,
                    data.ThreadID);
            }
        };
        kernel.ThreadEndGroup += data =>
        {
            if (shouldObserve())
            {
                buffer.ObserveThreadEnd(
                    data.TimeStampQPC,
                    ToTimestamp(data.TimeStamp),
                    data.ProcessID,
                    data.ThreadID);
            }
        };
        if (!includeContextSwitch) return;
        BindContextSwitchCallbacks(kernel, buffer, shouldObserve, resolveProcessorId, boundaryObserved, observeContextSwitch);
    }

    private static void BindContextSwitchCallbacks(OracleKernelEtwEventSource kernel, OracleCpuResidencyAggregationBuffer buffer,
        Func<bool> shouldObserve, Func<int, int?>? resolveProcessorId = null, Action<int, long>? boundaryObserved = null,
        Action<long, DateTimeOffset, int, int, int, int, string?, int>? observeContextSwitch = null)
    {
        kernel.ThreadCSwitch += data =>
        {
            var processorId = resolveProcessorId is null ? data.ProcessorNumber : resolveProcessorId(data.ProcessorNumber);
            if (shouldObserve() && processorId is { } logicalProcessorId)
            {
                // A composition may serialize and gate finite CSwitch intake while keeping metadata live.
                if (observeContextSwitch is not null)
                {
                    observeContextSwitch(data.TimeStampQPC, ToTimestamp(data.TimeStamp), logicalProcessorId,
                        data.OldProcessID, data.OldThreadID, data.NewProcessID, data.NewProcessName, data.NewThreadID);
                    return;
                }
                buffer.ObserveContextSwitch(
                    data.TimeStampQPC,
                    ToTimestamp(data.TimeStamp),
                    logicalProcessorId,
                    data.OldProcessID,
                    data.OldThreadID,
                    data.NewProcessID,
                    data.NewProcessName,
                    data.NewThreadID);
                boundaryObserved?.Invoke(logicalProcessorId, data.TimeStampQPC);
            }
        };

    }

    internal static int? ResolveEventLogicalProcessorId(int eventProcessorIndex, IReadOnlyList<int> processorIds)
        => eventProcessorIndex >= 0 && eventProcessorIndex < processorIds.Count ? processorIds[eventProcessorIndex] : null;

    private bool IsCurrentSession(ResidencySessionState session, long expectedBindingEpoch)
        => Volatile.Read(ref bindingEpoch) == expectedBindingEpoch
            && ReferenceEquals(Volatile.Read(ref activeSession), session);

    private void RefreshBrokerState(ResidencySessionState? expectedSession = null)
    {
        var broker = ReadBrokerSnapshot();
        var session = Volatile.Read(ref activeSession);
        if (session is not null) RefreshEventLoss(session, broker);
        if (session is not null
            && broker.IsRunning
            && broker.Generation == session.Generation
            && broker.EventsLost == Volatile.Read(ref session.EventsLost))
        {
            return;
        }

        lock (subscriptionGate)
        {
            if (expectedSession is not null
                && !IsCurrentSessionCore(expectedSession))
            {
                return;
            }

            state = broker.State;
            message = broker.Message;
        }
    }

    public bool Collect(IMetricPublicationPermit? ownerToken = null)
        => Collect(ownerToken, null);

    /// <summary>Collects from the original session and invokes the callback only inside an accepted current commit.</summary>
    public bool Collect(IMetricPublicationPermit? ownerToken,
        Action<CpuCoreResidencySnapshot?, bool>? captureCommitted)
    {
        ResidencySessionState? observedSession;
        OracleCpuResidencyWindowCompletion? observedWindow;
        long observedBindingEpoch;
        lock (subscriptionGate)
        {
            if (stopped || activeSession is { WindowActive: false })
            {
                return false;
            }

            observedSession = Volatile.Read(ref activeSession);
            observedWindow = observedSession?.Window;
            observedBindingEpoch = bindingEpoch;
        }

        try
        {
            var now = DateTimeOffset.UtcNow;
            var brokerBefore = ReadBrokerSnapshot();
            if (observedSession is not null) RefreshEventLoss(observedSession, brokerBefore);
            CpuCoreResidencySnapshot? snapshot;
            string nextState;
            string nextMessage;
            var unavailableReason = BrokerUnavailableReason(brokerBefore, observedSession);
            var sampled = observedSession is not null
                && brokerBefore.IsRunning
                && brokerBefore.Generation == observedSession.Generation
                && brokerBefore.EventsLost == Volatile.Read(ref observedSession.EventsLost);
            if (!sampled)
            {
                if (!ResetWindowContinuity(observedSession, observedWindow)) return false;
                snapshot = null;
                (nextState, nextMessage) = BrokerDiagnostic(brokerBefore, observedSession);
            }
            else
            {
                var currentSession = observedSession
                    ?? throw new InvalidOperationException("The running ETW session state is missing.");
                var aggregate = currentSession.Buffer.Snapshot(currentSession.Plan.PhysicalCoreByLogicalProcessor.Keys);
                var brokerAfter = ReadBrokerSnapshot();
                if (!brokerAfter.IsRunning
                    || brokerAfter.Generation != currentSession.Generation
                    || brokerAfter.EventsLost != brokerBefore.EventsLost
                    || brokerAfter.EventsLost != Volatile.Read(ref currentSession.EventsLost))
                {
                    RefreshEventLoss(currentSession, brokerAfter);
                    if (!ResetWindowContinuity(currentSession, observedWindow)) return false;
                    snapshot = null;
                    (nextState, nextMessage) = BrokerDiagnostic(brokerAfter, currentSession);
                    unavailableReason = BrokerUnavailableReason(brokerAfter, currentSession);
                    sampled = false;
                }
                else if (!aggregate.IsComplete)
                {
                    unavailableReason = currentSession.UnavailableReason;
                    snapshot = null;
                    nextState = "Warming";
                    nextMessage = WarmingMessage(currentSession);
                }
                else
                {
                    var measuredThrough = aggregate.MeasuredThrough
                        ?? throw new InvalidDataException(
                            "A complete CPU execution-time window must include its measured-through timestamp.");
                    var measuredWindow = ToDuration(
                        aggregate.MeasuredThroughQpc - aggregate.WindowStartQpc,
                        aggregate.QpcFrequency);
                    snapshot = new CpuCoreResidencySnapshot(
                        now,
                        measuredWindow,
                        currentSession.Generation,
                        measuredThrough - measuredWindow,
                        measuredThrough,
                        EtwCpuResidencyCaptureOwner.BuildProcessSnapshot(currentSession.Plan, currentSession.Generation, aggregate))
                    { LogicalProcessorScopes = CreateScopes(currentSession.Plan) };
                    nextState = "Running";
                    nextMessage = "ETW CPU 执行时长归因正在提供真实闭合执行区间。";
                }
            }

            return Publish(
                ownerToken,
                snapshot,
                sampled,
                observedSession,
                observedWindow,
                observedBindingEpoch,
                brokerBefore.EventsLost,
                nextState,
                nextMessage,
                unavailableReason,
                captureCommitted);
        }
        catch (Exception ex)
        {
            _ = TryPublish(ownerToken, () =>
            {
                lock (subscriptionGate)
                {
                    if (stopped || activeSession is { WindowActive: false }
                        || !ReferenceEquals(activeSession?.Window, observedWindow)
                        || bindingEpoch != observedBindingEpoch
                        || !ReferenceEquals(Volatile.Read(ref activeSession), observedSession))
                    {
                        return;
                    }

                    logger.LogWarning(ex, "Failed to build ETW CPU execution-time snapshot.");
                    state = "Unavailable";
                    message = $"ETW CPU 执行时长快照生成失败：{ex.Message}";
                    SetCachedSnapshot(null, CpuCoreResidencyUnavailableReasons.CaptureFailed);
                    NotifyCaptureCommitted(captureCommitted, null, sourceQuerySucceeded: false);
                    Publications.Publish(["process.cpu.residency"]); published?.Invoke();
                }
            });
            return false;
        }
    }

    private bool ResetWindowContinuity(ResidencySessionState? session, OracleCpuResidencyWindowCompletion? window)
    {
        lock (subscriptionGate)
        {
            if (stopped || activeSession is { WindowActive: false }
                || !ReferenceEquals(activeSession, session) || !ReferenceEquals(activeSession?.Window, window)) return false;
            session?.ResetContinuity();
            return true;
        }
    }

    private bool Publish(
        IMetricPublicationPermit? ownerToken,
        CpuCoreResidencySnapshot? snapshot,
        bool sampled,
        ResidencySessionState? expectedSession,
        OracleCpuResidencyWindowCompletion? expectedWindow,
        long expectedBindingEpoch,
        int expectedEventsLost,
        string nextState,
        string nextMessage,
        string unavailableReason,
        Action<CpuCoreResidencySnapshot?, bool>? captureCommitted)
    {
        var committed = false;
        var committedAsSampled = sampled;
        var accepted = TryPublish(ownerToken, () =>
        {
            lock (subscriptionGate)
            {
                if (stopped || activeSession is { WindowActive: false }
                    || !ReferenceEquals(activeSession?.Window, expectedWindow)
                    || bindingEpoch != expectedBindingEpoch
                    || !ReferenceEquals(Volatile.Read(ref activeSession), expectedSession))
                {
                    return;
                }

                if (expectedSession is not null)
                {
                    var broker = ReadBrokerSnapshot();
                    if (!broker.IsRunning
                        || broker.Generation != expectedSession.Generation
                        || broker.EventsLost != expectedEventsLost
                        || broker.EventsLost != Volatile.Read(ref expectedSession.EventsLost))
                    {
                        RefreshEventLoss(expectedSession, broker);
                        expectedSession.ResetContinuity();
                        snapshot = null;
                        committedAsSampled = false;
                        (nextState, nextMessage) = BrokerDiagnostic(broker, expectedSession);
                        unavailableReason = BrokerUnavailableReason(broker, expectedSession);
                    }
                }

                SetCachedSnapshot(snapshot, unavailableReason);
                if (snapshot is not null && expectedSession is not null)
                    expectedSession.UnavailableReason = CpuCoreResidencyUnavailableReasons.WindowWarming;
                state = nextState;
                message = nextMessage;
                NotifyCaptureCommitted(captureCommitted, snapshot,
                    sourceQuerySucceeded: committedAsSampled && IsCompleteWindow(snapshot));
                Publications.Publish(["process.cpu.residency"]); published?.Invoke();
                committed = true;
            }
        });
        return accepted && committed && committedAsSampled;
    }

    private void NotifyCaptureCommitted(Action<CpuCoreResidencySnapshot?, bool>? captureCommitted,
        CpuCoreResidencySnapshot? snapshot, bool sourceQuerySucceeded)
    {
        if (captureCommitted is null) return;
        try { captureCommitted(snapshot, sourceQuerySucceeded); }
        catch (Exception error) { logger.LogWarning(error, "Failed to snapshot committed CPU residency field readings."); }
    }

    private static bool IsCompleteWindow(CpuCoreResidencySnapshot? snapshot)
        => snapshot is not null && snapshot.Window > TimeSpan.Zero
            && snapshot.MeasuredThrough > snapshot.MeasuredFrom
            && snapshot.MeasuredThrough - snapshot.MeasuredFrom == snapshot.Window;

    private bool IsCurrentSessionCore(ResidencySessionState session)
        => bindingEpoch == session.BindingEpoch
            && ReferenceEquals(Volatile.Read(ref activeSession), session);

    private void SetCachedSnapshot(CpuCoreResidencySnapshot? snapshot, string unavailableReason)
    {
        lock (snapshotGate)
        {
            currentObservation = snapshot is not null ? snapshot : new CpuCoreResidencyUnavailable(unavailableReason);
            Interlocked.Increment(ref revision);
        }
    }

    private static (string State, string Message) BrokerDiagnostic(
        KernelEtwSessionSnapshot broker, ResidencySessionState? session)
        => broker.IsRunning && session is not null && broker.Generation == session.Generation
            ? ("Warming", WarmingMessage(session))
            : (broker.State, broker.Message);

    private static string BrokerUnavailableReason(KernelEtwSessionSnapshot broker, ResidencySessionState? session)
        => !broker.IsRunning || session is null ? CpuCoreResidencyUnavailableReasons.SessionUnavailable
            : broker.Generation != session.Generation ? CpuCoreResidencyUnavailableReasons.SessionRestarted
            : session.UnavailableReason;

    private static string WarmingMessage(ResidencySessionState session)
        => $"ETW CPU 执行时长归因正在形成最近 {session.Plan.ObservationWindow.TotalSeconds:0.###} 秒完整窗口。";

    public void Stop(string idleMessage = "ETW CPU attribution stopped after its demand was withdrawn.")
    {
        lock (durationResourceGate)
        {
        IOracleKernelEtwSubscription? subscriptionToRelease = null;
        IOracleKernelEtwSubscription? durationToRelease;
        ResidencySessionState? sessionToClear = null;
        lock (subscriptionGate)
        {
            if (stopped) { ClearRetiredBuffersCore(); return; }

            durationToRelease = DetachDurationHoldCore();
            (subscriptionToRelease, sessionToClear) = DetachSessionCore();
            state = "Idle";
            message = idleMessage;
        }

        try { DrainInventorySeed(sessionToClear); }
        finally { try { durationToRelease?.Dispose(); } finally { try { subscriptionToRelease?.Dispose(); } finally { ClearRetiredBuffers(); } } }
        }
    }

    private (IOracleKernelEtwSubscription? Subscription, ResidencySessionState? Session) DetachSessionCore()
    {
        ClearDiagnosticSource();
        bindingEpoch = checked(bindingEpoch + 1);
        var subscription = kernelSubscription;
        kernelSubscription = null;
        var session = Volatile.Read(ref activeSession);
        if (session is not null) { session.Window.Invalidate(); if (session.ManagedBuffer is { } buffer) retiredBuffers.Add(buffer); }
        Volatile.Write(ref activeSession, null);
        return (subscription, session);
    }

    private void ClearRetiredBuffers()
    {
        lock (subscriptionGate) ClearRetiredBuffersCore();
    }
    private static void DrainInventorySeed(ResidencySessionState? session)
        => session?.InventorySeedTask?.GetAwaiter().GetResult();
    private void ClearRetiredBuffersCore()
    {
        while (retiredBuffers.Count != 0)
        {
            var buffer = retiredBuffers.First();
            buffer.Clear(); // Keep ownership on failure; a later Stop/Dispose/rebind retries the same close.
            retiredBuffers.Remove(buffer);
        }
        retiredBuffers.TrimExcess();
    }

    private static long? ReadEventCreation(int pid, DateTimeOffset eventTime)
    {
        using var identity = WindowsNetworkEndpointProcessLease.Open(unchecked((uint)pid));
        return ResolveEventCreation(pid, eventTime, identity);
    }
    internal static long? ResolveEventCreation(int pid, DateTimeOffset eventTime, IWindowsNetworkEndpointProcessLease? identity)
    {
        if (pid <= 0 || identity is null || identity.ProcessId != (uint)pid
            || !identity.TryReadCreationFileTime(out var birth) || birth == 0 || birth > long.MaxValue
            || birth > (ulong)eventTime.UtcDateTime.ToFileTimeUtc()
            || !identity.TryReadCreationFileTime(out var after) || birth != after) return null;
        // A reused PID born after this start event is rejected. Same live handle supplies raw FILETIME.
        return (long)birth;
    }
    internal static void SeedInventory(OracleCpuResidencyAggregationBuffer.InventorySeed inventory, Func<bool> shouldSeed) => SeedExistingProcesses(inventory, shouldSeed);
    private static void SeedExistingProcesses(OracleCpuResidencyAggregationBuffer.InventorySeed inventory, Func<bool> shouldSeed)
    {
        VisitOwnedArray(Process.GetProcesses(), process =>
        {
            if (!shouldSeed()) return false;
            try
            {
                using var identity = WindowsNetworkEndpointProcessLease.Open(unchecked((uint)process.Id));
                if (identity is null || !identity.TryReadCreationFileTime(out var birth) || birth == 0 || birth > long.MaxValue) return true;
                var name = process.ProcessName;
                if (!shouldSeed()) return false;
                var threadLeases = process.Threads.Cast<ProcessThread>().ToArray();
                var threads = new List<int>(threadLeases.Length);
                if (!VisitOwnedArray(threadLeases, thread =>
                {
                    if (!shouldSeed()) return false;
                    threads.Add(thread.Id);
                    return true;
                })) return false;
                if (!shouldSeed() || !identity.TryReadCreationFileTime(out var after) || after != birth) return true;
                var seeded = inventory.SeedProcess(process.Id, (long)birth, name);
                if (seeded is null) return true;
                foreach (var thread in threads) seeded.SeedThread(thread);
            }
            catch { /* Permission/retirement means no proven inventory entry; never PID-only fallback. */ }
            return true;
        });
    }

    internal static bool VisitOwnedArray<T>(T[] items, Func<T, bool> visit) where T : IDisposable
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(visit);
        var next = 0;
        try
        {
            while (next < items.Length)
            {
                var item = items[next];
                bool continueVisiting;
                try { continueVisiting = visit(item); }
                finally
                {
                    try { item.Dispose(); }
                    finally { next++; }
                }
                if (!continueVisiting) return false;
            }
            return true;
        }
        finally
        {
            List<Exception>? disposalErrors = null;
            for (; next < items.Length; next++)
            {
                try { items[next].Dispose(); }
                catch (Exception error) { (disposalErrors ??= []).Add(error); }
            }
            if (disposalErrors is { Count: 1 })
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(disposalErrors[0]).Throw();
            if (disposalErrors is { Count: > 1 })
                throw new AggregateException("Some owned process inventory handles could not be disposed.", disposalErrors);
        }
    }

    private static DateTimeOffset ToTimestamp(DateTime timestamp)
        => new(timestamp.Kind == DateTimeKind.Utc ? timestamp : timestamp.ToUniversalTime());

    private static string? FirstNonEmpty(string? first, string? second)
        => !string.IsNullOrWhiteSpace(first)
            ? first
            : !string.IsNullOrWhiteSpace(second)
                ? second
                : null;

    private static string FormatInstanceId(long generation, long instanceId)
        => string.Create(CultureInfo.InvariantCulture, $"{generation}:{instanceId}");

    private static double ToMilliseconds(long durationQpc, long qpcFrequency)
        => Math.Round(durationQpc * 1000d / qpcFrequency, 3);

    private static TimeSpan ToDuration(long durationQpc, long qpcFrequency)
        => TimeSpan.FromSeconds(durationQpc / (double)qpcFrequency);

    private static double Share(long durationQpc, long totalDurationQpc)
        => totalDurationQpc <= 0
            ? 0
            : Math.Round(durationQpc * 100d / totalDurationQpc, 2);

    private sealed class ResidencySessionState(
        long generation,
        DateTimeOffset startedAt,
        long bindingEpoch,
        CpuResidencyCaptureConfiguration plan,
        OracleCpuResidencyAggregationBuffer buffer)
    {
        public long Generation { get; } = generation;
        public DateTimeOffset StartedAt { get; } = startedAt;
        public long BindingEpoch { get; } = bindingEpoch;
        public CpuResidencyCaptureConfiguration Plan { get; } = plan;
        public OracleCpuResidencyAggregationBuffer? ManagedBuffer { get; } = buffer;
        public OracleCpuResidencyAggregationBuffer Buffer => ManagedBuffer ?? throw new InvalidOperationException("A native capture has no managed event buffer.");
        public void ResetContinuity() => Buffer.ResetContinuity();
        public OracleCpuResidencyWindowCompletion Window { get; set; } = new(plan.PhysicalCoreByLogicalProcessor.Keys,
            checked(Stopwatch.Frequency * (long)plan.ObservationWindowMilliseconds / 1000));
        public OracleCpuResidencyWindowCompletion CreateWindow() => new(Plan.PhysicalCoreByLogicalProcessor.Keys,
            checked(Stopwatch.Frequency * (long)Plan.ObservationWindowMilliseconds / 1000));
        public bool WindowActive = true;
        public long WindowStartQpc = Stopwatch.GetTimestamp();
        public int EventsLost;
        public string UnavailableReason = CpuCoreResidencyUnavailableReasons.WindowWarming;
        public Task? InventorySeedTask;
    }

    public async Task<bool> WaitForCompleteWindowAsync(TimeSpan boundaryBudget, TimeSpan deliveryGrace,
        CancellationToken cancellationToken)
    {
        ResidencySessionState? session;
        OracleCpuResidencyWindowCompletion window;
        lock (subscriptionGate)
        {
            session = activeSession;
            if (session is null || !session.WindowActive) return false;
            window = session.Window;
        }
        if (!IsRunning) return false;
        try
        {
            if (!await window.Boundaries.WaitAsync(boundaryBudget, cancellationToken)) return false;
            if (!await window.Complete.WaitAsync(session.Plan.ObservationWindow + deliveryGrace, cancellationToken)) return false;
            lock (subscriptionGate)
                return !stopped && IsCurrentSessionCore(session)
                    && session.WindowActive && ReferenceEquals(session.Window, window);
        }
        catch (TimeoutException)
        {
            window.Invalidate();
            logger.LogWarning(new TimeoutException("CPU residency capture did not observe a complete boundary window."),
                "CPU residency capture produced no complete window within its observation and delivery budget.");
            return false;
        }
    }

    private static bool TryPublish(IMetricPublicationPermit? permit, Action commit)
    { if (permit is not null) return permit.TryPublish(commit); commit(); return true; }
    public void Dispose()
    {
        lock (durationResourceGate)
        {
        IOracleKernelEtwSubscription? lease, duration; ResidencySessionState? session;
        lock (subscriptionGate)
        {
            if (stopped) { ClearRetiredBuffersCore(); return; }
            stopped = true; DisposeDiagnosticRequests(); duration = DetachDurationHoldCore(); (lease, session) = DetachSessionCore(); state = "Stopped";
        }
        try { DrainInventorySeed(session); }
        finally { try { duration?.Dispose(); } finally { try { lease?.Dispose(); } finally { ClearRetiredBuffers(); } } }
        }
    }
}
#pragma warning restore CS0618
