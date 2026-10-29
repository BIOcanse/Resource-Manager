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
namespace ResourceManager.ReadWrite.Windows.Processes;
#pragma warning disable CS0618
internal sealed record CpuResidencyOwnerDiagnostics(string State, string Message, long BindingEpoch,
    long? SessionGeneration, CpuResidencyCaptureConfiguration? SessionPlan, CpuResidencyAggregationDiagnostics? Aggregation);
/// <summary>The original shared-broker session, closed-window buffer and published raw current.
/// No worker or global broker. EnsureStarted/Collect/Stop are explicit; borrowed broker is never disposed.</summary>
public sealed partial class EtwCpuResidencyCaptureOwner : IDisposable
{
    // Lifecycle callbacks maintain process/thread identities between windows, so the
    // next context-switch boundary can be attributed without reseeding stale PIDs/TIDs.
    private const KernelTraceEventParser.Keywords RequiredKeywords = KernelTraceEventParser.Keywords.Process | KernelTraceEventParser.Keywords.Thread;

    private readonly IKernelEtwSessionBroker kernelEtwBroker;
    private readonly Func<CpuResidencyCaptureConfiguration> readConfiguration;
    private readonly KernelEtwDiagnostics logger;
    private readonly Action<CpuResidencyAggregationBuffer.InventorySeed, Func<bool>> seedProcessInventory;
    private readonly Action? published;
    private readonly Func<int, CaptureBucketMemory>? allocateBuckets;
    // Borrowed from the enclosing window/run. Contains positions, never leases.
    private CaptureBucketStorage? bucketStorage;
    private readonly object subscriptionGate = new(), snapshotGate = new();
    public SamplingPublicationSignal Publications { get; } = new();
    private IKernelEtwSubscription? kernelSubscription;
    private IKernelEtwSubscription? contextSwitchSubscription;
    private long contextSwitchEpoch;
    private bool acquiringContextSwitch;
    private ResidencySessionState? activeSession;
    private readonly HashSet<CpuResidencyAggregationBuffer> retiredBuffers = [];
    private long bindingEpoch;
    private long revision;
    public long Revision => Interlocked.Read(ref revision);
    private CpuCoreResidencyObservation currentObservation = new CpuCoreResidencyUnavailable(CpuCoreResidencyUnavailableReasons.NotRequested);
    private CpuCoreResidencySnapshot? cachedSnapshot => currentObservation as CpuCoreResidencySnapshot;
    private bool stopped, paused;
    private volatile string state = "Idle", message = "ETW CPU execution-time attribution starts on explicit demand.";
    public string State => state;
    public string Message => message;
    public EtwCpuResidencyCaptureOwner(IKernelEtwSessionBroker broker,
        Func<CpuResidencyCaptureConfiguration> configuration, KernelEtwDiagnostics? diagnostics = null)
        : this(broker, configuration, diagnostics ?? new(), SeedExistingProcesses) { }
    internal EtwCpuResidencyCaptureOwner(IKernelEtwSessionBroker broker, Func<CpuResidencyCaptureConfiguration> configuration,
        KernelEtwDiagnostics diagnostics, Action<CpuResidencyAggregationBuffer.InventorySeed, Func<bool>> seed, Action? published = null, Func<int, CaptureBucketMemory>? allocateBuckets = null)
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

    internal CpuResidencyOwnerDiagnostics GetDiagnostics()
    {
        lock (subscriptionGate)
        {
            var session = Volatile.Read(ref activeSession);
            return new CpuResidencyOwnerDiagnostics(
                state,
                message,
                bindingEpoch,
                session?.Generation,
                session?.Plan,
                session?.Buffer.GetDiagnostics());
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

            var broker = CaptureBrokerSnapshot();
            return broker.IsRunning
                && broker.Generation == session.Generation
                && broker.EventsLost == Volatile.Read(ref session.EventsLost);
        }
    }

    /// <summary>Pauses execution-window intake without releasing the run's kernel resource or published value.</summary>
    public void SetCaptureEnabled(bool enabled, string idleMessage = "CPU residency capture is paused by its composition.", bool beginWindow = true)
    {
        IKernelEtwSubscription? held = null;
        lock (subscriptionGate)
        {
            if (stopped) return;
            if (paused == !enabled) return;
            paused = !enabled;
            if (paused)
            {
                PauseWindowCore(idleMessage);
                held = DetachContextSwitchCore();
            }
            else if (beginWindow) BeginWindowCore();
            else PauseWindowCore("CPU residency is waiting for its next finite observation window.");
        }
        held?.Dispose();
        if (enabled && beginWindow) EnsureContextSwitchHeld();
    }

    public bool IsWindowActive
    {
        get { lock (subscriptionGate) return !paused && activeSession is { WindowActive: true }; }
    }

