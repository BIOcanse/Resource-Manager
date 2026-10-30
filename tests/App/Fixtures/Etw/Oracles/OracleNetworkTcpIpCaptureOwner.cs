using ResourceManager.ReadWrite.Windows.Processes;
using ResourceManager.App.Infrastructure.ResourceBreakdown;
using System.Collections.Concurrent;
using System.Net;
using Microsoft.Diagnostics.Tracing.Parsers;
using ResourceManager.App.Application.ResourceBreakdown;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Infrastructure.Telemetry.Etw;

namespace Resource_Manager_APP.Tests.Oracles.KernelEtw;

/// <summary>Borrowed broker; owns only its TCPIP lease, queue and original complete two-second window.</summary>
internal sealed partial class OracleNetworkTcpIpCaptureOwner : IDisposable
{
    private const KernelTraceEventParser.Keywords RequiredKeywords = KernelTraceEventParser.Keywords.NetworkTCPIP;
    private const KernelTraceEventParser.Keywords MetadataKeywords = KernelTraceEventParser.Keywords.Process | KernelTraceEventParser.Keywords.Thread;

    private readonly IOracleKernelEtwSessionBroker kernelEtwBroker;
    private readonly KernelEtwDiagnostics logger;

    public OracleNetworkTcpIpCaptureOwner(IOracleKernelEtwSessionBroker broker,
        KernelEtwDiagnostics? diagnostics = null)
    {
        kernelEtwBroker = broker ?? throw new ArgumentNullException(nameof(broker));
        logger = diagnostics ?? new();
    }

    private static readonly TimeSpan CalculationWindow = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RetentionWindow = TimeSpan.FromSeconds(8);
    private static readonly long CalculationWindowMilliseconds = (long)CalculationWindow.TotalMilliseconds;
    private static readonly long RetentionWindowMilliseconds = (long)RetentionWindow.TotalMilliseconds;
    internal const int MaximumRetainedEvents = 262144;

    private readonly object subscriptionGate = new();
    private readonly object resourceGate = new();
    private readonly object eventGate = new();
    private readonly Func<long> readTickMilliseconds = static () => Environment.TickCount64;
    private readonly Func<DateTimeOffset> utcNow = static () => DateTimeOffset.UtcNow;
    private ConcurrentQueue<NetworkTransferEvent> events = new();
    private IOracleKernelEtwSubscription? kernelSubscription;
    private IOracleKernelEtwSubscription? transferSubscription;
    private long sessionGeneration;
    private int observedBrokerLoss;
    private long incompleteThroughTick = long.MinValue;
    private long windowRevision;
    private int retainedEventCount;
    private bool disposed;
    private volatile string state = NetworkAttributionProviderStates.Idle;
    private volatile string message = "ETW 网络事件归因按需启动。";

    internal OracleNetworkTcpIpCaptureOwner(IOracleKernelEtwSessionBroker broker,
        KernelEtwDiagnostics diagnostics, Func<long> clock)
        : this(broker, diagnostics)
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

