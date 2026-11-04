using ResourceManager.App.Application.ResourceBreakdown;
using ResourceManager.App.Application.ResourceTable;
using ResourceManager.App.Domain.ResourceTable;
using ResourceManager.App.Infrastructure.Telemetry.Etw;
using ResourceManager.ReadWrite.Windows.Processes;

namespace ResourceManager.App.Infrastructure.ResourceBreakdown;

/// <summary>Product zone and run lifetime over the original shared SDK DiskIO resource.</summary>
public sealed class EtwPhysicalDiskIoAttributionReader : BackgroundService, IPhysicalDiskIoAttributionReader,
    IResourceTableProviderStateSource
{
    private readonly EtwPhysicalDiskIoCaptureOwner capture;
    private readonly object captureLifetimeGate = new();
    private bool disposed;
    public EtwPhysicalDiskIoAttributionReader(IKernelEtwSessionBroker broker, ILogger<EtwPhysicalDiskIoAttributionReader> logger)
    {
        capture = new(broker, new KernelEtwDiagnostics(item =>
            logger.Log(item.Level switch { KernelEtwDiagnosticLevel.Debug => LogLevel.Debug, KernelEtwDiagnosticLevel.Information => LogLevel.Information, KernelEtwDiagnosticLevel.Warning => LogLevel.Warning, _ => LogLevel.Error }, item.Exception, item.MessageTemplate, item.Arguments.ToArray())));
    }
    internal EtwPhysicalDiskIoAttributionReader(EtwPhysicalDiskIoCaptureOwner capture) => this.capture = capture;
    public PhysicalDiskIoAttributionSnapshot Read() => Read(false);
    public PhysicalDiskIoAttributionSnapshot Read(bool includeObservedTraffic)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return capture.Read(includeObservedTraffic);
    }
    public IReadOnlyList<ResourceTableProviderState> GetStates(IReadOnlyList<ResourceTableColumn> columns)
    {
        if (!columns.Any(static column => column.Id.Equals(ResourceTableColumnIds.Disk, StringComparison.OrdinalIgnoreCase))
            || capture.State is PhysicalDiskIoProviderStates.Running or PhysicalDiskIoProviderStates.Idle) return [];
        return [new("etw-fileio-disk", capture.State, capture.Message)];
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            stoppingToken.ThrowIfCancellationRequested();
            await RunCaptureHoldAsync(() => Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
    // Closed DiskIO history is needed by later reads while this run captures.
    // Stop returns every chunk immediately.
    internal async Task RunCaptureHoldAsync(Func<Task> hold)
    {
        try
        {
            lock (captureLifetimeGate)
            {
                if (disposed) return;
                capture.Start();
            }
            await hold();
        }
        finally
        {
            lock (captureLifetimeGate)
            {
                capture.Stop("ETW DiskIO 物理磁盘归因正在随程序停止。");
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
                capture.Dispose();
            }
        }
        finally { base.Dispose(); }
    }
}
