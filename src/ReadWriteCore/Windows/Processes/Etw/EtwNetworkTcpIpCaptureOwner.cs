using System.Collections.Concurrent;
using System.Net;
using Microsoft.Diagnostics.Tracing.Parsers;
using ResourceManager.App.Application.ResourceBreakdown;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Infrastructure.Telemetry.Etw;

namespace ResourceManager.App.Infrastructure.ResourceBreakdown;

/// <summary>Borrowed broker; owns only its TCPIP lease, queue and original complete two-second window.</summary>
public sealed partial class EtwNetworkTcpIpCaptureOwner : IDisposable
{
    private const KernelTraceEventParser.Keywords RequiredKeywords = KernelTraceEventParser.Keywords.NetworkTCPIP;

    private readonly IKernelEtwSessionBroker kernelEtwBroker;
    private readonly Func<bool> canRead;
    private readonly KernelEtwDiagnostics logger;

    public EtwNetworkTcpIpCaptureOwner(IKernelEtwSessionBroker broker,
        Func<bool>? canRead = null, KernelEtwDiagnostics? diagnostics = null)
    {
        kernelEtwBroker = broker ?? throw new ArgumentNullException(nameof(broker));
        this.canRead = canRead ?? (static () => true);
        logger = diagnostics ?? new();
    }

    private static readonly TimeSpan CalculationWindow = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RetentionWindow = TimeSpan.FromSeconds(8);
    private static readonly long CalculationWindowMilliseconds = (long)CalculationWindow.TotalMilliseconds;
    private static readonly long RetentionWindowMilliseconds = (long)RetentionWindow.TotalMilliseconds;
    internal const int MaximumRetainedEvents = 262144;

    private readonly object subscriptionGate = new();
    private readonly object eventGate = new();
    private readonly Func<long> readTickMilliseconds = static () => Environment.TickCount64;
    private ConcurrentQueue<NetworkTransferEvent> events = new();
    private IKernelEtwSubscription? kernelSubscription;
    private long sessionGeneration;
    private int observedBrokerLoss;
    private long incompleteThroughTick = long.MinValue;
    private long windowRevision;
    private int retainedEventCount;
    private bool disposed;
    private volatile string state = NetworkAttributionProviderStates.Idle;
    private volatile string message = "ETW 网络事件归因按需启动。";

    internal EtwNetworkTcpIpCaptureOwner(IKernelEtwSessionBroker broker,
        Func<bool> canRead, KernelEtwDiagnostics diagnostics, Func<long> clock)
        : this(broker, canRead, diagnostics)
    {
        readTickMilliseconds = clock ?? throw new ArgumentNullException(nameof(clock));
    }
    public string State => state;
    public string Message => message;

    public NetworkAttributionSnapshot Read(bool includeObservedTraffic = false)
    {
        lock (subscriptionGate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return ReadCore(includeObservedTraffic);
        }
    }

    private NetworkAttributionSnapshot ReadCore(bool includeObservedTraffic)
    {
        if (!canRead())
        {
            InvalidateFrozenWindow(readTickMilliseconds());
            return new NetworkAttributionSnapshot(
                DateTimeOffset.UtcNow,
                new Dictionary<int, NetworkProcessAttribution>(),
                new NetworkAttributionProviderState(
                    "etw-network-tcpip",
                    NetworkAttributionProviderStates.Frozen,
                    "ETW 网络事件归因当前处于功能区冻结。"),
                new Dictionary<int, NetworkProcessAttribution>());
        }

        if (kernelSubscription is null)
            return new(DateTimeOffset.UtcNow, new Dictionary<int, NetworkProcessAttribution>(),
                new("etw-network-tcpip", state, message), new Dictionary<int, NetworkProcessAttribution>());
        var now = DateTimeOffset.UtcNow;
        var nowTickMilliseconds = readTickMilliseconds();
        var brokerBefore = (Volatile.Read(ref kernelSubscription)?.GetSnapshot() ?? kernelEtwBroker.GetSnapshot());
        RefreshBrokerState(brokerBefore);
        Prune(nowTickMilliseconds);
        var processSnapshots = brokerBefore.IsRunning
            && brokerBefore.Generation == Volatile.Read(ref sessionGeneration)
                ? BuildProcessSnapshots(nowTickMilliseconds)
                : ProcessSnapshots.Empty;
        ProcessNetworkTrafficWindow? observedTraffic = null;
        if (processSnapshots.WindowEvents is { } windowEvents)
        {
            var window = BuildObservedTraffic(windowEvents, now, out var raw, out var external);
            processSnapshots = processSnapshots with { Raw = raw, External = external };
            if (includeObservedTraffic) observedTraffic = window;
        }
        var brokerAfter = (Volatile.Read(ref kernelSubscription)?.GetSnapshot() ?? kernelEtwBroker.GetSnapshot());
        if (!brokerAfter.IsRunning || brokerAfter.Generation != brokerBefore.Generation
            || brokerAfter.Generation != Volatile.Read(ref sessionGeneration)
            || brokerAfter.EventsLost != brokerBefore.EventsLost
            || (processSnapshots.WindowRevision != 0
                && processSnapshots.WindowRevision != Volatile.Read(ref windowRevision))
            || !canRead())
        {
            RefreshBrokerState(brokerAfter);
            processSnapshots = ProcessSnapshots.Empty;
            observedTraffic = null;
            if (!canRead()) InvalidateFrozenWindow(nowTickMilliseconds);
            else if (brokerAfter.IsRunning && brokerAfter.Generation == Volatile.Read(ref sessionGeneration)) state = NetworkAttributionProviderStates.Warming;
        }
        return new NetworkAttributionSnapshot(
            now,
            processSnapshots.External,
            new NetworkAttributionProviderState("etw-network-tcpip", state, message),
            processSnapshots.Raw)
        {
            ObservedTraffic = observedTraffic
        };
    }