        if (kernelSubscription is null)
            return new(utcNow(), new Dictionary<int, NetworkProcessAttribution>(),
                new("etw-network-tcpip", state, message), new Dictionary<int, NetworkProcessAttribution>());
        var now = utcNow();
        var nowTickMilliseconds = readTickMilliseconds();
        var brokerBefore = CaptureSnapshot();
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
        var brokerAfter = CaptureSnapshot();
        if (!brokerAfter.IsRunning || brokerAfter.Generation != brokerBefore.Generation
            || brokerAfter.Generation != Volatile.Read(ref sessionGeneration)
            || brokerAfter.EventsLost != brokerBefore.EventsLost
            || (processSnapshots.WindowRevision != 0
                && processSnapshots.WindowRevision != Volatile.Read(ref windowRevision)))
        {
            RefreshBrokerState(brokerAfter);
            processSnapshots = ProcessSnapshots.Empty;
            observedTraffic = null;
            if (brokerAfter.IsRunning && brokerAfter.Generation == Volatile.Read(ref sessionGeneration)) state = NetworkAttributionProviderStates.Warming;
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
        lock (resourceGate)
        {
            lock (subscriptionGate) ObjectDisposedException.ThrowIf(disposed, this);
            try
            {
                if (kernelSubscription is null)
                    kernelSubscription = kernelEtwBroker.Acquire(new OracleKernelEtwSubscriptionRequest(
                        "network-tcpip-metadata", MetadataKeywords, BindMetadata));
                AcquireTransferHold();
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

    private void AcquireTransferHold()
    {
        if (transferSubscription is null)
            transferSubscription = kernelEtwBroker.Acquire(new OracleKernelEtwSubscriptionRequest(
                "network-tcpip-attribution", RequiredKeywords, BindKernelEvents));
    }

    private void BindMetadata(OracleKernelEtwEventSource source, KernelEtwSessionContext context)
    {
        lock (eventGate)
        {
            sessionGeneration = context.Generation;
        }
    }

    private KernelEtwSessionSnapshot CaptureSnapshot()
    {
        var transfer = Volatile.Read(ref transferSubscription);
        var metadata = Volatile.Read(ref kernelSubscription);
        if (transfer is null && metadata is not null)
            return (metadata?.GetSnapshot() ?? kernelEtwBroker.GetSnapshot()) with { State = "Unavailable", Message = message };
        return (transfer ?? metadata)?.GetSnapshot() ?? kernelEtwBroker.GetSnapshot();
    }

    private void BindKernelEvents(
        OracleKernelEtwEventSource kernel,
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
        kernel.TcpIpSend += data => ObserveBoundNetworkTransfer(kernel, context, destination, data.ProcessID, data.size, true, false, data.daddr, readTickMilliseconds(), ToEventFileTime(data.TimeStamp));
        kernel.TcpIpSendIPV6 += data => ObserveBoundNetworkTransfer(kernel, context, destination, data.ProcessID, data.size, true, false, data.daddr, readTickMilliseconds(), ToEventFileTime(data.TimeStamp));
        kernel.TcpIpRecv += data => ObserveBoundNetworkTransfer(kernel, context, destination, data.ProcessID, data.size, false, false, data.saddr, readTickMilliseconds(), ToEventFileTime(data.TimeStamp));
        kernel.TcpIpRecvIPV6 += data => ObserveBoundNetworkTransfer(kernel, context, destination, data.ProcessID, data.size, false, false, data.saddr, readTickMilliseconds(), ToEventFileTime(data.TimeStamp));
        kernel.UdpIpSend += data => ObserveBoundNetworkTransfer(kernel, context, destination, data.ProcessID, data.size, true, true, data.daddr, readTickMilliseconds(), ToEventFileTime(data.TimeStamp));
        kernel.UdpIpSendIPV6 += data => ObserveBoundNetworkTransfer(kernel, context, destination, data.ProcessID, data.size, true, true, data.daddr, readTickMilliseconds(), ToEventFileTime(data.TimeStamp));
        kernel.UdpIpRecv += data => ObserveBoundNetworkTransfer(kernel, context, destination, data.ProcessID, data.size, false, true, data.saddr, readTickMilliseconds(), ToEventFileTime(data.TimeStamp));
        kernel.UdpIpRecvIPV6 += data => ObserveBoundNetworkTransfer(kernel, context, destination, data.ProcessID, data.size, false, true, data.saddr, readTickMilliseconds(), ToEventFileTime(data.TimeStamp));
    }

    private void RefreshBrokerState(KernelEtwSessionSnapshot? observedSnapshot = null)
    {
        var snapshot = observedSnapshot ?? CaptureSnapshot();
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
        OracleKernelEtwEventSource source, KernelEtwSessionContext context,
        ConcurrentQueue<NetworkTransferEvent> destination,
        int processId,
        int size,
        bool isSend,
        bool isUdp,
        IPAddress? remoteAddress, long observedTick, ulong? eventFileTime)
    {
        var byteCount = unchecked((uint)size); // TraceEvent exposes the provider's ULONG as Int32.
        if (processId <= 0 || byteCount == 0)
        {
            return;
        }

        lock (eventGate)
        {
            if (!source.IsActive || context.Generation != Volatile.Read(ref sessionGeneration)
                || !ReferenceEquals(destination, Volatile.Read(ref events))
                || observedTick <= incompleteThroughTick) return;
            if (retainedEventCount >= MaximumRetainedEvents)
            {
                ClearEventsCore(destination);
                incompleteThroughTick = observedTick;
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

    internal void ObserveBoundNetworkTransfer(OracleKernelEtwEventSource source, KernelEtwSessionContext context,
        int processId, int size, bool isSend, bool isUdp, IPAddress? remoteAddress, long observedTick,
        ulong? eventFileTime = null)
        => ObserveBoundNetworkTransfer(source, context, Volatile.Read(ref events),
            processId, size, isSend, isUdp, remoteAddress, observedTick, eventFileTime);

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
        lock (resourceGate) StopCore(idleMessage);
    }
    private void StopCore(string idleMessage)
    {
        IOracleKernelEtwSubscription? subscriptionToRelease;
        IOracleKernelEtwSubscription? transferToRelease;
        ConcurrentQueue<NetworkTransferEvent> eventsToClear;
        lock (subscriptionGate)
        {
            subscriptionToRelease = kernelSubscription;
            kernelSubscription = null;
            transferToRelease = transferSubscription;
            transferSubscription = null;
            eventsToClear = Volatile.Read(ref events);
            Volatile.Write(ref sessionGeneration, 0);
        }

        transferToRelease?.Dispose();
        subscriptionToRelease?.Dispose();
        lock (eventGate) { ClearEventsCore(eventsToClear); }
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
