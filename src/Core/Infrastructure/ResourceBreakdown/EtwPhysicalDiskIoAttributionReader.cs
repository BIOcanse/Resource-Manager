using ResourceManager.Adapter;
using ResourceManager.App.Application.Adaptation;
using ResourceManager.App.Application.Monitoring;
using ResourceManager.App.Application.ResourceBreakdown;
using ResourceManager.App.Application.ResourceTable;
using ResourceManager.App.Domain.Adaptation;
using ResourceManager.App.Domain.ResourceTable;
using ResourceManager.App.Infrastructure.Telemetry.Etw;
using ResourceManager.ReadWrite.Windows.Processes;

namespace ResourceManager.App.Infrastructure.ResourceBreakdown;

/// <summary>Product zone and idle policy over the original shared SDK DiskIO resource.</summary>
public sealed class EtwPhysicalDiskIoAttributionReader : BackgroundService, IPhysicalDiskIoAttributionReader,
    IResourceTableProviderStateSource, IResourceManagerSelfComputeZone
{
    private static readonly ulong ZoneObjectKey = AdapterResourceKey.FromString($"resource-manager:zone:{MonitoringSourceZoneIds.EtwFileIoDisk}");
    private readonly EtwPhysicalDiskIoCaptureOwner capture;
    private bool disposed;
    private DateTimeOffset lastRequestAt = DateTimeOffset.MinValue;
    private ResourceManagerComputeZoneMode currentComputeMode = ResourceManagerComputeZoneMode.Normal;
    internal const int MaximumTrackedEvents = EtwPhysicalDiskIoCaptureOwner.MaximumTrackedEvents;
    internal EtwPhysicalDiskIoCaptureOwner CaptureResource => capture;
    public EtwPhysicalDiskIoAttributionReader(IKernelEtwSessionBroker broker, ILogger<EtwPhysicalDiskIoAttributionReader> logger)
    {
        capture = new(broker, new KernelEtwDiagnostics(item =>
            logger.Log(item.Level switch { KernelEtwDiagnosticLevel.Debug => LogLevel.Debug, KernelEtwDiagnosticLevel.Information => LogLevel.Information, KernelEtwDiagnosticLevel.Warning => LogLevel.Warning, _ => LogLevel.Error }, item.Exception, item.MessageTemplate, item.Arguments.ToArray())));
    }
    internal EtwPhysicalDiskIoAttributionReader(EtwPhysicalDiskIoCaptureOwner capture) => this.capture = capture;
    public ulong ZoneKey => ZoneObjectKey;
    public string DisplayName => MonitoringSourceZoneIds.EtwFileIoDisk;
    public ResourceManagerComputeZoneMode CurrentMode => currentComputeMode;
    public PhysicalDiskIoAttributionSnapshot Read() => Read(false);
    public PhysicalDiskIoAttributionSnapshot Read(bool includeObservedTraffic)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (currentComputeMode == ResourceManagerComputeZoneMode.Freeze)
            return new(DateTimeOffset.UtcNow, new Dictionary<int, PhysicalDiskProcessAttribution>(),
                new("etw-fileio-disk", "Frozen", "ETW DiskIO 物理磁盘归因当前处于功能区冻结。"));
        lastRequestAt = DateTimeOffset.UtcNow;
        return capture.Read(includeObservedTraffic);
    }
    public IReadOnlyList<ResourceTableProviderState> GetStates(IReadOnlyList<ResourceTableColumn> columns)
    {
        if (!columns.Any(static column => column.Id.Equals(ResourceTableColumnIds.Disk, StringComparison.OrdinalIgnoreCase))
            || capture.State is "Running" or "Idle") return [];
        return [new("etw-fileio-disk", capture.State, capture.Message)];
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(1000, stoppingToken);
                StopIfIdle(DateTimeOffset.UtcNow);
            }
            catch (OperationCanceledException) { break; }
        }
        capture.Stop("ETW DiskIO 物理磁盘归因正在随程序停止。");
    }
    public void ApplyMode(ResourceManagerComputeZoneMode mode)
    {
        currentComputeMode = mode;
        if (mode == ResourceManagerComputeZoneMode.Freeze) capture.Stop("ETW DiskIO 物理磁盘归因已按功能区冻结停止。");
    }
    internal void StopIfIdle(DateTimeOffset now)
    {
        if (now - lastRequestAt > (currentComputeMode == ResourceManagerComputeZoneMode.LowPower
            ? TimeSpan.FromSeconds(2) : TimeSpan.FromSeconds(5))) capture.Stop();
    }
    public override void Dispose() { disposed = true; try { capture.Dispose(); } finally { base.Dispose(); } }
    internal void ObserveDiskTransfer(int pid, long bytes, bool write, long tick) => capture.ObserveDiskTransfer(pid, bytes, write, tick);
    internal void ObserveBoundDiskTransfer(KernelEtwEventSource source, KernelEtwSessionContext context, int pid, long bytes, bool write, long tick)
        => capture.ObserveBoundDiskTransfer(source, context, pid, bytes, write, tick);
    internal int RetainedEventCount => capture.RetainedEventCount;
    internal (IReadOnlyDictionary<int, PhysicalDiskProcessAttribution> Processes, bool Complete) ReadWindow(long now) => capture.ReadWindow(now);
}

