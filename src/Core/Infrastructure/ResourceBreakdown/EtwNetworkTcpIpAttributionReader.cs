using System.Collections.Concurrent;
using System.Net;
using Microsoft.Diagnostics.Tracing.Parsers;
using ResourceManager.App.Application.ResourceBreakdown;
using ResourceManager.App.Application.ResourceTable;
using ResourceManager.App.Domain.ResourceTable;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Infrastructure.Telemetry.Etw;

namespace ResourceManager.App.Infrastructure.ResourceBreakdown;

public sealed class EtwNetworkTcpIpAttributionReader(
    EtwNetworkTcpIpMonitoringZone sourceZone,
    IKernelEtwSessionBroker kernelEtwBroker,
    ILogger<EtwNetworkTcpIpAttributionReader> logger) : BackgroundService, IEtwNetworkAttributionReader, IResourceTableProviderStateSource
{
    private static readonly TimeSpan CalculationWindow = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RetentionWindow = TimeSpan.FromSeconds(8);
    private static readonly long CalculationWindowMilliseconds = (long)CalculationWindow.TotalMilliseconds;
    private static readonly long RetentionWindowMilliseconds = (long)RetentionWindow.TotalMilliseconds;

    private readonly object subscriptionGate = new();
    private readonly ConcurrentQueue<NetworkTransferEvent> events = new();
    private IKernelEtwSubscription? kernelSubscription;
    private long sessionGeneration;
    private DateTimeOffset lastRequestAt = DateTimeOffset.MinValue;
    private volatile string state = "Idle";
    private volatile string message = "ETW 网络事件归因按需启动。";

    public NetworkAttributionSnapshot Read()
    {
        if (!sourceZone.CanRead)
        {
            return new NetworkAttributionSnapshot(
                DateTimeOffset.UtcNow,
                new Dictionary<int, NetworkProcessAttribution>(),
                new NetworkAttributionProviderState(
                    "etw-network-tcpip",
                    "Frozen",
                    "ETW 网络事件归因当前处于功能区冻结。"),
                new Dictionary<int, NetworkProcessAttribution>());
        }

        var now = DateTimeOffset.UtcNow;
        var nowTickMilliseconds = Environment.TickCount64;
        lastRequestAt = now;
        EnsureStarted();
        Prune(nowTickMilliseconds);
        var processSnapshots = BuildProcessSnapshots(nowTickMilliseconds);
        return new NetworkAttributionSnapshot(
            now,
            processSnapshots.External,
            new NetworkAttributionProviderState("etw-network-tcpip", state, message),
            processSnapshots.Raw);
    }

    public IReadOnlyList<ResourceTableProviderState> GetStates(IReadOnlyList<ResourceTableColumn> columns)
    {
        if (!columns.Any(static column => column.Id.Equals(ResourceTableColumnIds.Network, StringComparison.OrdinalIgnoreCase)))
        {
            return [];
        }

        var currentState = state;
        if (currentState.Equals("Running", StringComparison.OrdinalIgnoreCase)
            || currentState.Equals("Idle", StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        return
        [
            new ResourceTableProviderState(
                "etw-network-tcpip",
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
                await Task.Delay(1000, stoppingToken);
                if (!sourceZone.CanRead)
                {
                    StopSession("ETW 网络事件归因已按功能区冻结停止。");
                }
                else if (IsRunning && DateTimeOffset.UtcNow - lastRequestAt > CurrentIdleStopAfter())
                {
                    StopSession("ETW 网络事件归因已因无请求而停止。");
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        StopSession("ETW 网络事件归因正在随程序停止。");
    }

    private TimeSpan CurrentIdleStopAfter()
    {
        return sourceZone.IsLowPower
            ? TimeSpan.FromSeconds(2)
            : TimeSpan.FromSeconds(5);
    }

    private bool IsRunning
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

            var snapshot = kernelEtwBroker.GetSnapshot();
            return snapshot.IsRunning
                && snapshot.Generation == Volatile.Read(ref sessionGeneration);
        }
    }

    private void EnsureStarted()
    {
        if (!sourceZone.CanRead)
        {
            state = "Idle";
            message = "ETW 网络事件归因当前处于功能区冻结。";
            return;
        }

        lock (subscriptionGate)
        {
            if (kernelSubscription is not null)
            {
                RefreshBrokerState();
                return;
            }

            try
            {
                kernelSubscription = kernelEtwBroker.Subscribe(new KernelEtwSubscriptionRequest(
                    "network-tcpip-attribution",
                    KernelTraceEventParser.Keywords.NetworkTCPIP,
                    BindKernelEvents));
            }
            catch (Exception ex)
            {
                state = "Unavailable";
                message = $"ETW 网络事件归因启动失败：{ex.Message}";
                logger.LogWarning(ex, "Failed to start ETW TCP/IP network attribution reader.");
            }

            RefreshBrokerState();
        }
    }

    private void BindKernelEvents(
        KernelTraceEventParser kernel,
        KernelEtwSessionContext context)
    {
        Volatile.Write(ref sessionGeneration, context.Generation);
        state = "Running";
        message = "ETW 网络事件归因正在采集进程收发事件。";
        kernel.TcpIpSend += data => Observe(data.ProcessID, data.size, isSend: true, isUdp: false, data.daddr);
        kernel.TcpIpSendIPV6 += data => Observe(data.ProcessID, data.size, isSend: true, isUdp: false, data.daddr);
        kernel.TcpIpRecv += data => Observe(data.ProcessID, data.size, isSend: false, isUdp: false, data.saddr);
        kernel.TcpIpRecvIPV6 += data => Observe(data.ProcessID, data.size, isSend: false, isUdp: false, data.saddr);
        kernel.UdpIpSend += data => Observe(data.ProcessID, data.size, isSend: true, isUdp: true, data.daddr);
        kernel.UdpIpSendIPV6 += data => Observe(data.ProcessID, data.size, isSend: true, isUdp: true, data.daddr);
        kernel.UdpIpRecv += data => Observe(data.ProcessID, data.size, isSend: false, isUdp: true, data.saddr);
        kernel.UdpIpRecvIPV6 += data => Observe(data.ProcessID, data.size, isSend: false, isUdp: true, data.saddr);
    }

    private void RefreshBrokerState()
    {
        var snapshot = kernelEtwBroker.GetSnapshot();
        if (snapshot.IsRunning
            && snapshot.Generation == Volatile.Read(ref sessionGeneration))
        {
            return;
        }

        state = snapshot.State;
        message = snapshot.Message;
    }

    private void Observe(
        int processId,
        int size,
        bool isSend,
        bool isUdp,
        IPAddress? remoteAddress)
    {
        if (processId <= 0 || size <= 0)
        {
            return;
        }

        events.Enqueue(new NetworkTransferEvent(
            Environment.TickCount64,
            processId,
            isSend ? 0 : size,
            isSend ? size : 0,
            isUdp,
            LeavesHost(remoteAddress)));
    }

    private ProcessSnapshots BuildProcessSnapshots(long nowTickMilliseconds)
    {
        var cutoff = nowTickMilliseconds - CalculationWindowMilliseconds;
        var elapsedSeconds = Math.Max(0.5, CalculationWindow.TotalSeconds);
        var totals = new Dictionary<int, NetworkAccumulator>();
        foreach (var item in events)
        {
            if (item.ObservedTickMilliseconds < cutoff)
            {
                continue;
            }

            totals.TryGetValue(item.ProcessId, out var accumulator);
            accumulator.Add(item);
            totals[item.ProcessId] = accumulator;
        }

        var raw = new Dictionary<int, NetworkProcessAttribution>(totals.Count);
        var external = new Dictionary<int, NetworkProcessAttribution>(totals.Count);
        foreach (var (processId, accumulator) in totals)
        {
            raw[processId] = accumulator.CreateRaw(processId, elapsedSeconds);
            if (accumulator.ExternalEventCount > 0)
            {
                external[processId] = accumulator.CreateExternal(processId, elapsedSeconds);
            }
        }

        return new ProcessSnapshots(external, raw);
    }

    private void Prune(long nowTickMilliseconds)
    {
        var cutoff = nowTickMilliseconds - RetentionWindowMilliseconds;
        while (events.TryPeek(out var item) && item.ObservedTickMilliseconds < cutoff)
        {
            events.TryDequeue(out _);
        }
    }

    private void StopSession(string idleMessage)
    {
        IKernelEtwSubscription? subscriptionToRelease;
        lock (subscriptionGate)
        {
            subscriptionToRelease = kernelSubscription;
            kernelSubscription = null;
            Volatile.Write(ref sessionGeneration, 0);
        }

        subscriptionToRelease?.Dispose();
        ClearEvents();
        state = "Idle";
        message = idleMessage;
    }

    private void ClearEvents()
    {
        while (events.TryDequeue(out _))
        {
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
        double ReceiveBytes,
        double SendBytes,
        bool IsUdp,
        bool LeavesHost);

    private readonly record struct ProcessSnapshots(
        IReadOnlyDictionary<int, NetworkProcessAttribution> External,
        IReadOnlyDictionary<int, NetworkProcessAttribution> Raw);

    private struct NetworkAccumulator
    {
        private double receiveBytes;
        private double sendBytes;
        private double externalReceiveBytes;
        private double externalSendBytes;
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
            double receiveBytes,
            double sendBytes,
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
