using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using ResourceManager.Adapter;
using ResourceManager.App.Application.Adaptation;
using ResourceManager.App.Application.CpuTopology;
using ResourceManager.App.Application.Monitoring;
using ResourceManager.App.Application.ResourceTable;
using ResourceManager.App.Application.RuntimeSpecialization;
using ResourceManager.App.Domain.Adaptation;
using ResourceManager.App.Domain.CpuTopology;
using ResourceManager.App.Domain.ResourceTable;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.Telemetry.Etw;

namespace ResourceManager.App.Infrastructure.CpuTopology;

#pragma warning disable CS0618 // Exact integer ETW QPC boundaries are required; relative milliseconds lose precision.

internal sealed record EtwCpuCoreResidencyDiagnostics(
    string State,
    string Message,
    long BindingEpoch,
    long? SessionGeneration,
    CompiledCpuCoreResidencyPlan? SessionPlan,
    CpuResidencyAggregationDiagnostics? Aggregation);

public sealed class EtwCpuCoreResidencyReader(
    IKernelEtwSessionBroker kernelEtwBroker,
    IRuntimePlanProvider runtimePlanProvider,
    HostManagerSamplingSubscriptionOwner subscriptionOwner,
    ILogger<EtwCpuCoreResidencyReader> logger) : BackgroundService, ICpuCoreResidencyReader, IResourceTableProviderStateSource, IResourceManagerSelfComputeZone
{
    private static readonly ulong ZoneObjectKey = AdapterResourceKey.FromString($"resource-manager:zone:{MonitoringSourceZoneIds.EtwCpuCoreResidency}");
    private const string ResidencyItemId = "cpu-residency";
    private readonly object subscriptionGate = new();
    private readonly object snapshotGate = new();
    private readonly SemaphoreSlim demandSignal = new(0, 1);
    private readonly CurrentValuePublicationSignal publicationSignal = new();
    private readonly NativeItemSamplingSubscriptionTracker<CpuResidencySampleRequest> subscriptions = new(
        subscriptionOwner,
        roleId: 6,
        static request => request.SourceKey,
        static _ => [ResidencyItemId],
        static (itemIds, _) => itemIds.Contains(ResidencyItemId, StringComparer.OrdinalIgnoreCase)
            ? new CpuResidencySampleRequest("provider")
            : null,
        coalesceItemsIntoLatestCapture: true);
    private readonly Action<CpuResidencyAggregationBuffer, Func<bool>> seedProcessInventory =
        SeedExistingProcesses;
    private IKernelEtwSubscription? kernelSubscription;
    private ResidencySessionState? activeSession;
    private long bindingEpoch;
    private CpuCoreResidencySnapshot? cachedSnapshot;
    private long nextLeaseId;
    private bool stopped;
    private volatile ResourceManagerComputeZoneMode currentComputeMode = ResourceManagerComputeZoneMode.Normal;
    private volatile string state = "Idle";
    private volatile string message = "ETW CPU 执行时长归因按需启动。";

    internal EtwCpuCoreResidencyReader(
        IKernelEtwSessionBroker kernelEtwBroker,
        IRuntimePlanProvider runtimePlanProvider,
        HostManagerSamplingSubscriptionOwner subscriptionOwner,
        ILogger<EtwCpuCoreResidencyReader> logger,
        Action<CpuResidencyAggregationBuffer, Func<bool>> seedProcessInventory)
        : this(
            kernelEtwBroker,
            runtimePlanProvider,
            subscriptionOwner,
            logger)
    {
        this.seedProcessInventory = seedProcessInventory
            ?? throw new ArgumentNullException(nameof(seedProcessInventory));
    }

    public ulong ZoneKey => ZoneObjectKey;
    public string DisplayName => MonitoringSourceZoneIds.EtwCpuCoreResidency;
    public ResourceManagerComputeZoneMode CurrentMode => currentComputeMode;

    public CpuCoreResidencySnapshot? Read()
    {
        lock (snapshotGate)
        {
            return cachedSnapshot;
        }
    }

    internal EtwCpuCoreResidencyDiagnostics GetDiagnostics()
    {
        lock (subscriptionGate)
        {
            var session = Volatile.Read(ref activeSession);
            return new EtwCpuCoreResidencyDiagnostics(
                state,
                message,
                bindingEpoch,
                session?.Generation,
                session?.Plan,
                session?.Buffer.GetDiagnostics());
        }
    }

    public IDisposable AcquireSubscription(string subscriptionId, TimeSpan refreshInterval)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subscriptionId);
        var leaseId = Interlocked.Increment(ref nextLeaseId);
        var sourceKey = $"lease:{leaseId}:{subscriptionId.Trim()}";
        lock (subscriptionGate)
        {
            if (stopped)
            {
                throw new InvalidOperationException("The CPU-residency reader has stopped.");
            }

            subscriptions.TrackPersistent(
                new CpuResidencySampleRequest(sourceKey),
                DateTimeOffset.UtcNow,
                refreshInterval);
        }

        SignalDemand();
        return new ResidencySubscriptionLease(this, sourceKey);
    }

    public async IAsyncEnumerable<CpuCoreResidencySnapshot?> SubscribeAsync(
        string subscriptionId,
        TimeSpan interval,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var publication = publicationSignal.Subscribe();
        using var lease = AcquireSubscription(subscriptionId, interval);
        while (!cancellationToken.IsCancellationRequested)
        {
            await publication.WaitAsync(cancellationToken);
            yield return Read();
        }
    }

    public IReadOnlyList<ResourceTableProviderState> GetStates(IReadOnlyList<ResourceTableColumn> columns)
    {
        if (!columns.Any(static column => column.Id.Equals(ResourceTableColumnIds.Cpu, StringComparison.OrdinalIgnoreCase)))
        {
            return [];
        }

        var currentState = state;
        if (currentState.Equals("Running", StringComparison.OrdinalIgnoreCase)
            || currentState.Equals("Warming", StringComparison.OrdinalIgnoreCase)
            || currentState.Equals("NotRequested", StringComparison.OrdinalIgnoreCase)
            || currentState.Equals("Idle", StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        return
        [
            new ResourceTableProviderState(
                "etw-context-switch-cpu",
                currentState,
                message)
        ];
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var now = DateTimeOffset.UtcNow;
                var plan = subscriptions.CreatePlan(now);
                if (!plan.IsActive)
                {
                    if (HasSessionLease)
                    {
                        StopSession("ETW CPU 执行时长归因已因无订阅而停止。");
                    }

                    await WaitForDemandAsync(stoppingToken);
                    continue;
                }

                if (plan.Request is null)
                {
                    await WaitForDemandOrDelayAsync(plan.Delay, stoppingToken);
                    continue;
                }

                EnsureStarted();
                var sampled = RefreshCachedSnapshot(plan.OwnerToken);
                var completion = !plan.OwnerToken.IsActive
                    ? subscriptions.MarkSkipped(plan.Request, DateTimeOffset.UtcNow)
                    : sampled
                        ? subscriptions.MarkSampled(plan.Request, DateTimeOffset.UtcNow)
                        : subscriptions.MarkFailed(plan.Request, DateTimeOffset.UtcNow);
                await WaitForDemandOrDelayAsync(completion.Delay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        StopSession("ETW CPU 执行时长归因正在随程序停止。");
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        BeginStop();
        return base.StopAsync(cancellationToken);
    }

    public override void Dispose()
    {
        BeginStop();
        base.Dispose();
    }

    private async Task WaitForDemandAsync(CancellationToken cancellationToken)
    {
        try
        {
            await demandSignal.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task WaitForDemandOrDelayAsync(
        TimeSpan delay,
        CancellationToken cancellationToken)
    {
        if (delay < TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                "Native CPU-residency sampling returned a past next-wake time.");
        }

        try
        {
            await demandSignal.WaitAsync(delay, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void ReleaseLease(string sourceKey)
    {
        lock (subscriptionGate)
        {
            if (stopped)
            {
                return;
            }

            subscriptions.Remove(sourceKey, DateTimeOffset.UtcNow);
        }
        SignalDemand();
    }

    public void ApplyMode(ResourceManagerComputeZoneMode mode)
    {
        IKernelEtwSubscription? subscriptionToRelease = null;
        ResidencySessionState? sessionToClear = null;
        lock (subscriptionGate)
        {
            if (stopped)
            {
                return;
            }

            currentComputeMode = mode;
            if (mode == ResourceManagerComputeZoneMode.Freeze)
            {
                (subscriptionToRelease, sessionToClear) = DetachSessionCore();
                state = "Idle";
                message = "ETW CPU 执行时长归因已按功能区冻结停止。";
            }
        }

        subscriptionToRelease?.Dispose();
        sessionToClear?.Buffer.Clear();
        if (mode != ResourceManagerComputeZoneMode.Freeze)
        {
            SignalDemand();
        }
    }

    private bool IsRunning
    {
        get
        {
            var session = Volatile.Read(ref activeSession);
            if (session is null)
            {
                return false;
            }

            var broker = kernelEtwBroker.GetSnapshot();
            return broker.IsRunning
                && broker.Generation == session.Generation
                && broker.EventsLost == 0;
        }
    }

    private bool HasSessionLease
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

    private void EnsureStarted()
    {
        lock (subscriptionGate)
        {
            if (stopped || currentComputeMode == ResourceManagerComputeZoneMode.Freeze)
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

            try
            {
                var expectedBindingEpoch = checked(++bindingEpoch);
                kernelSubscription = kernelEtwBroker.Subscribe(new KernelEtwSubscriptionRequest(
                    "cpu-core-residency",
                    KernelTraceEventParser.Keywords.Process
                    | KernelTraceEventParser.Keywords.Thread
                    | KernelTraceEventParser.Keywords.ContextSwitch,
                    (kernel, context) => BindKernelEvents(
                        kernel,
                        context,
                        expectedBindingEpoch)));
            }
            catch (Exception ex)
            {
                Volatile.Write(ref activeSession, null);
                state = "Unavailable";
                message = $"ETW CPU 执行时长归因启动失败：{ex.Message}";
                logger.LogWarning(ex, "Failed to start ETW CPU execution-time reader.");
            }

            RefreshBrokerState();
        }
    }

    private void BindKernelEvents(
        KernelTraceEventParser kernel,
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

        var plan = runtimePlanProvider.Current.HostManager
            .RequirePublished()
            .CpuCoreResidency
            .RequirePublished();
        var session = new ResidencySessionState(
            context.Generation,
            context.StartedAt,
            expectedBindingEpoch,
            plan,
            CreateAggregationBuffer(plan, Stopwatch.Frequency));
        BindKernelEventCallbacks(
            kernel,
            session.Buffer,
            () => IsCurrentSession(session, expectedBindingEpoch),
            static _ => null);

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

        _ = Task.Run(
            () => seedProcessInventory(
                session.Buffer,
                () => IsCurrentSession(session, expectedBindingEpoch)),
            CancellationToken.None);
    }

    internal static CpuResidencyAggregationBuffer CreateAggregationBuffer(
        CompiledCpuCoreResidencyPlan plan,
        long qpcFrequency)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var published = plan.RequirePublished();
        return new CpuResidencyAggregationBuffer(
            qpcFrequency,
            published.ObservationWindowMilliseconds,
            published.ExecutionTimeSource.MaximumClosedSlices);
    }

    internal static void BindKernelEventCallbacks(
        KernelTraceEventParser kernel,
        CpuResidencyAggregationBuffer buffer,
        Func<bool> shouldObserve,
        Func<int, long?> readProcessStartKey)
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
                readProcessStartKey(data.ProcessID),
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
            if (shouldObserve())
            {
                buffer.ObserveContextSwitch(
                    data.TimeStampQPC,
                    ToTimestamp(data.TimeStamp),
                    data.ProcessorNumber,
                    data.OldProcessID,
                    data.OldThreadID,
                    data.NewProcessID,
                    data.NewProcessName,
                    data.NewThreadID);
            }
        };

    }

    private bool IsCurrentSession(ResidencySessionState session, long expectedBindingEpoch)
        => Volatile.Read(ref bindingEpoch) == expectedBindingEpoch
            && ReferenceEquals(Volatile.Read(ref activeSession), session);

    private void RefreshBrokerState(ResidencySessionState? expectedSession = null)
    {
        var broker = kernelEtwBroker.GetSnapshot();
        var session = Volatile.Read(ref activeSession);
        if (session is not null
            && broker.IsRunning
            && broker.Generation == session.Generation
            && broker.EventsLost == 0)
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

            state = broker.EventsLost > 0 ? "Warming" : broker.State;
            message = broker.EventsLost > 0
                ? "共享 kernel ETW 会话发生事件丢失，当前执行时长窗口已作废并等待会话重建。"
                : broker.Message;
        }
    }

    private bool RefreshCachedSnapshot(SamplingOwnerToken ownerToken)
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
            var brokerBefore = kernelEtwBroker.GetSnapshot();
            CpuCoreResidencySnapshot? snapshot;
            string nextState;
            string nextMessage;
            var sampled = observedSession is not null
                && brokerBefore.IsRunning
                && brokerBefore.Generation == observedSession.Generation
                && brokerBefore.EventsLost == 0;
            if (!sampled)
            {
                observedSession?.Buffer.ResetContinuity();
                snapshot = null;
                (nextState, nextMessage) = BrokerDiagnostic(brokerBefore);
            }
            else
            {
                var currentSession = observedSession
                    ?? throw new InvalidOperationException("The running ETW session state is missing.");
                var aggregate = currentSession.Buffer.Snapshot(
                    currentSession.Plan.PhysicalCoreByLogicalProcessor.Keys);
                var brokerAfter = kernelEtwBroker.GetSnapshot();
                if (!brokerAfter.IsRunning
                    || brokerAfter.Generation != currentSession.Generation
                    || brokerAfter.EventsLost != 0)
                {
                    currentSession.Buffer.ResetContinuity();
                    snapshot = null;
                    (nextState, nextMessage) = BrokerDiagnostic(brokerAfter);
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
                        BuildProcessSnapshot(currentSession.Plan, currentSession.Generation, aggregate));
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
                nextState,
                nextMessage);
        }
        catch (Exception ex)
        {
            _ = ownerToken.TryPublish(() =>
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
                    publicationSignal.Publish();
                }
            });
            return false;
        }
    }

    private bool Publish(
        SamplingOwnerToken ownerToken,
        CpuCoreResidencySnapshot? snapshot,
        bool sampled,
        ResidencySessionState? expectedSession,
        long expectedBindingEpoch,
        string nextState,
        string nextMessage)
    {
        var committed = false;
        var committedAsSampled = sampled;
        var published = ownerToken.TryPublish(() =>
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
                    var broker = kernelEtwBroker.GetSnapshot();
                    if (!broker.IsRunning
                        || broker.Generation != expectedSession.Generation
                        || broker.EventsLost != 0)
                    {
                        expectedSession.Buffer.ResetContinuity();
                        snapshot = null;
                        committedAsSampled = false;
                        (nextState, nextMessage) = BrokerDiagnostic(broker);
                    }
                }

                SetCachedSnapshot(snapshot);
                state = nextState;
                message = nextMessage;
                publicationSignal.Publish();
                committed = true;
            }
        });
        return published && committed && committedAsSampled;
    }

    private bool IsCurrentSessionCore(ResidencySessionState session)
        => bindingEpoch == session.BindingEpoch
            && ReferenceEquals(Volatile.Read(ref activeSession), session);

    private void SetCachedSnapshot(CpuCoreResidencySnapshot? snapshot)
    {
        lock (snapshotGate)
        {
            cachedSnapshot = snapshot;
        }
    }

    private static (string State, string Message) BrokerDiagnostic(KernelEtwSessionSnapshot broker)
        => broker.EventsLost > 0
            ? ("Warming", "共享 kernel ETW 会话发生事件丢失，当前执行时长窗口已作废并等待会话重建。")
            : (broker.State, broker.Message);

    private static string WarmingMessage(ResidencySessionState session)
        => $"ETW CPU 执行时长归因正在形成最近 {session.Plan.ObservationWindow.TotalSeconds:0.###} 秒完整窗口。";

    internal static IReadOnlyList<CpuProcessCoreResidency> BuildProcessSnapshot(
        CompiledCpuCoreResidencyPlan plan,
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
        IReadOnlyDictionary<int, CompiledCpuPhysicalCoreAccounting> physicalByLogicalId)
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
        IReadOnlyDictionary<int, CompiledCpuPhysicalCoreAccounting> physicalByLogicalId)
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
            core.CcdId,
            core.PhysicalCoreId,
            primary.Key,
            physicalCoreIds);
    }

    private void StopSession(string idleMessage)
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

        subscriptionToRelease?.Dispose();
        sessionToClear?.Buffer.Clear();
    }

    private void BeginStop()
    {
        IKernelEtwSubscription? subscriptionToRelease;
        ResidencySessionState? sessionToClear;
        lock (subscriptionGate)
        {
            if (stopped)
            {
                return;
            }

            stopped = true;
            subscriptions.Clear(DateTimeOffset.UtcNow);
            (subscriptionToRelease, sessionToClear) = DetachSessionCore();
            state = "Stopped";
            message = "ETW CPU 执行时长归因已随程序停止。";
        }

        subscriptionToRelease?.Dispose();
        sessionToClear?.Buffer.Clear();
        SignalDemand();
    }

    private (IKernelEtwSubscription? Subscription, ResidencySessionState? Session) DetachSessionCore()
    {
        bindingEpoch = checked(bindingEpoch + 1);
        var subscription = kernelSubscription;
        kernelSubscription = null;
        var session = Volatile.Read(ref activeSession);
        Volatile.Write(ref activeSession, null);
        return (subscription, session);
    }

    private void SignalDemand()
    {
        if (demandSignal.CurrentCount != 0)
        {
            return;
        }

        try
        {
            demandSignal.Release();
        }
        catch (SemaphoreFullException)
        {
        }
    }

    private static void SeedExistingProcesses(
        CpuResidencyAggregationBuffer buffer,
        Func<bool> shouldSeed)
    {
        ArgumentNullException.ThrowIfNull(shouldSeed);
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                if (!shouldSeed())
                {
                    return;
                }

                int processId;
                try
                {
                    processId = process.Id;
                    buffer.SeedProcess(
                        processId,
                        TryReadProcessStartKey(process),
                        process.ProcessName);
                }
                catch
                {
                    continue;
                }

                try
                {
                    foreach (ProcessThread thread in process.Threads)
                    {
                        using (thread)
                        {
                            if (!shouldSeed())
                            {
                                return;
                            }

                            try
                            {
                                buffer.SeedThread(processId, thread.Id);
                            }
                            catch
                            {
                            }
                        }
                    }
                }
                catch
                {
                }
            }
        }
    }

    private static long? TryReadProcessStartKey(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return TryReadProcessStartKey(process);
        }
        catch
        {
            return null;
        }
    }

    private static long? TryReadProcessStartKey(Process process)
    {
        try
        {
            return process.StartTime.ToFileTimeUtc();
        }
        catch
        {
            return null;
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

    private sealed class ResidencySubscriptionLease(
        EtwCpuCoreResidencyReader owner,
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

    private sealed class ResidencySessionState(
        long generation,
        DateTimeOffset startedAt,
        long bindingEpoch,
        CompiledCpuCoreResidencyPlan plan,
        CpuResidencyAggregationBuffer buffer)
    {
        public long Generation { get; } = generation;
        public DateTimeOffset StartedAt { get; } = startedAt;
        public long BindingEpoch { get; } = bindingEpoch;
        public CompiledCpuCoreResidencyPlan Plan { get; } = plan;
        public CpuResidencyAggregationBuffer Buffer { get; } = buffer;
    }

    private sealed record CpuResidencySampleRequest(string SourceKey);
}

#pragma warning restore CS0618
