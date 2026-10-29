using System.Net;
using ResourceManager.App.Application.ResourceBreakdown;
using ResourceManager.App.Application.ResourceTable;
using ResourceManager.App.Domain.ResourceTable;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Infrastructure.Telemetry.Etw;
using ResourceManager.ReadWrite.Windows.Devices;

namespace ResourceManager.App.Infrastructure.ResourceBreakdown;

/// <summary>Host zone/run/display adapter; SDK owns the actual TCPIP lease and window.</summary>
public sealed partial class EtwNetworkTcpIpAttributionReader : BackgroundService, IEtwNetworkAttributionReader, IResourceTableProviderStateSource
{
    private readonly EtwNetworkTcpIpMonitoringZone sourceZone;
    private readonly EtwNetworkTcpIpCaptureOwner owner;
    private readonly object captureLifetimeGate = new();
    private bool runActive;
    private bool disposed;
    internal const int MaximumRetainedEvents = EtwNetworkTcpIpCaptureOwner.MaximumRetainedEvents;
    public EtwNetworkTcpIpAttributionReader(EtwNetworkTcpIpMonitoringZone sourceZone,
        IKernelEtwSessionBroker broker, ILogger<EtwNetworkTcpIpAttributionReader> logger)
        : this(sourceZone, broker, logger, static () => Environment.TickCount64) { }
    internal EtwNetworkTcpIpAttributionReader(EtwNetworkTcpIpMonitoringZone sourceZone,
        IKernelEtwSessionBroker broker, ILogger<EtwNetworkTcpIpAttributionReader> logger, Func<long> clock)
    {
        this.sourceZone = sourceZone;
        owner = new(broker, () => sourceZone.CanRead, new(value => KernelEtwSessionBroker.Report(logger, value)), clock);
    }
    internal EtwNetworkTcpIpAttributionReader(EtwNetworkTcpIpMonitoringZone sourceZone,
        IKernelEtwSessionBroker broker, ILogger<EtwNetworkTcpIpAttributionReader> logger, Func<long> clock,
        Func<uint, IWindowsNetworkEndpointProcessLease?> openProcess)
    {
        this.sourceZone = sourceZone;
        owner = new(broker, () => sourceZone.CanRead, new(value => KernelEtwSessionBroker.Report(logger, value)), clock, openProcess);
    }
    public NetworkAttributionSnapshot Read(bool includeObservedTraffic = false) => owner.Read(includeObservedTraffic);
    internal void ObserveBoundNetworkTransfer(KernelEtwEventSource source, KernelEtwSessionContext context,
        int processId, int size, bool isSend, bool isUdp, IPAddress? remoteAddress, long observedTick, ulong? eventFileTime = null)
        => owner.ObserveBoundNetworkTransfer(source, context, processId, size, isSend, isUdp, remoteAddress, observedTick, eventFileTime);
    internal static ulong? ToEventFileTime(DateTime timestamp) => EtwNetworkTcpIpCaptureOwner.ToEventFileTime(timestamp);

    public IReadOnlyList<ResourceTableProviderState> GetStates(IReadOnlyList<ResourceTableColumn> columns)
    {
        if (!columns.Any(static column => column.Id.Equals(ResourceTableColumnIds.Network, StringComparison.OrdinalIgnoreCase)))
        {
            return [];
        }

        var currentState = owner.State;
        if (currentState is NetworkAttributionProviderStates.Running or NetworkAttributionProviderStates.Idle)
        {
            return [];
        }

        return
        [
            new ResourceTableProviderState(
                "etw-network-tcpip",
                currentState,
                owner.Message)
        ];
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            stoppingToken.ThrowIfCancellationRequested();
            lock (captureLifetimeGate)
            {
                if (disposed) return;
                runActive = true;
                sourceZone.ModeChanged += SynchronizeCapture;
                SynchronizeCapture();
            }
            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            lock (captureLifetimeGate)
            {
                runActive = false;
                sourceZone.ModeChanged -= SynchronizeCapture;
                owner.Stop("ETW 网络事件归因正在随程序停止。");
            }
        }
    }

    private void SynchronizeCapture()
    {
        lock (captureLifetimeGate)
        {
            if (!runActive) return;
            if (sourceZone.CanRead) owner.Start();
            else owner.Stop("ETW 网络事件归因当前处于功能区冻结。");
        }
    }

    public override void Dispose()
    {
        try
        {
            lock (captureLifetimeGate)
            {
                disposed = true;
                runActive = false;
                sourceZone.ModeChanged -= SynchronizeCapture;
                owner.Dispose("ETW 网络事件归因正在随程序停止。");
            }
        }
        finally { base.Dispose(); }
    }

}