    /// <summary>Starts fresh boundary coverage and holds context switches while the window needs them.</summary>
    public void BeginWindow()
    {
        lock (subscriptionGate) BeginWindowCore();
        EnsureContextSwitchHeld();
    }

    internal void SetBucketStorage(CaptureBucketStorage storage)
    {
        lock (subscriptionGate)
        {
            activeSession?.Buffer.SetBucketStorage(storage);
            bucketStorage = storage;
        }
    }

    // This is the finite window method: skipped waits, cancellation, publication
    // and exceptions all return its storage and window-only event hold.
    internal async Task<T> RunWindowAsync<T>(CaptureBucketStorage storage, Func<Task<T>> sample)
    {
        try
        {
            SetBucketStorage(storage);
            BeginWindow();
            return await sample().ConfigureAwait(false);
        }
        finally { PauseWindow("CPU residency window has returned; aggregation is paused until the next window."); }
    }

    private void EnsureContextSwitchHeld()
    {
        long epoch;
        lock (subscriptionGate)
        {
            if (stopped || paused || kernelSubscription is null
                || activeSession is not { WindowActive: true }
                || contextSwitchSubscription is not null || acquiringContextSwitch) return;
            acquiringContextSwitch = true;
            epoch = ++contextSwitchEpoch;
        }
        IKernelEtwSubscription? created = null;
        try
        {
            // Reconciliation may bind the metadata holder on another thread.
            created = kernelEtwBroker.Subscribe(new("cpu-core-residency-window",
                KernelTraceEventParser.Keywords.ContextSwitch, static (_, _) => { }));
            lock (subscriptionGate)
            {
                if (contextSwitchEpoch == epoch && !stopped && !paused
                    && activeSession is { WindowActive: true })
                {
                    contextSwitchSubscription = created;
                    created = null;
                }
            }
        }
        catch (Exception error)
        {
            logger.LogWarning(error, "Failed to hold ETW CPU context-switch keywords.");
        }
        finally
        {
            lock (subscriptionGate)
                if (contextSwitchEpoch == epoch) acquiringContextSwitch = false;
            created?.Dispose();
        }
    }

    private IKernelEtwSubscription? DetachContextSwitchCore()
    {
        ++contextSwitchEpoch;
        acquiringContextSwitch = false;
        var held = contextSwitchSubscription;
        contextSwitchSubscription = null;
        return held;
    }

    private KernelEtwSessionSnapshot CaptureBrokerSnapshot()
    {
        var switches = Volatile.Read(ref contextSwitchSubscription)?.GetSnapshot();
        var metadata = Volatile.Read(ref kernelSubscription)?.GetSnapshot() ?? kernelEtwBroker.GetSnapshot();
        if (!metadata.IsRunning || Volatile.Read(ref activeSession) is not { WindowActive: true } || Volatile.Read(ref paused)) return metadata;
        return switches
            ?? metadata with { State = "Unavailable", Message = "CPU residency context-switch keywords are unavailable." };
    }

    private void BeginWindowCore()
    {
        if (stopped || paused || activeSession is not { } session || session.WindowActive) return;
        session.Window.Invalidate();
        session.Buffer.ResetContinuity();
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
        IKernelEtwSubscription? held;
        lock (subscriptionGate)
        {
            PauseWindowCore(idleMessage);
            held = DetachContextSwitchCore();
        }
        held?.Dispose();
    }

