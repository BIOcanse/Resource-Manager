using ResourceManager.App.Application.ResourceBreakdown;
using ResourceManager.App.Application.ResourceTable;
using ResourceManager.App.Domain.ResourceTable;
using ResourceManager.App.Infrastructure.Telemetry.Etw;

namespace ResourceManager.App.Infrastructure.ResourceBreakdown;

/// <summary>Host zone/run/display adapter; SDK owns the actual TCPIP lease and window.</summary>
public sealed partial class EtwNetworkTcpIpAttributionReader : BackgroundService, IEtwNetworkAttributionReader, IResourceTableProviderStateSource
{
    private readonly EtwNetworkTcpIpCaptureOwner owner;
    private readonly object captureLifetimeGate = new();
    private bool disposed;
    public EtwNetworkTcpIpAttributionReader(IKernelEtwSessionBroker broker, ILogger<EtwNetworkTcpIpAttributionReader> logger)
        : this(broker, logger, static () => Environment.TickCount64) { }
    internal EtwNetworkTcpIpAttributionReader(IKernelEtwSessionBroker broker, ILogger<EtwNetworkTcpIpAttributionReader> logger, Func<long> clock)
    {
        owner = new(broker, new(value => KernelEtwSessionBroker.Report(logger, value)), clock);
    }
    public NetworkAttributionSnapshot Read(bool includeObservedTraffic = false) => owner.Read(includeObservedTraffic);
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
                owner.Start();
            }
            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            lock (captureLifetimeGate)
            {
                owner.Stop("ETW 网络事件归因正在随程序停止。");
            }
        }
    }

    public override void Dispose()
    {
        try
        {
            lock (captureLifetimeGate)
            {
                disposed = true;
                owner.Dispose("ETW 网络事件归因正在随程序停止。");
            }
        }
        finally { base.Dispose(); }
    }

}
