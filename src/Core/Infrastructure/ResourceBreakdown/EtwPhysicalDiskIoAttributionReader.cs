using System.Collections.Concurrent;
using Microsoft.Diagnostics.Tracing.Parsers;
using ResourceManager.Adapter;
using ResourceManager.App.Application.Adaptation;
using ResourceManager.App.Application.Monitoring;
using ResourceManager.App.Application.ResourceBreakdown;
using ResourceManager.App.Application.ResourceTable;
using ResourceManager.App.Domain.Adaptation;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Domain.ResourceTable;
using ResourceManager.App.Infrastructure.Telemetry.Etw;

namespace ResourceManager.App.Infrastructure.ResourceBreakdown;

public sealed class EtwPhysicalDiskIoAttributionReader(
    IKernelEtwSessionBroker kernelEtwBroker,
    ILogger<EtwPhysicalDiskIoAttributionReader> logger) : BackgroundService, IPhysicalDiskIoAttributionReader, IResourceTableProviderStateSource, IResourceManagerSelfComputeZone
{
    private static readonly TimeSpan CalculationWindow = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RetentionWindow = TimeSpan.FromSeconds(8);
    private static readonly long CalculationWindowMilliseconds = (long)CalculationWindow.TotalMilliseconds;
    private static readonly long RetentionWindowMilliseconds = (long)RetentionWindow.TotalMilliseconds;
    private static readonly ulong ZoneObjectKey = AdapterResourceKey.FromString($"resource-manager:zone:{MonitoringSourceZoneIds.EtwFileIoDisk}");

    private readonly object subscriptionGate = new();
    private readonly ConcurrentQueue<PhysicalDiskIoEvent> events = new();
    private IKernelEtwSubscription? kernelSubscription;
    private long sessionGeneration;
    private DateTimeOffset lastRequestAt = DateTimeOffset.MinValue;
    private ResourceManagerComputeZoneMode currentComputeMode = ResourceManagerComputeZoneMode.Normal;
    private volatile string state = "Idle";
    private volatile string message = "ETW FileIO 真实磁盘归因按需启动。";

    public ulong ZoneKey => ZoneObjectKey;
    public string DisplayName => MonitoringSourceZoneIds.EtwFileIoDisk;
    public ResourceManagerComputeZoneMode CurrentMode => currentComputeMode;

    public PhysicalDiskIoAttributionSnapshot Read()
    {
        if (currentComputeMode == ResourceManagerComputeZoneMode.Freeze)
        {
            return new PhysicalDiskIoAttributionSnapshot(
                DateTimeOffset.UtcNow,
                new Dictionary<int, PhysicalDiskProcessAttribution>(),
                new PhysicalDiskIoProviderState(
                    "etw-fileio-disk",
                    "Frozen",
                    "ETW FileIO 真实磁盘归因当前处于功能区冻结。"));
        }

        var now = DateTimeOffset.UtcNow;
        var nowTickMilliseconds = Environment.TickCount64;
        lastRequestAt = now;
        EnsureStarted();
        Prune(nowTickMilliseconds);
        return new PhysicalDiskIoAttributionSnapshot(
            now,
            BuildProcessSnapshot(nowTickMilliseconds),
            new PhysicalDiskIoProviderState("etw-fileio-disk", state, message));
    }

    public IReadOnlyList<ResourceTableProviderState> GetStates(IReadOnlyList<ResourceTableColumn> columns)
    {
        if (!columns.Any(static column => column.Id.Equals(ResourceTableColumnIds.Disk, StringComparison.OrdinalIgnoreCase)))
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
                "etw-fileio-disk",
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
                if (IsRunning && DateTimeOffset.UtcNow - lastRequestAt > CurrentIdleStopAfter())
                {
                    StopSession("ETW FileIO 真实磁盘归因已因无请求而停止。");
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        StopSession("ETW FileIO 真实磁盘归因正在随程序停止。");
    }

    public void ApplyMode(ResourceManagerComputeZoneMode mode)
    {
        currentComputeMode = mode;
        if (mode == ResourceManagerComputeZoneMode.Freeze)
        {
            StopSession("ETW FileIO 真实磁盘归因已按功能区冻结停止。");
        }
    }

    private TimeSpan CurrentIdleStopAfter()
    {
        return currentComputeMode switch
        {
            ResourceManagerComputeZoneMode.LowPower => TimeSpan.FromSeconds(2),
            _ => TimeSpan.FromSeconds(5)
        };
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
        if (currentComputeMode == ResourceManagerComputeZoneMode.Freeze)
        {
            state = "Idle";
            message = "ETW FileIO 真实磁盘归因当前处于功能区冻结。";
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
                    "physical-disk-io-attribution",
                    KernelTraceEventParser.Keywords.FileIO | KernelTraceEventParser.Keywords.FileIOInit,
                    BindKernelEvents));
            }
            catch (Exception ex)
            {
                state = "Unavailable";
                message = $"ETW FileIO 真实磁盘归因启动失败：{ex.Message}";
                logger.LogWarning(ex, "Failed to start ETW FileIO disk attribution reader.");
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
        message = "ETW FileIO 真实磁盘归因正在采集文件读写事件。";
        kernel.FileIORead += data => Observe(data.ProcessID, data.FileName, data.IoSize, isWrite: false);
        kernel.FileIOWrite += data => Observe(data.ProcessID, data.FileName, data.IoSize, isWrite: true);
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

    private void Observe(int processId, string? fileName, long ioSize, bool isWrite)
    {
        if (processId <= 0 || ioSize <= 0 || !LooksLikeDiskBackedFile(fileName))
        {
            return;
        }

        var value = Math.Max(0, ioSize);
        events.Enqueue(new PhysicalDiskIoEvent(
            Environment.TickCount64,
            processId,
            isWrite ? 0 : value,
            isWrite ? value : 0,
            isWrite));
    }

    private IReadOnlyDictionary<int, PhysicalDiskProcessAttribution> BuildProcessSnapshot(long nowTickMilliseconds)
    {
        var cutoff = nowTickMilliseconds - CalculationWindowMilliseconds;
        var elapsedSeconds = Math.Max(0.5, CalculationWindow.TotalSeconds);
        var totals = new Dictionary<int, PhysicalDiskAccumulator>();
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

        var snapshot = new Dictionary<int, PhysicalDiskProcessAttribution>(totals.Count);
        foreach (var (processId, accumulator) in totals)
        {
            snapshot[processId] = accumulator.Create(processId, elapsedSeconds);
        }

        return snapshot;
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

    private static bool LooksLikeDiskBackedFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var value = path.Trim();
        if (value.StartsWith(@"\Device\NamedPipe\", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith(@"\Device\Mailslot\", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith(@"\Device\Afd\", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith(@"\Device\Tcp", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith(@"\Device\Udp", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith(@"\\.\pipe\", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return value.StartsWith(@"\Device\Harddisk", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith(@"\Device\CdRom", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith(@"\??\", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith(@"\\", StringComparison.OrdinalIgnoreCase)
            || (value.Length >= 3 && char.IsLetter(value[0]) && value[1] == ':' && (value[2] == '\\' || value[2] == '/'));
    }

    private readonly record struct PhysicalDiskIoEvent(
        long ObservedTickMilliseconds,
        int ProcessId,
        double ReadBytes,
        double WriteBytes,
        bool IsWrite);

    private struct PhysicalDiskAccumulator
    {
        private double readBytes;
        private double writeBytes;
        private int readOperations;
        private int writeOperations;

        public void Add(PhysicalDiskIoEvent item)
        {
            readBytes += item.ReadBytes;
            writeBytes += item.WriteBytes;
            if (item.ReadBytes > 0)
            {
                readOperations++;
            }

            if (item.WriteBytes > 0)
            {
                writeOperations++;
            }
        }

        public PhysicalDiskProcessAttribution Create(int processId, double elapsedSeconds)
        {
            return new PhysicalDiskProcessAttribution(
                processId,
                readBytes / elapsedSeconds,
                writeBytes / elapsedSeconds,
                readOperations,
                writeOperations);
        }
    }
}