    private void PauseWindowCore(string idleMessage)
    {
        if (stopped) return;
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
        long expectedBindingEpoch;
        lock (subscriptionGate)
        {
            if (stopped)
            {
                state = "Idle";
                message = "ETW CPU 执行时长归因当前处于功能区冻结。";
                return;
            }

            if (kernelSubscription is not null)
            {
                RefreshBrokerState();
                return;
            }

            expectedBindingEpoch = checked(++bindingEpoch);
        }

        // The containing run is the starter. Broker reconciliation can bind
        // this reader on another thread, so do not hold its subscription lock here.
        try
        {
            var created = kernelEtwBroker.Subscribe(new KernelEtwSubscriptionRequest(
                "cpu-core-residency",
                RequiredKeywords,
                (kernel, context) => BindKernelEvents(kernel, context, expectedBindingEpoch)));
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
                if (failed is not null) retiredBuffers.Add(failed.Buffer);
                Volatile.Write(ref activeSession, null);
                state = "Unavailable";
                message = $"ETW CPU 执行时长归因启动失败：{ex.Message}";
                logger.LogWarning(ex, "Failed to start ETW CPU execution-time reader.");
            }
        }
    }

    private void BindKernelEvents(
        KernelEtwEventSource kernel,
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
        session.EventSource = kernel;
        session.ContextSwitchCounter = kernel.ConsumerCounter("ThreadCSwitch");
        var processorIds = plan.PhysicalCoreByLogicalProcessor.Keys.Order().ToArray();
        BindKernelEventCallbacks(
            kernel,
            session.Buffer,
            () => IsCurrentSession(session, expectedBindingEpoch),
            ReadEventCreation,
            index => ResolveEventLogicalProcessorId(index, processorIds),
            observeContextSwitch: (qpc, time, processor, oldPid, oldTid, newPid, name, newTid) =>
                ObserveContextSwitch(session, qpc, time, processor, oldPid, oldTid, newPid, name, newTid));
        BindDiagnosticSource(kernel, () => IsCurrentSession(session, expectedBindingEpoch));

        lock (subscriptionGate)
        {
            if (bindingEpoch != expectedBindingEpoch)
            {
                return;
            }

            var previous = Volatile.Read(ref activeSession);
            if (previous is not null) session.UnavailableReason = CpuCoreResidencyUnavailableReasons.SessionRestarted;
            if (previous is not null) retiredBuffers.Add(previous.Buffer);
            Volatile.Write(ref activeSession, null);
            ClearRetiredBuffersCore();
            if (bucketStorage is { } storage) session.Buffer.SetBucketStorage(storage);
            session.WindowActive = !paused && (previous?.WindowActive ?? true);
            Volatile.Write(ref activeSession, session);
            state = paused ? "Idle" : "Warming";
            message = $"ETW CPU 执行时长归因正在形成最近 {plan.ObservationWindow.TotalSeconds:0.###} 秒完整窗口。";
        }

        StartProcessInventorySeed(session);
    }

    private void ObserveContextSwitch(ResidencySessionState session, long qpc, DateTimeOffset time,
        int processor, int oldPid, int oldTid, int newPid, string? name, int newTid)
    {
        lock (subscriptionGate)
        {
            string? rejection = !IsCurrentSessionCore(session) ? "generation-mismatch"
                : paused ? "frozen" : !session.WindowActive ? "outside-window"
                : qpc < session.WindowStartQpc ? "before-window" : null;
            if (rejection is not null)
            {
                session.ContextSwitchCounter?.Drop();
                session.EventSource?.RecordConsumerRejection("ThreadCSwitch", rejection);
                return;
            }
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
                    CpuResidencyAggregationBuffer.InventorySeed inventory;
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
            session.Buffer.ResetAfterEventLoss();
            session.UnavailableReason = CpuCoreResidencyUnavailableReasons.EventsLost;
            session.Window.Invalidate();
            Volatile.Write(ref session.EventsLost, broker.EventsLost);
            state = "Warming";
            message = WarmingMessage(session);
        }
        StartProcessInventorySeed(session);
    }

    internal static CpuResidencyAggregationBuffer CreateAggregationBuffer(
        CpuResidencyCaptureConfiguration plan,
        long qpcFrequency, Func<int, CaptureBucketMemory>? allocateBuckets = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var published = plan.RequirePublished();
        return new CpuResidencyAggregationBuffer(
            qpcFrequency,
            published.ObservationWindowMilliseconds,
            published.MaximumActivePairs, published.BucketWidthMilliseconds, allocateBuckets);
    }

    internal static void BindKernelEventCallbacks(
        KernelEtwEventSource kernel,
        CpuResidencyAggregationBuffer buffer,
        Func<bool> shouldObserve,
        Func<int, long?> readProcessStartKey,
        Func<int, int?>? resolveProcessorId = null)
    {
        ArgumentNullException.ThrowIfNull(readProcessStartKey);
        BindKernelEventCallbacks(kernel, buffer, shouldObserve, (pid, _) => readProcessStartKey(pid), resolveProcessorId);
    }

    internal static void BindKernelEventCallbacks(
        KernelEtwEventSource kernel,
        CpuResidencyAggregationBuffer buffer,
        Func<bool> shouldObserve,
        Func<int, DateTimeOffset, long?> readProcessStartKey,
        Func<int, int?>? resolveProcessorId = null,
        Action<int, long>? boundaryObserved = null,
        Action<long, DateTimeOffset, int, int, int, int, string?, int>? observeContextSwitch = null)
    {
        ArgumentNullException.ThrowIfNull(kernel);
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentNullException.ThrowIfNull(shouldObserve);
        ArgumentNullException.ThrowIfNull(readProcessStartKey);
        var processStart = kernel.ConsumerCounter("ProcessStartGroup");
        var processEnd = kernel.ConsumerCounter("ProcessEndGroup");
        var threadStart = kernel.ConsumerCounter("ThreadStartGroup");
        var threadEnd = kernel.ConsumerCounter("ThreadEndGroup");
        var contextSwitch = kernel.ConsumerCounter("ThreadCSwitch");
        kernel.ProcessStartGroup += data =>
        {
            processStart.Receive();
            if (!shouldObserve())
            {
                processStart.Drop();
                kernel.RecordConsumerRejection("ProcessStartGroup", "generation-mismatch");
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
            processEnd.Receive();
            if (!shouldObserve())
            {
                processEnd.Drop();
                kernel.RecordConsumerRejection("ProcessEndGroup", "generation-mismatch");
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
            threadStart.Receive();
            if (shouldObserve())
            {
                buffer.ObserveThreadStart(
                    data.TimeStampQPC,
                    ToTimestamp(data.TimeStamp),
                    data.ProcessID,
                    data.ThreadID);
            }
            else
            {
                threadStart.Drop();
                kernel.RecordConsumerRejection("ThreadStartGroup", "generation-mismatch");
            }
        };
        kernel.ThreadEndGroup += data =>
        {
            threadEnd.Receive();
            if (shouldObserve())
            {
                buffer.ObserveThreadEnd(
                    data.TimeStampQPC,
                    ToTimestamp(data.TimeStamp),
                    data.ProcessID,
                    data.ThreadID);
            }
            else
            {
                threadEnd.Drop();
                kernel.RecordConsumerRejection("ThreadEndGroup", "generation-mismatch");
            }
        };
        kernel.ThreadCSwitch += data =>
        {
            contextSwitch.Receive();
            if (!shouldObserve())
            {
                contextSwitch.Drop();
                kernel.RecordConsumerRejection("ThreadCSwitch", "generation-mismatch");
                return;
            }
            var processorId = resolveProcessorId is null ? data.ProcessorNumber : resolveProcessorId(data.ProcessorNumber);
            if (processorId is { } logicalProcessorId)
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
            else
            {
                contextSwitch.Drop();
                kernel.RecordConsumerRejection("ThreadCSwitch", "unmapped-processor");
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
        var broker = CaptureBrokerSnapshot();
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
        CpuResidencyWindowCompletion? observedWindow;
        long observedBindingEpoch;
        lock (subscriptionGate)
        {
            if (stopped || paused || activeSession is { WindowActive: false })
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
            var brokerBefore = CaptureBrokerSnapshot();
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
                var aggregate = currentSession.Buffer.Snapshot(
                    currentSession.Plan.PhysicalCoreByLogicalProcessor.Keys);
                var brokerAfter = CaptureBrokerSnapshot();
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
                        BuildProcessSnapshot(currentSession.Plan, currentSession.Generation, aggregate))
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
                    if (stopped || paused || activeSession is { WindowActive: false }
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

    private bool ResetWindowContinuity(ResidencySessionState? session, CpuResidencyWindowCompletion? window)
    {
        lock (subscriptionGate)
        {
            if (stopped || paused || activeSession is { WindowActive: false }
                || !ReferenceEquals(activeSession, session) || !ReferenceEquals(activeSession?.Window, window)) return false;
            session?.Buffer.ResetContinuity();
            return true;
        }
    }

    private bool Publish(
        IMetricPublicationPermit? ownerToken,
        CpuCoreResidencySnapshot? snapshot,
        bool sampled,
        ResidencySessionState? expectedSession,
        CpuResidencyWindowCompletion? expectedWindow,
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
                if (stopped || paused || activeSession is { WindowActive: false }
                    || !ReferenceEquals(activeSession?.Window, expectedWindow)
                    || bindingEpoch != expectedBindingEpoch
                    || !ReferenceEquals(Volatile.Read(ref activeSession), expectedSession))
                {
                    return;
                }

                if (expectedSession is not null)
                {
                    var broker = CaptureBrokerSnapshot();
                    if (!broker.IsRunning
                        || broker.Generation != expectedSession.Generation
                        || broker.EventsLost != expectedEventsLost
                        || broker.EventsLost != Volatile.Read(ref expectedSession.EventsLost))
                    {
                        RefreshEventLoss(expectedSession, broker);
                        expectedSession.Buffer.ResetContinuity();
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

    internal static IReadOnlyList<CpuProcessCoreResidency> BuildProcessSnapshot(
        CpuResidencyCaptureConfiguration plan,
        long sessionGeneration,
        CpuResidencyAggregationSnapshot aggregate)
    {
        var windowQpc = checked(aggregate.MeasuredThroughQpc - aggregate.WindowStartQpc);
        if (!aggregate.IsComplete || windowQpc <= 0 || aggregate.QpcFrequency <= 0)
        {
            throw new InvalidDataException("Physical-core usage requires a complete positive execution-time window.");
        }
        var physicalByLogicalId = plan.PhysicalCoreByLogicalProcessor;
        var processByInstance = new Dictionary<long, ProcessProjectionAccumulator>();
        var processes = new List<ProcessProjectionAccumulator>();
        var physicalCoreIdWorkspace = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in aggregate.Records)
        {
            if (item.ExecutionTimeQpc <= 0
                || !physicalByLogicalId.TryGetValue(item.LogicalProcessorId, out var core))
            {
                continue;
            }

            if (!processByInstance.TryGetValue(item.ProcessInstanceId, out var process))
            {
                process = new ProcessProjectionAccumulator(
                    item,
                    processes.Count,
                    physicalByLogicalId.Count);
                processByInstance.Add(item.ProcessInstanceId, process);
                processes.Add(process);
            }

            process.Add(item, core);
        }

        processes.Sort((left, right) =>
        {
            var byDuration = ToMilliseconds(right.TotalDurationQpc, aggregate.QpcFrequency)
                .CompareTo(ToMilliseconds(left.TotalDurationQpc, aggregate.QpcFrequency));
            if (byDuration != 0) return byDuration;
            var byProcessId = left.First.ProcessId.CompareTo(right.First.ProcessId);
            return byProcessId != 0 ? byProcessId : left.Order.CompareTo(right.Order);
        });

        var result = new CpuProcessCoreResidency[processes.Count];
        for (var index = 0; index < result.Length; index++)
        {
            result[index] = CreateProcessResidency(
                sessionGeneration,
                aggregate.QpcFrequency,
                windowQpc,
                processes[index],
                physicalByLogicalId,
                physicalCoreIdWorkspace);
        }
        return result;
    }

    private static CpuProcessCoreResidency CreateProcessResidency(
        long sessionGeneration,
        long qpcFrequency,
        long windowQpc,
        ProcessProjectionAccumulator process,
        IReadOnlyDictionary<int, CpuResidencyCoreAccounting> physicalByLogicalId,
        HashSet<string> physicalCoreIdWorkspace)
    {
        var first = process.First;
        var logicalProcessorGroups = process.LogicalProcessors;
        logicalProcessorGroups.Sort((left, right) =>
        {
            var byDuration = ToMilliseconds(right.DurationQpc, qpcFrequency)
                .CompareTo(ToMilliseconds(left.DurationQpc, qpcFrequency));
            if (byDuration != 0) return byDuration;
            var byProcessorId = left.LogicalProcessorId.CompareTo(right.LogicalProcessorId);
            return byProcessorId != 0 ? byProcessorId : left.Order.CompareTo(right.Order);
        });
        var logicalProcessors = new CpuLogicalProcessorResidency[logicalProcessorGroups.Count];
        for (var index = 0; index < logicalProcessors.Length; index++)
        {
            var group = logicalProcessorGroups[index];
            logicalProcessors[index] = new CpuLogicalProcessorResidency(
                group.LogicalProcessorId,
                group.Core.PhysicalCoreId,
                group.Core.CcdId,
                ToMilliseconds(group.DurationQpc, qpcFrequency),
                group.SwitchCount,
                Share(group.DurationQpc, process.TotalDurationQpc));
        }

        var physicalCoreGroups = process.PhysicalCores;
        physicalCoreGroups.Sort((left, right) =>
        {
            var byDuration = ToMilliseconds(right.DurationQpc, qpcFrequency)
                .CompareTo(ToMilliseconds(left.DurationQpc, qpcFrequency));
            if (byDuration != 0) return byDuration;
            var byCoreId = StringComparer.OrdinalIgnoreCase.Compare(
                left.Core.PhysicalCoreId,
                right.Core.PhysicalCoreId);
            return byCoreId != 0 ? byCoreId : left.Order.CompareTo(right.Order);
        });
        var physicalCores = new CpuPhysicalCoreResidency[physicalCoreGroups.Count];
        for (var index = 0; index < physicalCores.Length; index++)
        {
            var group = physicalCoreGroups[index];
            physicalCores[index] = new CpuPhysicalCoreResidency(
                group.Core.PhysicalCoreId,
                group.Core.CcdId,
                ToMilliseconds(group.DurationQpc, qpcFrequency),
                group.SwitchCount,
                Share(group.DurationQpc, process.TotalDurationQpc),
                100.0 * ((double)group.DurationQpc / windowQpc) * group.Core.LogicalProcessorShare);
        }

        var ccdById = new Dictionary<string, CcdProjectionAccumulator>(StringComparer.OrdinalIgnoreCase);
        var ccdGroups = new List<CcdProjectionAccumulator>();
        foreach (var logicalProcessor in logicalProcessors)
        {
            if (string.IsNullOrWhiteSpace(logicalProcessor.CcdId)) continue;
            if (!ccdById.TryGetValue(logicalProcessor.CcdId, out var ccd))
            {
                ccd = new CcdProjectionAccumulator(logicalProcessor.CcdId, ccdGroups.Count);
                ccdById.Add(logicalProcessor.CcdId, ccd);
                ccdGroups.Add(ccd);
            }
            ccd.Add(logicalProcessor);
        }
        ccdGroups.Sort(static (left, right) =>
        {
            var byDuration = Math.Round(right.ExecutionTimeMilliseconds, 3)
                .CompareTo(Math.Round(left.ExecutionTimeMilliseconds, 3));
            if (byDuration != 0) return byDuration;
            var byCcdId = StringComparer.OrdinalIgnoreCase.Compare(left.CcdId, right.CcdId);
            return byCcdId != 0 ? byCcdId : left.Order.CompareTo(right.Order);
        });
        var ccds = new CpuCcdResidency[ccdGroups.Count];
        for (var index = 0; index < ccds.Length; index++)
        {
            var group = ccdGroups[index];
            ccds[index] = new CpuCcdResidency(
                group.CcdId,
                Math.Round(group.ExecutionTimeMilliseconds, 3),
                group.SwitchCount,
                Math.Round(group.SharePercent, 2));
        }

        var threadGroups = process.Threads;
        threadGroups.Sort((left, right) =>
        {
            var byDuration = ToMilliseconds(right.DurationQpc, qpcFrequency)
                .CompareTo(ToMilliseconds(left.DurationQpc, qpcFrequency));
            if (byDuration != 0) return byDuration;
            var byThreadId = left.ThreadId.CompareTo(right.ThreadId);
            return byThreadId != 0 ? byThreadId : left.Order.CompareTo(right.Order);
        });
        var threads = new CpuThreadCoreResidency[threadGroups.Count];
        for (var index = 0; index < threads.Length; index++)
        {
            threads[index] = CreateThreadResidency(
                sessionGeneration,
                qpcFrequency,
                threadGroups[index],
                physicalByLogicalId,
                physicalCoreIdWorkspace);
        }

        return new CpuProcessCoreResidency(
            FormatInstanceId(sessionGeneration, first.ProcessInstanceId),
            first.ProcessId,
            first.ProcessStartKey?.ToString(CultureInfo.InvariantCulture),
            first.ProcessName,
            ToMilliseconds(process.TotalDurationQpc, qpcFrequency),
            process.SwitchCount,
            threadGroups.Count,
            ccds.FirstOrDefault()?.CcdId,
            physicalCores.FirstOrDefault()?.PhysicalCoreId,
            logicalProcessors.FirstOrDefault()?.LogicalProcessorId,
            ccds,
            physicalCores,
            logicalProcessors,
            threads);
    }

    private static CpuThreadCoreResidency CreateThreadResidency(
        long sessionGeneration,
        long qpcFrequency,
        ThreadProjectionAccumulator threadGroup,
        IReadOnlyDictionary<int, CpuResidencyCoreAccounting> physicalByLogicalId,
        HashSet<string> physicalCoreIdWorkspace)
    {
        var primaryProcessorId = int.MaxValue;
        var primaryDurationQpc = long.MinValue;
        for (var index = 0; index < threadGroup.ProcessorCount; index++)
        {
            var processor = threadGroup.ProcessorDurations[index];
            if (processor.DurationQpc > primaryDurationQpc
                || processor.DurationQpc == primaryDurationQpc
                    && processor.LogicalProcessorId < primaryProcessorId)
            {
                primaryProcessorId = processor.LogicalProcessorId;
                primaryDurationQpc = processor.DurationQpc;
            }
        }
        var core = physicalByLogicalId[primaryProcessorId];
        physicalCoreIdWorkspace.Clear();
        for (var index = 0; index < threadGroup.ProcessorCount; index++)
        {
            physicalCoreIdWorkspace.Add(threadGroup.ProcessorDurations[index].PhysicalCoreId!);
        }

        var physicalCoreIds = new string[physicalCoreIdWorkspace.Count];
        physicalCoreIdWorkspace.CopyTo(physicalCoreIds);
        Array.Sort(physicalCoreIds, StringComparer.OrdinalIgnoreCase);
        return new CpuThreadCoreResidency(
            FormatInstanceId(sessionGeneration, threadGroup.ThreadInstanceId),
            threadGroup.ThreadId,
            ToMilliseconds(threadGroup.DurationQpc, qpcFrequency),
            threadGroup.SwitchCount,
            string.IsNullOrWhiteSpace(core.CcdId) ? null : core.CcdId,
            core.PhysicalCoreId,
            primaryProcessorId,
            physicalCoreIds);
    }

    private sealed class ProcessProjectionAccumulator(
        CpuExecutionTimeAggregate first,
        int order,
        int threadProcessorCapacity)
    {
        public CpuExecutionTimeAggregate First { get; } = first;
        public int Order { get; } = order;
        public long TotalDurationQpc;
        public int SwitchCount;
        private int ThreadProcessorCapacity { get; } = threadProcessorCapacity;
        public Dictionary<int, LogicalProcessorProjectionAccumulator> LogicalProcessorById { get; } = [];
        public List<LogicalProcessorProjectionAccumulator> LogicalProcessors { get; } = [];
        public Dictionary<CpuResidencyCoreAccounting, PhysicalCoreProjectionAccumulator> PhysicalCoreByIdentity { get; } = [];
        public List<PhysicalCoreProjectionAccumulator> PhysicalCores { get; } = [];
        public Dictionary<long, ThreadProjectionAccumulator> ThreadByInstance { get; } = [];
        public List<ThreadProjectionAccumulator> Threads { get; } = [];

        public void Add(CpuExecutionTimeAggregate item, CpuResidencyCoreAccounting core)
        {
            TotalDurationQpc = checked(TotalDurationQpc + item.ExecutionTimeQpc);
            SwitchCount = checked(SwitchCount + item.SwitchCount);

            if (!LogicalProcessorById.TryGetValue(item.LogicalProcessorId, out var logicalProcessor))
            {
                logicalProcessor = new LogicalProcessorProjectionAccumulator(
                    item.LogicalProcessorId, core, LogicalProcessors.Count);
                LogicalProcessorById.Add(item.LogicalProcessorId, logicalProcessor);
                LogicalProcessors.Add(logicalProcessor);
            }
            logicalProcessor.Add(item);

            if (!PhysicalCoreByIdentity.TryGetValue(core, out var physicalCore))
            {
                physicalCore = new PhysicalCoreProjectionAccumulator(core, PhysicalCores.Count);
                PhysicalCoreByIdentity.Add(core, physicalCore);
                PhysicalCores.Add(physicalCore);
            }
            physicalCore.Add(item);

            if (!ThreadByInstance.TryGetValue(item.ThreadInstanceId, out var thread))
            {
                thread = new ThreadProjectionAccumulator(item, Threads.Count, ThreadProcessorCapacity);
                ThreadByInstance.Add(item.ThreadInstanceId, thread);
                Threads.Add(thread);
            }
            thread.Add(item, core);
        }
    }

    private sealed class LogicalProcessorProjectionAccumulator(
        int logicalProcessorId,
        CpuResidencyCoreAccounting core,
        int order)
    {
        public int LogicalProcessorId { get; } = logicalProcessorId;
        public CpuResidencyCoreAccounting Core { get; } = core;
        public int Order { get; } = order;
        public long DurationQpc;
        public int SwitchCount;
        public void Add(CpuExecutionTimeAggregate item)
        {
            DurationQpc = checked(DurationQpc + item.ExecutionTimeQpc);
            SwitchCount = checked(SwitchCount + item.SwitchCount);
        }
    }

    private sealed class PhysicalCoreProjectionAccumulator(CpuResidencyCoreAccounting core, int order)
    {
        public CpuResidencyCoreAccounting Core { get; } = core;
        public int Order { get; } = order;
        public long DurationQpc;
        public int SwitchCount;
        public void Add(CpuExecutionTimeAggregate item)
        {
            DurationQpc = checked(DurationQpc + item.ExecutionTimeQpc);
            SwitchCount = checked(SwitchCount + item.SwitchCount);
        }
    }

    private sealed class ThreadProjectionAccumulator(
        CpuExecutionTimeAggregate first,
        int order,
        int processorCapacity)
    {
        public long ThreadInstanceId { get; } = first.ThreadInstanceId;
        public int ThreadId { get; } = first.ThreadId;
        public int Order { get; } = order;
        public long DurationQpc;
        public int SwitchCount;
        private readonly int _processorCapacity = processorCapacity;
        private ThreadProcessorDuration[] processorDurations
            = new ThreadProcessorDuration[Math.Min(4, processorCapacity)];
        public ThreadProcessorDuration[] ProcessorDurations => processorDurations;
        public int ProcessorCount { get; private set; }
        public void Add(CpuExecutionTimeAggregate item, CpuResidencyCoreAccounting core)
        {
            DurationQpc = checked(DurationQpc + item.ExecutionTimeQpc);
            SwitchCount = checked(SwitchCount + item.SwitchCount);
            for (var index = 0; index < ProcessorCount; index++)
            {
                if (ProcessorDurations[index].LogicalProcessorId != item.LogicalProcessorId) continue;
                ProcessorDurations[index].DurationQpc = checked(
                    ProcessorDurations[index].DurationQpc + item.ExecutionTimeQpc);
                return;
            }

            if (ProcessorCount == ProcessorDurations.Length)
            {
                var nextCapacity = ProcessorDurations.Length == 0
                    ? 1
                    : Math.Min(_processorCapacity, ProcessorDurations.Length * 2);
                if (nextCapacity <= ProcessorCount)
                {
                    throw new InvalidOperationException("A thread observed more logical processors than the capture topology contains.");
                }
                Array.Resize(ref processorDurations, nextCapacity);
            }

            processorDurations[ProcessorCount++] = new ThreadProcessorDuration(
                item.LogicalProcessorId,
                item.ExecutionTimeQpc,
                core.PhysicalCoreId);
        }
    }

    private struct ThreadProcessorDuration(int logicalProcessorId, long durationQpc, string physicalCoreId)
    {
        public int LogicalProcessorId = logicalProcessorId;
        public long DurationQpc = durationQpc;
        public string? PhysicalCoreId = physicalCoreId;
    }

    private sealed class CcdProjectionAccumulator(string ccdId, int order)
    {
        public string CcdId { get; } = ccdId;
        public int Order { get; } = order;
        public double ExecutionTimeMilliseconds;
        public int SwitchCount;
        public double SharePercent;
        public void Add(CpuLogicalProcessorResidency item)
        {
            ExecutionTimeMilliseconds += item.ExecutionTimeMilliseconds;
            SwitchCount = checked(SwitchCount + item.SwitchCount);
            SharePercent += item.SharePercent;
        }
    }

    public void Stop(string idleMessage = "ETW CPU attribution stopped after its demand was withdrawn.")
    {
        IKernelEtwSubscription? subscriptionToRelease = null;
        IKernelEtwSubscription? windowToRelease;
        ResidencySessionState? sessionToClear = null;
        lock (subscriptionGate)
        {
            if (stopped) { ClearRetiredBuffersCore(); return; }

            windowToRelease = DetachContextSwitchCore();
            (subscriptionToRelease, sessionToClear) = DetachSessionCore();
            state = "Idle";
            message = idleMessage;
        }

        try { try { windowToRelease?.Dispose(); } finally { subscriptionToRelease?.Dispose(); } }
        finally { ClearRetiredBuffers(); }
    }

    private (IKernelEtwSubscription? Subscription, ResidencySessionState? Session) DetachSessionCore()
    {
        ClearDiagnosticSource();
        bindingEpoch = checked(bindingEpoch + 1);
        var subscription = kernelSubscription;
        kernelSubscription = null;
        var session = Volatile.Read(ref activeSession);
        if (session is not null) { session.Window.Invalidate(); retiredBuffers.Add(session.Buffer); }
        Volatile.Write(ref activeSession, null);
        bucketStorage = null;
        return (subscription, session);
    }

    private void ClearRetiredBuffers()
    {
        lock (subscriptionGate) ClearRetiredBuffersCore();
    }
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
    internal static void SeedInventory(CpuResidencyAggregationBuffer.InventorySeed inventory, Func<bool> shouldSeed) => SeedExistingProcesses(inventory, shouldSeed);
    private static void SeedExistingProcesses(CpuResidencyAggregationBuffer.InventorySeed inventory, Func<bool> shouldSeed)
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
        CpuResidencyAggregationBuffer buffer)
    {
        public long Generation { get; } = generation;
        public DateTimeOffset StartedAt { get; } = startedAt;
        public long BindingEpoch { get; } = bindingEpoch;
        public CpuResidencyCaptureConfiguration Plan { get; } = plan;
        public CpuResidencyAggregationBuffer Buffer { get; } = buffer;
        public KernelEtwEventSource? EventSource;
        public EtwEventDropCounter? ContextSwitchCounter;
        public CpuResidencyWindowCompletion Window { get; set; } = new(plan.PhysicalCoreByLogicalProcessor.Keys,
            checked(Stopwatch.Frequency * (long)plan.ObservationWindowMilliseconds / 1000));
        public CpuResidencyWindowCompletion CreateWindow() => new(Plan.PhysicalCoreByLogicalProcessor.Keys,
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
        CpuResidencyWindowCompletion window;
        lock (subscriptionGate)
        {
            session = activeSession;
            if (session is null || paused || !session.WindowActive) return false;
            window = session.Window;
        }
        if (!IsRunning) return false;
        try
        {
            if (!await window.Boundaries.WaitAsync(boundaryBudget, cancellationToken)) return false;
            if (!await window.Complete.WaitAsync(session.Plan.ObservationWindow + deliveryGrace, cancellationToken)) return false;
            lock (subscriptionGate)
                return !stopped && !paused && IsCurrentSessionCore(session)
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
        IKernelEtwSubscription? lease; ResidencySessionState? session;
        IKernelEtwSubscription? window;
        lock (subscriptionGate)
        {
            if (stopped) { ClearRetiredBuffersCore(); return; }
            stopped = true; DisposeDiagnosticRequests(); window = DetachContextSwitchCore(); (lease, session) = DetachSessionCore(); state = "Stopped";
        }
        try { try { window?.Dispose(); } finally { lease?.Dispose(); } } finally { ClearRetiredBuffers(); }
    }
}
#pragma warning restore CS0618
