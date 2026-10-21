using System.Net;
using ResourceManager.App.Application.ResourceBreakdown;
using ResourceManager.App.Application.ResourceTable;
using ResourceManager.App.Domain.ResourceTable;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Infrastructure.Telemetry.Etw;
using ResourceManager.ReadWrite.Windows.Devices;

namespace ResourceManager.App.Infrastructure.ResourceBreakdown;

/// <summary>Host zone/idle/display adapter; SDK owns the actual TCPIP lease and window.</summary>
public sealed partial class EtwNetworkTcpIpAttributionReader : BackgroundService, IEtwNetworkAttributionReader, IResourceTableProviderStateSource
{
    private readonly EtwNetworkTcpIpMonitoringZone sourceZone;
    private readonly EtwNetworkTcpIpCaptureOwner owner;
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
                owner.Message)
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
                    owner.Stop("ETW 网络事件归因已按功能区冻结停止。");
                }
                else if (owner.IsRunning && DateTimeOffset.UtcNow - owner.LastRequestAt > CurrentIdleStopAfter())
                {
                    owner.Stop("ETW 网络事件归因已因无请求而停止。");
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        owner.Stop("ETW 网络事件归因正在随程序停止。");
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        try { await base.StopAsync(cancellationToken); }
        finally { owner.Stop("ETW 网络事件归因正在随程序停止。"); }
    }

    public override void Dispose()
    {
        try { owner.Dispose("ETW 网络事件归因正在随程序停止。"); }
        finally { base.Dispose(); }
    }

    private TimeSpan CurrentIdleStopAfter()
    {
        return sourceZone.IsLowPower
            ? TimeSpan.FromSeconds(2)
            : TimeSpan.FromSeconds(5);
    }

}
