using System.Diagnostics;
using System.Globalization;
using Microsoft.Diagnostics.Tracing.Parsers;
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
    private const KernelTraceEventParser.Keywords MetadataKeywords = KernelTraceEventParser.Keywords.Process | KernelTraceEventParser.Keywords.Thread;

    private readonly IKernelEtwSessionBroker kernelEtwBroker;
    private readonly Func<CpuResidencyCaptureConfiguration> readConfiguration;
    private readonly KernelEtwDiagnostics logger;
    private readonly Action? published;
    private readonly Action<InventorySeed, Func<bool>> seedProcessInventory;
    private readonly bool hasExplicitInventorySource;
    private readonly object subscriptionGate = new(), snapshotGate = new();
    public SamplingPublicationSignal Publications { get; } = new();
    private IKernelEtwSubscription? kernelSubscription;
    private ResidencySessionState? activeSession;
    private long bindingEpoch;
    private long revision;
    public long Revision => Interlocked.Read(ref revision);
    private CpuCoreResidencyObservation currentObservation = new CpuCoreResidencyUnavailable(CpuCoreResidencyUnavailableReasons.NotRequested);
    private CpuCoreResidencySnapshot? cachedSnapshot => currentObservation as CpuCoreResidencySnapshot;
    private bool stopped, acquiringMetadata;
    private volatile string state = "Idle", message = "ETW CPU execution-time attribution starts on explicit demand.";
    public string State => state;
    public string Message => message;
    public EtwCpuResidencyCaptureOwner(IKernelEtwSessionBroker broker,
        Func<CpuResidencyCaptureConfiguration> configuration, KernelEtwDiagnostics? diagnostics = null)
        : this(broker, configuration, diagnostics ?? new(), published: null) { }
    internal EtwCpuResidencyCaptureOwner(IKernelEtwSessionBroker broker,
        Func<CpuResidencyCaptureConfiguration> configuration, KernelEtwDiagnostics diagnostics, Action? published,
        Action<InventorySeed, Func<bool>>? seedInventory = null)
    {
        ArgumentNullException.ThrowIfNull(broker); ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(diagnostics);
        kernelEtwBroker = broker; readConfiguration = configuration; logger = diagnostics;
        this.published = published;
        seedProcessInventory = seedInventory ?? SeedNativeInventory;
        hasExplicitInventorySource = seedInventory is not null;
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
                session?.Native.GetDiagnostics());
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
        get { lock (subscriptionGate) return activeSession is { WindowActive: true } && durationSubscription is not null; }
    }

    /// <summary>Opens continuous observation coverage and its duration-keyword hold on the run's metadata session.
    /// The window is split into ceil(window / cadence) slots (1..2047), so each read slides by about one cadence.</summary>
    public void BeginWindow(TimeSpan cadence)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(cadence, TimeSpan.Zero);
        BeginWindow((TimeSpan?)cadence);
    }

    private void BeginWindow(TimeSpan? cadence)
    {
        lock (durationResourceGate)
        lock (subscriptionGate) BeginWindowCore(cadence);
        SynchronizeDurationHold();
    }

    // A finite window: it starts when every processor is known and stops growing
    // once it has covered the observation window. Every exit releases the native
    // window's pairs and duration keywords, while the enclosing run continues to
    // own process/thread identity.
    internal async Task<T> RunWindowAsync<T>(Func<Task<T>> sample)
    {
        try
        {
            BeginWindow(null);
            return await sample().ConfigureAwait(false);
        }
        finally { PauseWindow("CPU residency window has returned; aggregation is paused until the next window."); }
    }

    // Null cadence = finite window (0 slots).
    private static int SlotsPerWindow(CpuResidencyCaptureConfiguration plan, TimeSpan? cadence)
    {
        if (cadence is not { } value) return 0;
        var slots = (plan.ObservationWindow.Ticks + value.Ticks - 1) / value.Ticks;
        return (int)Math.Clamp(slots, 1, KernelEtwNativeReaders.MaximumSlotsPerWindow);
    }

    private void BeginWindowCore(TimeSpan? cadence)
    {
        if (stopped) return;
        windowRequested = true;
        windowCadence = cadence;
        if (activeSession is not { } session) return;
        var slots = SlotsPerWindow(session.Plan, cadence);
        if (session.WindowActive && session.Native.SlotsPerWindow == slots) return;
        session.Window = new object();
        session.Native.BeginWindow(slots);

        session.UnavailableReason = CpuCoreResidencyUnavailableReasons.WindowWarming;
        state = "Warming";
        message = WarmingMessage(session);
        session.WindowActive = true;
        ConfigureAdmission(session);
    }

    /// <summary>Stops finite CSwitch aggregation; process/thread metadata and the resource holder remain live.</summary>
    public void PauseWindow(string idleMessage = "CPU residency is waiting for its next finite observation window.")
    {
        IKernelEtwSubscription? release;
        lock (durationResourceGate)
        lock (subscriptionGate)
        {
            PauseWindowCore(idleMessage);
            release = DetachDurationHoldCore();
        }
        release?.Dispose();
    }

    private void PauseWindowCore(string idleMessage)
    {
        if (stopped) return;
        windowRequested = false;
        if (activeSession is { } session)
        {
            session.WindowActive = false;
            session.Native.SetEnabled(false);
            session.Window = new object();
            ConfigureAdmission(session);
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
                    || activeSession is not null;
            }
        }
    }

    public void EnsureStarted()
    {
        EnsureMetadataStarted();
        SynchronizeDurationHold();
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

            if (acquiringMetadata) return;
            acquiringMetadata = true;
            expectedBindingEpoch = checked(++bindingEpoch);
        }

        // The containing run is the starter. Broker reconciliation can bind
        // this reader on another thread, so do not hold its subscription lock here.
        try
        {
            var created = kernelEtwBroker.Acquire(new KernelEtwSubscriptionRequest(
                "cpu-core-residency",
                MetadataKeywords,
                (kernel, context) => BindKernelEvents(kernel, context, expectedBindingEpoch))
                { NativeOutputs = KernelEtwNativeOutputs.Residency });
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
                Volatile.Write(ref activeSession, null);
                state = "Unavailable";
                message = $"ETW CPU 执行时长归因启动失败：{ex.Message}";
                logger.LogWarning(ex, "Failed to start ETW CPU execution-time reader.");
            }
        }
        finally
        {
            lock (subscriptionGate)
                if (bindingEpoch == expectedBindingEpoch) acquiringMetadata = false;
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
        var native = kernel.NativeSession
            ?? throw new InvalidOperationException("CPU residency requires a native kernel ETW session.");
        var session = new ResidencySessionState(context.Generation,
            expectedBindingEpoch, plan, native, kernel);
        var processorIds = plan.PhysicalCoreByLogicalProcessor.Keys.Order().ToArray();
        BindDiagnosticSource(session);
        ResidencySessionState? previous;
        lock (subscriptionGate)
        {
            if (bindingEpoch != expectedBindingEpoch)
            {
                return;
            }

            previous = Volatile.Read(ref activeSession);
            if (previous is not null) session.UnavailableReason = CpuCoreResidencyUnavailableReasons.SessionRestarted;
            Volatile.Write(ref activeSession, null);
            session.WindowActive = windowRequested;
            session.Native.Configure(processorIds, session.WindowActive && durationSubscription is not null,
                SlotsPerWindow(plan, windowCadence));
            ConfigureAdmission(session);
            _ = kernel.ConsumerCounter("ProcessStartGroup");
            _ = kernel.ConsumerCounter("ProcessEndGroup");
            _ = kernel.ConsumerCounter("ThreadStartGroup");
            _ = kernel.ConsumerCounter("ThreadEndGroup");
            _ = kernel.ConsumerCounter("ThreadCSwitch");
            foreach (var kind in new[] { "ProcessStartGroup", "ProcessEndGroup", "ThreadStartGroup", "ThreadEndGroup" })
                _ = kernel.ConsumerRejectionCounter(kind, "generation-mismatch");
            foreach (var reason in new[] { "generation-mismatch", "unmapped-processor", "outside-window", "before-window" })
                _ = kernel.ConsumerRejectionCounter("ThreadCSwitch", reason);
            Volatile.Write(ref activeSession, session);
            state = "Warming";
            message = $"ETW CPU 执行时长归因正在形成最近 {plan.ObservationWindow.TotalSeconds:0.###} 秒完整窗口。";
        }

        DrainInventorySeed(previous);
        StartNativeProcessInventorySeed(session);
    }

    private void RefreshEventLoss(ResidencySessionState session, KernelEtwSessionSnapshot broker)
    {
        if (!broker.IsRunning || broker.Generation != session.Generation) return;
        lock (subscriptionGate)
        {
            if (!IsCurrentSessionCore(session) || session.EventsLost == broker.EventsLost) return;
            session.Native.RefreshEventLoss(broker.EventsLost);
            session.UnavailableReason = CpuCoreResidencyUnavailableReasons.EventsLost;
            Volatile.Write(ref session.EventsLost, broker.EventsLost);
            state = "Warming";
            message = WarmingMessage(session);
        }
        StartNativeProcessInventorySeed(session);
    }

    private bool IsCurrentSession(ResidencySessionState session, long expectedBindingEpoch)
        => Volatile.Read(ref bindingEpoch) == expectedBindingEpoch
            && ReferenceEquals(Volatile.Read(ref activeSession), session) && !session.EventSource.IsCancelled;

    private void ConfigureAdmission(ResidencySessionState session)
    {
        if (!session.EventSource.IsCancelled)
            session.EventSource.ConfigureAdmission(session.WindowActive, session.Native.WindowStartQpc);
    }

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
        => CollectAsync(ownerToken, captureCommitted, CancellationToken.None).GetAwaiter().GetResult();

    public Task<bool> CollectAsync(IMetricPublicationPermit? ownerToken = null,
        CancellationToken cancellationToken = default)
        => CollectAsync(ownerToken, null, cancellationToken);

    /// <summary>Samples the current native CPU component on the existing resource, then attempts the original permitted commit.</summary>
    public async Task<bool> CollectAsync(IMetricPublicationPermit? ownerToken,
        Action<CpuCoreResidencySnapshot?, bool>? captureCommitted, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (ownerToken is { IsActive: false }) return false;
        ResidencySessionState? observedSession;
        object? observedWindow;
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
                await currentSession.Native.SampleCurrentAsync(currentSession.Plan.ObservationWindow, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                lock (subscriptionGate)
                {
                    if (stopped || !currentSession.WindowActive
                        || !ReferenceEquals(activeSession, observedSession)
                        || !ReferenceEquals(currentSession.Window, observedWindow)
                        || bindingEpoch != observedBindingEpoch || ownerToken is { IsActive: false })
                        return false;
                }
                var nativeRead = currentSession.Native.ReadCurrent();
                var aggregate = nativeRead.Aggregate;
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
                    snapshot = nativeRead.Projected
                        ?? throw new InvalidDataException("A complete native CPU window has no matching immutable projection.");
                    nextState = "Running";
                    nextMessage = "ETW CPU 执行时长归因正在提供真实闭合执行区间。";
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { cancellationToken.ThrowIfCancellationRequested(); throw; }
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

    private bool ResetWindowContinuity(ResidencySessionState? session, object? window)
    {
        lock (subscriptionGate)
        {
            if (stopped || activeSession is { WindowActive: false }
                || !ReferenceEquals(activeSession, session) || !ReferenceEquals(activeSession?.Window, window)) return false;
            session?.ResetContinuity();
            if (session is not null) ConfigureAdmission(session);
            return true;
        }
    }

    private bool Publish(
        IMetricPublicationPermit? ownerToken,
        CpuCoreResidencySnapshot? snapshot,
        bool sampled,
        ResidencySessionState? expectedSession,
        object? expectedWindow,
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
                        ConfigureAdmission(expectedSession);
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
            && !session.EventSource.IsCancelled
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
        IKernelEtwSubscription? subscriptionToRelease, durationToRelease;
        ResidencySessionState? sessionToClear;
        lock (durationResourceGate)
        lock (subscriptionGate)
        {
            if (stopped) return;
            windowRequested = false;
            durationToRelease = DetachDurationHoldCore();
            (subscriptionToRelease, sessionToClear) = DetachSessionCore();
            state = "Idle";
            message = idleMessage;
        }
        try { DrainInventorySeed(sessionToClear); }
        finally { try { durationToRelease?.Dispose(); } finally { subscriptionToRelease?.Dispose(); } }
    }

    private (IKernelEtwSubscription? Subscription, ResidencySessionState? Session) DetachSessionCore()
    {
        acquiringMetadata = false;
        ClearDiagnosticSource();
        bindingEpoch = checked(bindingEpoch + 1);
        var subscription = kernelSubscription;
        kernelSubscription = null;
        var session = Volatile.Read(ref activeSession);
        if (session is not null) session.Window = new object();
        Volatile.Write(ref activeSession, null);
        return (subscription, session);
    }

    private static void DrainInventorySeed(ResidencySessionState? session)
        => session?.InventorySeedTask?.GetAwaiter().GetResult();

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

    private sealed class ResidencySessionState(long generation,
        long bindingEpoch, CpuResidencyCaptureConfiguration plan, NativeKernelEtwSession nativeSession,
        KernelEtwEventSource source)
    {
        public long Generation { get; } = generation;
        public long BindingEpoch { get; } = bindingEpoch;
        public CpuResidencyCaptureConfiguration Plan { get; } = plan;
        public NativeResidencyReader Native { get; } = new(nativeSession, plan, generation, source);
        public KernelEtwEventSource EventSource { get; } = source;
        public void ResetContinuity() => Native.ResetContinuity();
        // Identity only: prevents an old waiter or collect from committing after
        // an explicit BeginWindow/PauseWindow. Native owns all coverage state.
        public object Window { get; set; } = new();
        public bool WindowActive = true;
        public int EventsLost;
        public string UnavailableReason = CpuCoreResidencyUnavailableReasons.WindowWarming;
        public Task? InventorySeedTask;
    }

    public async Task<bool> WaitForCompleteWindowAsync(TimeSpan boundaryBudget, TimeSpan deliveryGrace,
        CancellationToken cancellationToken)
    {
        ResidencySessionState? session;
        object window;
        lock (subscriptionGate)
        {
            session = activeSession;
            if (session is null || !session.WindowActive) return false;
            window = session.Window;
        }
        if (!IsRunning) return false;
        bool complete;
        try { complete = await session.Native.WaitForCompleteWindowAsync(boundaryBudget, session.Plan.ObservationWindow + deliveryGrace, cancellationToken); }
        catch (KernelEtwNativeReaders.CaptureFailure) { return false; }
        lock (subscriptionGate)
            return complete && !stopped && IsCurrentSessionCore(session)
                && session.WindowActive && ReferenceEquals(session.Window, window);
    }
    private static bool TryPublish(IMetricPublicationPermit? permit, Action commit)
    { if (permit is not null) return permit.TryPublish(commit); commit(); return true; }
    public void Dispose()
    {
        IKernelEtwSubscription? lease, duration; ResidencySessionState? session;
        lock (durationResourceGate)
        lock (subscriptionGate)
        {
            if (stopped) return;
            stopped = true; windowRequested = false;
            DisposeDiagnosticRequests(); duration = DetachDurationHoldCore(); (lease, session) = DetachSessionCore(); state = "Stopped";
        }
        try { DrainInventorySeed(session); }
        finally { try { duration?.Dispose(); } finally { lease?.Dispose(); } }
    }
}
#pragma warning restore CS0618