    public void Dispose() => Dispose("ETW network capture stopped.");

    internal void Dispose(string stoppedMessage)
    {
        lock (subscriptionGate)
        {
            if (disposed) return;
            disposed = true;
        }
        Stop(stoppedMessage);
    }

    public bool IsRunning
    {
        get
        {
            lock (subscriptionGate)
            {
                if (kernelSubscription is null)
                {
                    return false;
                }
            }

            var snapshot = (Volatile.Read(ref kernelSubscription)?.GetSnapshot() ?? kernelEtwBroker.GetSnapshot());
            return snapshot.IsRunning
                && snapshot.Generation == Volatile.Read(ref sessionGeneration);
        }
    }

    public void Start()
    {
        lock (subscriptionGate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (kernelSubscription is not null)
            {
                RefreshBrokerState();
                return;
            }

            try
            {
                kernelSubscription = kernelEtwBroker.Subscribe(new KernelEtwSubscriptionRequest(
                    "network-tcpip-attribution",
                    RequiredKeywords,
                    BindKernelEvents));
            }
            catch (Exception ex)
            {
                state = NetworkAttributionProviderStates.Unavailable;
                message = $"ETW 网络事件归因启动失败：{ex.Message}";
                logger.LogWarning(ex, "Failed to start ETW TCP/IP network attribution reader.");
            }

            RefreshBrokerState();
        }
    }

    private void BindKernelEvents(
        KernelEtwEventSource kernel,
        KernelEtwSessionContext context)
    {
        var destination = new ConcurrentQueue<NetworkTransferEvent>();
        lock (eventGate)
        {
            Volatile.Write(ref events, destination);
            Volatile.Write(ref sessionGeneration, context.Generation);
            observedBrokerLoss = 0;
            incompleteThroughTick = readTickMilliseconds();
            retainedEventCount = 0;
            windowRevision = checked(windowRevision + 1);
        }
        state = NetworkAttributionProviderStates.Running;
        message = "ETW 网络事件归因正在采集进程收发事件。";
        var counter = kernel.ConsumerCounter("network-transfer");
        kernel.TcpIpSend += data => ObserveBoundNetworkTransfer(kernel, context, destination, data.ProcessID, data.size, true, false, data.daddr, readTickMilliseconds(), ToEventFileTime(data.TimeStamp), counter);
        kernel.TcpIpSendIPV6 += data => ObserveBoundNetworkTransfer(kernel, context, destination, data.ProcessID, data.size, true, false, data.daddr, readTickMilliseconds(), ToEventFileTime(data.TimeStamp), counter);
        kernel.TcpIpRecv += data => ObserveBoundNetworkTransfer(kernel, context, destination, data.ProcessID, data.size, false, false, data.saddr, readTickMilliseconds(), ToEventFileTime(data.TimeStamp), counter);
        kernel.TcpIpRecvIPV6 += data => ObserveBoundNetworkTransfer(kernel, context, destination, data.ProcessID, data.size, false, false, data.saddr, readTickMilliseconds(), ToEventFileTime(data.TimeStamp), counter);
        kernel.UdpIpSend += data => ObserveBoundNetworkTransfer(kernel, context, destination, data.ProcessID, data.size, true, true, data.daddr, readTickMilliseconds(), ToEventFileTime(data.TimeStamp), counter);
        kernel.UdpIpSendIPV6 += data => ObserveBoundNetworkTransfer(kernel, context, destination, data.ProcessID, data.size, true, true, data.daddr, readTickMilliseconds(), ToEventFileTime(data.TimeStamp), counter);
        kernel.UdpIpRecv += data => ObserveBoundNetworkTransfer(kernel, context, destination, data.ProcessID, data.size, false, true, data.saddr, readTickMilliseconds(), ToEventFileTime(data.TimeStamp), counter);
        kernel.UdpIpRecvIPV6 += data => ObserveBoundNetworkTransfer(kernel, context, destination, data.ProcessID, data.size, false, true, data.saddr, readTickMilliseconds(), ToEventFileTime(data.TimeStamp), counter);
    }

