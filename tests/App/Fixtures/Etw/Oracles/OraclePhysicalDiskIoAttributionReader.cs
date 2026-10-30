using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ResourceManager.App.Infrastructure.ResourceBreakdown;
using ResourceManager.App.Application.ResourceBreakdown;
using ResourceManager.App.Application.ResourceTable;
using ResourceManager.App.Domain.ResourceTable;
using ResourceManager.App.Infrastructure.Telemetry.Etw;
using ResourceManager.ReadWrite.Windows.Processes;

namespace Resource_Manager_APP.Tests.Oracles.KernelEtw;

/// <summary>Run lifetime over the original shared SDK DiskIO oracle resource.</summary>
internal sealed class OraclePhysicalDiskIoAttributionReader : BackgroundService, IPhysicalDiskIoAttributionReader,
    IResourceTableProviderStateSource
{
    private readonly OraclePhysicalDiskIoCaptureOwner capture;
    private bool disposed;
    internal const int MaximumActivePairs = OraclePhysicalDiskIoCaptureOwner.MaximumActivePairs;
    internal OraclePhysicalDiskIoCaptureOwner CaptureResource => capture;
    public OraclePhysicalDiskIoAttributionReader(IOracleKernelEtwSessionBroker broker, ILogger<OraclePhysicalDiskIoAttributionReader> logger)
    {
        capture = new(broker, new KernelEtwDiagnostics(item =>
            logger.Log(item.Level switch { KernelEtwDiagnosticLevel.Debug => LogLevel.Debug, KernelEtwDiagnosticLevel.Information => LogLevel.Information, KernelEtwDiagnosticLevel.Warning => LogLevel.Warning, _ => LogLevel.Error }, item.Exception, item.MessageTemplate, item.Arguments.ToArray())));
    }
    internal OraclePhysicalDiskIoAttributionReader(OraclePhysicalDiskIoCaptureOwner capture) => this.capture = capture;
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
            capture.Start();
            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally { capture.Stop("ETW DiskIO 物理磁盘归因正在随程序停止。"); }
    }
    public override void Dispose() { disposed = true; try { capture.Dispose(); } finally { base.Dispose(); } }
    internal void ObserveDiskTransfer(int pid, long bytes, bool write, long tick) => capture.ObserveDiskTransfer(pid, bytes, write, tick);
    internal void ObserveBoundDiskTransfer(OracleKernelEtwEventSource source, KernelEtwSessionContext context, int pid, long bytes, bool write, long tick)
        => capture.ObserveBoundDiskTransfer(source, context, pid, bytes, write, tick);
    internal int RetainedPairCount => capture.RetainedPairCount;
    internal (IReadOnlyDictionary<int, PhysicalDiskProcessAttribution> Processes, bool Complete) ReadWindow(long now) => capture.ReadWindow(now);
}

