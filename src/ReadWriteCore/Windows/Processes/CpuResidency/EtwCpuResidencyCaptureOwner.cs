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
public sealed class EtwCpuResidencyCaptureOwner : IDisposable
{
    private const KernelTraceEventParser.Keywords RequiredKeywords = KernelTraceEventParser.Keywords.Process | KernelTraceEventParser.Keywords.Thread | KernelTraceEventParser.Keywords.ContextSwitch;

    private readonly IKernelEtwSessionBroker kernelEtwBroker;
    private readonly Func<CpuResidencyCaptureConfiguration> readConfiguration;
    private readonly KernelEtwDiagnostics logger;
    private readonly Action<CpuResidencyAggregationBuffer.InventorySeed, Func<bool>> seedProcessInventory;
    private readonly Action? published;
    private readonly object subscriptionGate = new(), snapshotGate = new();
    public SamplingPublicationSignal Publications { get; } = new();
    private IKernelEtwSubscription? kernelSubscription;
    private ResidencySessionState? activeSession;
    private long bindingEpoch;
    private long revision;
    public long Revision => Interlocked.Read(ref revision);
    private CpuCoreResidencySnapshot? cachedSnapshot;
    private bool stopped, paused;
    private volatile string state = "Idle", message = "ETW CPU execution-time attribution starts on explicit demand.";
    public string State => state;
    public string Message => message;
    public EtwCpuResidencyCaptureOwner(IKernelEtwSessionBroker broker,
        Func<CpuResidencyCaptureConfiguration> configuration, KernelEtwDiagnostics? diagnostics = null)
        : this(broker, configuration, diagnostics ?? new(), SeedExistingProcesses) { }
    internal EtwCpuResidencyCaptureOwner(IKernelEtwSessionBroker broker, Func<CpuResidencyCaptureConfiguration> configuration,
        KernelEtwDiagnostics diagnostics, Action<CpuResidencyAggregationBuffer.InventorySeed, Func<bool>> seed, Action? published = null)
    {
        ArgumentNullException.ThrowIfNull(broker); ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(diagnostics); ArgumentNullException.ThrowIfNull(seed);
        kernelEtwBroker = broker; readConfiguration = configuration; logger = diagnostics;
        seedProcessInventory = seed; this.published = published;
    }
    public CpuCoreResidencySnapshot? Read()
    {
        lock (snapshotGate)
        {
            return cachedSnapshot;
        }
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

            var broker = (Volatile.Read(ref kernelSubscription)?.GetSnapshot() ?? kernelEtwBroker.GetSnapshot());
            return broker.IsRunning
                && broker.Generation == session.Generation
                && broker.EventsLost == Volatile.Read(ref session.EventsLost);
        }
    }

    /// <summary>External composition's explicit gate. This retains the original freeze/start epoch boundary;
    /// it reads no product policy and creates no worker. Stop alone remains restartable demand withdrawal.</summary>
    public void SetCaptureEnabled(bool enabled, string idleMessage = "CPU residency capture is paused by its composition.")
    {
        IKernelEtwSubscription? lease = null; ResidencySessionState? session = null;
        lock (subscriptionGate)
        {
            if (stopped) return;
            paused = !enabled;
            if (paused)
            {
                (lease, session) = DetachSessionCore(); state = "Idle"; message = idleMessage;
            }
        }
        try { lease?.Dispose(); } finally { session?.Buffer.Clear(); }
    }
    public bool HasSessionLease
    {
        get
        {
            lock (subscriptionGate)
            {
                return kernelSubscription is not null
                    || activeSession is not null;
            }
        }
    }

    public void EnsureStarted()
    {
        long expectedBindingEpoch;
        lock (subscriptionGate)
        {
            if (stopped || paused)
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

        // The sampling loop is the only starter. Broker reconciliation can bind
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
                if (!stopped && !paused
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
            CreateAggregationBuffer(plan, Stopwatch.Frequency));
        var processorIds = plan.PhysicalCoreByLogicalProcessor.Keys.Order().ToArray();
        BindKernelEventCallbacks(
            kernel,
            session.Buffer,
            () => IsCurrentSession(session, expectedBindingEpoch),
            ReadEventCreation,
            index => ResolveEventLogicalProcessorId(index, processorIds));

        lock (subscriptionGate)
        {
            if (bindingEpoch != expectedBindingEpoch)
            {
                return;
            }

            Volatile.Write(ref activeSession, session);
            state = "Warming";
            message = $"ETW CPU 执行时长归因正在形成最近 {plan.ObservationWindow.TotalSeconds:0.###} 秒完整窗口。";
        }

        StartProcessInventorySeed(session);
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
            Volatile.Write(ref session.EventsLost, broker.EventsLost);
            SetCachedSnapshot(null);
            state = "Warming";
            message = WarmingMessage(session);
        }
        StartProcessInventorySeed(session);
    }

    internal static CpuResidencyAggregationBuffer CreateAggregationBuffer(
        CpuResidencyCaptureConfiguration plan,
        long qpcFrequency)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var published = plan.RequirePublished();
        return new CpuResidencyAggregationBuffer(
            qpcFrequency,
            published.ObservationWindowMilliseconds,
            published.MaximumClosedSlices);
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
        Func<int, int?>? resolveProcessorId = null)
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
        kernel.ThreadCSwitch += data =>
        {
            var processorId = resolveProcessorId is null ? data.ProcessorNumber : resolveProcessorId(data.ProcessorNumber);
            if (shouldObserve() && processorId is { } logicalProcessorId)
            {
                buffer.ObserveContextSwitch(
                    data.TimeStampQPC,
                    ToTimestamp(data.TimeStamp),
                    logicalProcessorId,
                    data.OldProcessID,
                    data.OldThreadID,
                    data.NewProcessID,
                    data.NewProcessName,
                    data.NewThreadID);
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
        var broker = (Volatile.Read(ref kernelSubscription)?.GetSnapshot() ?? kernelEtwBroker.GetSnapshot());
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
        long observedBindingEpoch;
        lock (subscriptionGate)
        {
            if (stopped)
            {
                return false;
            }

            observedSession = Volatile.Read(ref activeSession);
            observedBindingEpoch = bindingEpoch;
        }

        try
        {
            var now = DateTimeOffset.UtcNow;
            var brokerBefore = (Volatile.Read(ref kernelSubscription)?.GetSnapshot() ?? kernelEtwBroker.GetSnapshot());
            if (observedSession is not null) RefreshEventLoss(observedSession, brokerBefore);
            CpuCoreResidencySnapshot? snapshot;
            string nextState;
            string nextMessage;
            var sampled = observedSession is not null
                && brokerBefore.IsRunning
                && brokerBefore.Generation == observedSession.Generation
                && brokerBefore.EventsLost == Volatile.Read(ref observedSession.EventsLost);
            if (!sampled)
            {
                observedSession?.Buffer.ResetContinuity();
                snapshot = null;
                (nextState, nextMessage) = BrokerDiagnostic(brokerBefore, observedSession);
            }
            else
            {
                var currentSession = observedSession
                    ?? throw new InvalidOperationException("The running ETW session state is missing.");
                var aggregate = currentSession.Buffer.Snapshot(
                    currentSession.Plan.PhysicalCoreByLogicalProcessor.Keys);
                var brokerAfter = (Volatile.Read(ref kernelSubscription)?.GetSnapshot() ?? kernelEtwBroker.GetSnapshot());
                if (!brokerAfter.IsRunning
                    || brokerAfter.Generation != currentSession.Generation
                    || brokerAfter.EventsLost != brokerBefore.EventsLost
                    || brokerAfter.EventsLost != Volatile.Read(ref currentSession.EventsLost))
                {
                    RefreshEventLoss(currentSession, brokerAfter);
                    currentSession.Buffer.ResetContinuity();
                    snapshot = null;
                    (nextState, nextMessage) = BrokerDiagnostic(brokerAfter, currentSession);
                    sampled = false;
                }
                else if (!aggregate.IsComplete)
                {
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
                observedBindingEpoch,
                brokerBefore.EventsLost,
                nextState,
                nextMessage,
                captureCommitted);
        }
        catch (Exception ex)
        {
            _ = TryPublish(ownerToken, () =>
            {
                lock (subscriptionGate)
                {
                    if (stopped
                        || bindingEpoch != observedBindingEpoch
                        || !ReferenceEquals(Volatile.Read(ref activeSession), observedSession))
                    {
                        return;
                    }

                    logger.LogWarning(ex, "Failed to build ETW CPU execution-time snapshot.");
                    state = "Unavailable";
                    message = $"ETW CPU 执行时长快照生成失败：{ex.Message}";
                    SetCachedSnapshot(null);
                    NotifyCaptureCommitted(captureCommitted, null, sourceQuerySucceeded: false);
                    Publications.Publish(["process.cpu.residency"]); published?.Invoke();
                }
            });
            return false;
        }
    }

    private bool Publish(
        IMetricPublicationPermit? ownerToken,
        CpuCoreResidencySnapshot? snapshot,
        bool sampled,
        ResidencySessionState? expectedSession,
        long expectedBindingEpoch,
        int expectedEventsLost,
        string nextState,
        string nextMessage,
        Action<CpuCoreResidencySnapshot?, bool>? captureCommitted)
    {
        var committed = false;
        var committedAsSampled = sampled;
        var accepted = TryPublish(ownerToken, () =>
        {
            lock (subscriptionGate)
            {
                if (stopped
                    || bindingEpoch != expectedBindingEpoch
                    || !ReferenceEquals(Volatile.Read(ref activeSession), expectedSession))
                {
                    return;
                }

                if (expectedSession is not null)
                {
                    var broker = (Volatile.Read(ref kernelSubscription)?.GetSnapshot() ?? kernelEtwBroker.GetSnapshot());
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
                    }
                }

                SetCachedSnapshot(snapshot);
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

    private void SetCachedSnapshot(CpuCoreResidencySnapshot? snapshot)
    {
        lock (snapshotGate)
        {
            cachedSnapshot = snapshot;
            Interlocked.Increment(ref revision);
        }
    }

    private static (string State, string Message) BrokerDiagnostic(
        KernelEtwSessionSnapshot broker, ResidencySessionState? session)
        => broker.IsRunning && session is not null && broker.Generation == session.Generation
            ? ("Warming", WarmingMessage(session))
            : (broker.State, broker.Message);

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
        return aggregate.Records
            .Where(item => item.ExecutionTimeQpc > 0
                && physicalByLogicalId.ContainsKey(item.LogicalProcessorId))
            .GroupBy(static item => item.ProcessInstanceId)
            .Select(group => CreateProcessResidency(
                sessionGeneration,
                aggregate.QpcFrequency,
                windowQpc,
                group,
                physicalByLogicalId))
            .OrderByDescending(static process => process.ExecutionTimeMilliseconds)
            .ThenBy(static process => process.ProcessId)
            .ToArray();
    }

    private static CpuProcessCoreResidency CreateProcessResidency(
        long sessionGeneration,
        long qpcFrequency,
        long windowQpc,
        IGrouping<long, CpuExecutionTimeAggregate> group,
        IReadOnlyDictionary<int, CpuResidencyCoreAccounting> physicalByLogicalId)
    {
        var records = group.ToArray();
        var first = records[0];
        var totalDurationQpc = records.Sum(static item => item.ExecutionTimeQpc);
        var totalSwitches = records.Sum(static item => item.SwitchCount);
        var logicalProcessors = records
            .GroupBy(static item => item.LogicalProcessorId)
            .Select(logicalGroup =>
            {
                var core = physicalByLogicalId[logicalGroup.Key];
                var durationQpc = logicalGroup.Sum(static item => item.ExecutionTimeQpc);
                return new CpuLogicalProcessorResidency(
                    logicalGroup.Key,
                    core.PhysicalCoreId,
                    core.CcdId,
                    ToMilliseconds(durationQpc, qpcFrequency),
                    logicalGroup.Sum(static item => item.SwitchCount),
                    Share(durationQpc, totalDurationQpc));
            })
            .OrderByDescending(static item => item.ExecutionTimeMilliseconds)
            .ThenBy(static item => item.LogicalProcessorId)
            .ToArray();
        var physicalCores = records
            .GroupBy(item => physicalByLogicalId[item.LogicalProcessorId])
            .Select(coreGroup =>
            {
                var core = coreGroup.Key;
                var durationQpc = coreGroup.Sum(static item => item.ExecutionTimeQpc);
                return new CpuPhysicalCoreResidency(
                    core.PhysicalCoreId,
                    core.CcdId,
                    ToMilliseconds(durationQpc, qpcFrequency),
                    coreGroup.Sum(static item => item.SwitchCount),
                    Share(durationQpc, totalDurationQpc),
                    100.0 * ((double)durationQpc / windowQpc) * core.LogicalProcessorShare);
            })
            .OrderByDescending(static item => item.ExecutionTimeMilliseconds)
            .ThenBy(static item => item.PhysicalCoreId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var ccds = logicalProcessors
            .Where(static item => !string.IsNullOrWhiteSpace(item.CcdId))
            .GroupBy(static item => item.CcdId, StringComparer.OrdinalIgnoreCase)
            .Select(ccdGroup => new CpuCcdResidency(
                ccdGroup.Key,
                Math.Round(ccdGroup.Sum(static item => item.ExecutionTimeMilliseconds), 3),
                ccdGroup.Sum(static item => item.SwitchCount),
                Math.Round(ccdGroup.Sum(static item => item.SharePercent), 2)))
            .OrderByDescending(static item => item.ExecutionTimeMilliseconds)
            .ThenBy(static item => item.CcdId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var threadGroups = records.GroupBy(static item => item.ThreadInstanceId).ToArray();
        var threads = threadGroups
            .Select(threadGroup => CreateThreadResidency(
                sessionGeneration,
                qpcFrequency,
                threadGroup,
                physicalByLogicalId))
            .OrderByDescending(static item => item.ExecutionTimeMilliseconds)
            .ThenBy(static item => item.ThreadId)
            .ToArray();

        return new CpuProcessCoreResidency(
            FormatInstanceId(sessionGeneration, first.ProcessInstanceId),
            first.ProcessId,
            first.ProcessStartKey?.ToString(CultureInfo.InvariantCulture),
            first.ProcessName,
            ToMilliseconds(totalDurationQpc, qpcFrequency),
            totalSwitches,
            threadGroups.Length,
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
        IGrouping<long, CpuExecutionTimeAggregate> threadGroup,
        IReadOnlyDictionary<int, CpuResidencyCoreAccounting> physicalByLogicalId)
    {
        var records = threadGroup.ToArray();
        var primary = records
            .GroupBy(static item => item.LogicalProcessorId)
            .OrderByDescending(static group => group.Sum(item => item.ExecutionTimeQpc))
            .ThenBy(static group => group.Key)
            .First();
        var core = physicalByLogicalId[primary.Key];
        var physicalCoreIds = records
            .Select(item => physicalByLogicalId[item.LogicalProcessorId].PhysicalCoreId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new CpuThreadCoreResidency(
            FormatInstanceId(sessionGeneration, threadGroup.Key),
            records[0].ThreadId,
            ToMilliseconds(records.Sum(static item => item.ExecutionTimeQpc), qpcFrequency),
            records.Sum(static item => item.SwitchCount),
            string.IsNullOrWhiteSpace(core.CcdId) ? null : core.CcdId,
            core.PhysicalCoreId,
            primary.Key,
            physicalCoreIds);
    }

    public void Stop(string idleMessage = "ETW CPU attribution stopped after its demand was withdrawn.")
    {
        IKernelEtwSubscription? subscriptionToRelease = null;
        ResidencySessionState? sessionToClear = null;
        lock (subscriptionGate)
        {
            if (stopped)
            {
                return;
            }

            (subscriptionToRelease, sessionToClear) = DetachSessionCore();
            state = "Idle";
            message = idleMessage;
        }

        try { subscriptionToRelease?.Dispose(); }
        finally { sessionToClear?.Buffer.Clear(); }
    }

    private (IKernelEtwSubscription? Subscription, ResidencySessionState? Session) DetachSessionCore()
    {
        bindingEpoch = checked(bindingEpoch + 1);
        var subscription = kernelSubscription;
        kernelSubscription = null;
        var session = Volatile.Read(ref activeSession);
        Volatile.Write(ref activeSession, null);
        SetCachedSnapshot(null);
        return (subscription, session);
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
        public int EventsLost;
        public Task? InventorySeedTask;
    }

    private static bool TryPublish(IMetricPublicationPermit? permit, Action commit)
    { if (permit is not null) return permit.TryPublish(commit); commit(); return true; }
    public void Dispose()
    {
        IKernelEtwSubscription? lease; ResidencySessionState? session;
        lock (subscriptionGate)
        {
            if (stopped) return;
            stopped = true; (lease, session) = DetachSessionCore(); state = "Stopped";
        }
        try { lease?.Dispose(); } finally { session?.Buffer.Clear(); }
    }
}
#pragma warning restore CS0618