    private void RefreshBrokerState(KernelEtwSessionSnapshot? observedSnapshot = null)
    {
        var snapshot = (observedSnapshot ?? (Volatile.Read(ref kernelSubscription)?.GetSnapshot() ?? kernelEtwBroker.GetSnapshot()));
        if (snapshot.IsRunning
            && snapshot.Generation == Volatile.Read(ref sessionGeneration))
        {
            lock (eventGate)
            {
                if (snapshot.Generation != Volatile.Read(ref sessionGeneration)) return;
                if (snapshot.EventsLost != observedBrokerLoss)
                {
                    // A concurrent runtime rebind can supersede a previously read snapshot.
                    snapshot = (Volatile.Read(ref kernelSubscription)?.GetSnapshot() ?? kernelEtwBroker.GetSnapshot());
                    if (!snapshot.IsRunning || snapshot.Generation != Volatile.Read(ref sessionGeneration)) return;
                    if (snapshot.EventsLost == observedBrokerLoss) return;
                    ClearEventsCore(Volatile.Read(ref events));
                    incompleteThroughTick = readTickMilliseconds();
                    observedBrokerLoss = snapshot.EventsLost;
                }
            }
            state = NetworkAttributionProviderStates.Running;
            message = "ETW 网络事件归因正在采集进程收发事件。";
            return;
        }

        state = NetworkAttributionProviderStates.FromKernelEtwState(snapshot.State);
        message = snapshot.Message;
    }

    private void ObserveBoundNetworkTransfer(
        KernelEtwEventSource source, KernelEtwSessionContext context,
        ConcurrentQueue<NetworkTransferEvent> destination,
        int processId,
        int size,
        bool isSend,
        bool isUdp,
        IPAddress? remoteAddress, long observedTick, ulong? eventFileTime, EtwEventDropCounter counter)
    {
        counter.Receive();
        void Reject(string reason)
        {
            counter.Drop();
            source.RecordConsumerRejection("network-transfer", reason);
        }
        var byteCount = unchecked((uint)size); // TraceEvent exposes the provider's ULONG as Int32.
        if (processId <= 0 || byteCount == 0)
        {
            Reject("invalid-payload");
            return;
        }

        lock (eventGate)
        {
            if (!source.IsActive || context.Generation != Volatile.Read(ref sessionGeneration)
                || !ReferenceEquals(destination, Volatile.Read(ref events)))
            { Reject("stale-binding"); return; }
            if (observedTick <= incompleteThroughTick) { Reject("incomplete-window"); return; }
            if (!canRead())
            {
                ClearEventsCore(destination);
                incompleteThroughTick = observedTick;
                Reject("frozen");
                return;
            }
            if (retainedEventCount >= MaximumRetainedEvents)
            {
                ClearEventsCore(destination);
                incompleteThroughTick = observedTick;
                Reject("capacity");
                return;
            }
            destination.Enqueue(new NetworkTransferEvent(
                observedTick,
                processId,
                isSend ? 0 : byteCount,
                isSend ? byteCount : 0,
                isUdp,
                LeavesHost(remoteAddress),
                eventFileTime));
            retainedEventCount++;
        }
    }

    internal void ObserveBoundNetworkTransfer(KernelEtwEventSource source, KernelEtwSessionContext context,
        int processId, int size, bool isSend, bool isUdp, IPAddress? remoteAddress, long observedTick,
        ulong? eventFileTime = null)
        => ObserveBoundNetworkTransfer(source, context, Volatile.Read(ref events),
            processId, size, isSend, isUdp, remoteAddress, observedTick, eventFileTime,
            source.ConsumerCounter("network-transfer"));

    private ProcessSnapshots BuildProcessSnapshots(long nowTickMilliseconds)
    {
        lock (eventGate) return BuildProcessSnapshotsCore(nowTickMilliseconds);
    }

    private ProcessSnapshots BuildProcessSnapshotsCore(long nowTickMilliseconds)
    {
        var cutoff = nowTickMilliseconds - CalculationWindowMilliseconds;
        if (cutoff <= incompleteThroughTick)
        {
            state = NetworkAttributionProviderStates.Warming;
            return ProcessSnapshots.Empty;
        }
        var selected = new List<NetworkTransferEvent>();
        foreach (var item in Volatile.Read(ref events))
        {
            if (item.ObservedTickMilliseconds < cutoff || item.ObservedTickMilliseconds > nowTickMilliseconds)
            {
                continue;
            }

            selected.Add(item);
        }

        return new ProcessSnapshots(new Dictionary<int, NetworkProcessAttribution>(),
            new Dictionary<int, NetworkProcessAttribution>(), selected.ToArray(), windowRevision);
    }

    private void Prune(long nowTickMilliseconds)
    {
        lock (eventGate)
        {
            var cutoff = nowTickMilliseconds - RetentionWindowMilliseconds;
            var current = Volatile.Read(ref events);
            while (current.TryPeek(out var item) && item.ObservedTickMilliseconds < cutoff)
            {
                if (current.TryDequeue(out _)) retainedEventCount--;
            }
        }
    }

    public void Stop(string idleMessage = "ETW network capture stopped.")
    {
        IKernelEtwSubscription? subscriptionToRelease;
        ConcurrentQueue<NetworkTransferEvent> eventsToClear;
        lock (subscriptionGate)
        {
            subscriptionToRelease = kernelSubscription;
            kernelSubscription = null;
            eventsToClear = Volatile.Read(ref events);
            Volatile.Write(ref sessionGeneration, 0);
        }

        subscriptionToRelease?.Dispose();
        lock (eventGate) ClearEventsCore(eventsToClear);
        lock (subscriptionGate)
        {
            if (kernelSubscription is not null) return;
            Volatile.Write(ref sessionGeneration, 0);
            state = NetworkAttributionProviderStates.Idle;
            message = idleMessage;
        }
    }

    private void ClearEventsCore(ConcurrentQueue<NetworkTransferEvent> current)
    {
        while (current.TryDequeue(out _))
        {
        }
        if (ReferenceEquals(current, Volatile.Read(ref events)))
        {
            retainedEventCount = 0;
            windowRevision = checked(windowRevision + 1);
        }
    }

    private void InvalidateFrozenWindow(long observedTick)
    {
        lock (eventGate)
        {
            ClearEventsCore(Volatile.Read(ref events));
            incompleteThroughTick = observedTick;
        }
        state = NetworkAttributionProviderStates.Frozen;
        message = "ETW 网络事件归因当前处于功能区冻结。";
    }

    private static bool LeavesHost(IPAddress? remoteAddress)
    {
        return remoteAddress is not null
            && !IPAddress.IsLoopback(remoteAddress)
            && !remoteAddress.Equals(IPAddress.Any)
            && !remoteAddress.Equals(IPAddress.IPv6Any)
            && !remoteAddress.Equals(IPAddress.None);
    }

    private readonly record struct NetworkTransferEvent(
        long ObservedTickMilliseconds,
        int ProcessId,
        uint ReceiveBytes,
        uint SendBytes,
        bool IsUdp,
        bool LeavesHost,
        ulong? EventWindowsFileTime100Ns);

    private readonly record struct ProcessSnapshots(
        IReadOnlyDictionary<int, NetworkProcessAttribution> External,
        IReadOnlyDictionary<int, NetworkProcessAttribution> Raw,
        NetworkTransferEvent[]? WindowEvents,
        long WindowRevision)
    {
        public static ProcessSnapshots Empty => new(new Dictionary<int, NetworkProcessAttribution>(),
            new Dictionary<int, NetworkProcessAttribution>(), null, 0);
    }

    private struct NetworkAccumulator
    {
        private ulong receiveBytes;
        private ulong sendBytes;
        private ulong externalReceiveBytes;
        private ulong externalSendBytes;
        private int eventCount;

        public int ExternalEventCount { get; private set; }

        public void Add(NetworkTransferEvent item)
        {
            receiveBytes += item.ReceiveBytes;
            sendBytes += item.SendBytes;
            eventCount++;
            if (!item.LeavesHost)
            {
                return;
            }

            externalReceiveBytes += item.ReceiveBytes;
            externalSendBytes += item.SendBytes;
            ExternalEventCount++;
        }

        public NetworkProcessAttribution CreateRaw(int processId, double elapsedSeconds)
        {
            return Create(processId, receiveBytes, sendBytes, eventCount, elapsedSeconds);
        }

        public NetworkProcessAttribution CreateExternal(int processId, double elapsedSeconds)
        {
            return Create(
                processId,
                externalReceiveBytes,
                externalSendBytes,
                ExternalEventCount,
                elapsedSeconds);
        }

        private static NetworkProcessAttribution Create(
            int processId,
            ulong receiveBytes,
            ulong sendBytes,
            int eventCount,
            double elapsedSeconds)
        {
            return new NetworkProcessAttribution(
                processId,
                receiveBytes / elapsedSeconds,
                sendBytes / elapsedSeconds,
                0,
                0,
                IsEstimated: false,
                IsKnownProxy: false,
                eventCount);
        }
    }
}
