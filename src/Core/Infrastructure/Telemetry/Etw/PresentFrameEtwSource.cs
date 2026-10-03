using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;
using ResourceManager.Adapter;
using ResourceManager.App.Application.Adaptation;
using ResourceManager.App.Application.FrameTiming;
using ResourceManager.App.Application.Monitoring;
using ResourceManager.App.Domain.Adaptation;
using ResourceManager.App.Domain.FrameTiming;

namespace ResourceManager.App.Infrastructure.Telemetry.Etw;

/// <summary>
/// 按进程采集 Present 帧时间。独立 ETW 会话，不并入内核会话：内核会话在订阅变化或丢事件时会整体重启，
/// 高频的 Present 事件不能连累其他采集。有订阅才开，最后一个订阅释放就停；功能区冻结时停。
/// 事件口径见 docs/performance_overlay_and_targeted_report_2026-10-02.md 的“第 1 片合同”。
/// </summary>
public sealed class PresentFrameEtwSource(ILogger<PresentFrameEtwSource> logger)
    : IFrameTimingObservationSource, IResourceManagerSelfComputeZone, IDisposable
{
    internal static readonly Guid DxgiProvider = new("CA11C036-0102-4A2D-A6AD-F03CFED5D3C9");
    internal static readonly Guid Direct3D9Provider = new("783ACA0A-790E-4D7F-8451-AA850511C6B9");
    internal static readonly Guid GraphicsKernelProvider = new("802EC45A-1E99-4B83-9920-87C98277BA9D");
    internal static readonly Guid KernelProcessProvider = new("22FB2CD6-0E7B-422B-A0C7-2FAD1FD0E716");
    private const ulong RuntimeEventsKeyword = 0x2;
    private const ulong GraphicsKernelPresentKeyword = 0x8000000;
    private const ulong KernelProcessKeyword = 0x10;
    private const int DxgiPresentStart = 42;
    private const int DxgiMultiplaneOverlayPresentStart = 55;
    private const int Direct3D9PresentStart = 1;
    private const int GraphicsKernelPresent = 184;
    private const int ProcessStart = 1;
    private const int ProcessStop = 2;
    // 帧事件量小，实时缓冲很久才会写满；定时冲刷把投递延迟限制在这个间隔内（叠加层按 250–500 ms 刷新）。
    internal static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(250);
    private static readonly ulong ZoneObjectKey =
        AdapterResourceKey.FromString($"resource-manager:zone:{MonitoringSourceZoneIds.EtwPresentFrames}");

    private readonly object lifecycle = new();
    private readonly object gate = new();
    private PresentFrameLedger ledger = new(ReadProcessStartKey);
    private TraceEventSession? session;
    private Task? processor;
    private Timer? flushTimer;
    private int leases;
    private bool failed;
    private bool disposed;
    private int observedEventsLost;
    private long lastLossUtcTicks;
    private ResourceManagerComputeZoneMode mode = ResourceManagerComputeZoneMode.Normal;

    public ulong ZoneKey => ZoneObjectKey;
    public string DisplayName => MonitoringSourceZoneIds.EtwPresentFrames;
    public ResourceManagerComputeZoneMode CurrentMode => mode;

    public IDisposable AcquireSubscription()
    {
        lock (lifecycle)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (leases++ == 0 && mode != ResourceManagerComputeZoneMode.Freeze)
            {
                Start();
            }

            return new Lease(this);
        }
    }

    public FrameTimingSnapshot? Read(TimeSpan window)
    {
        lock (lifecycle)
        {
            if (session is not { } current)
            {
                return null;
            }

            lock (gate)
            {
                if (failed)
                {
                    return null;
                }

                var now = DateTime.UtcNow.Ticks;
                var lost = current.EventsLost;
                if (lost != observedEventsLost)
                {
                    observedEventsLost = lost;
                    lastLossUtcTicks = now;
                }

                var complete = lastLossUtcTicks == 0 || lastLossUtcTicks < now - window.Ticks;
                return new FrameTimingSnapshot(
                    new DateTimeOffset(now, TimeSpan.Zero), window, complete, ledger.Read(now, window));
            }
        }
    }

    public FrameIntervalBatch? ReadIntervals(DateTimeOffset after, DateTimeOffset through)
    {
        if (through <= after || through - after > PresentFrameLedger.Retention)
        {
            throw new ArgumentOutOfRangeException(nameof(through));
        }

        lock (lifecycle)
        {
            if (session is not { } current)
            {
                return null;
            }

            lock (gate)
            {
                if (failed)
                {
                    return null;
                }

                var lost = current.EventsLost;
                if (lost != observedEventsLost)
                {
                    observedEventsLost = lost;
                    lastLossUtcTicks = DateTime.UtcNow.Ticks;
                }

                return new FrameIntervalBatch(through,
                    lastLossUtcTicks == 0 || lastLossUtcTicks < after.UtcTicks,
                    ledger.ReadIntervals(after.UtcTicks, through.UtcTicks));
            }
        }
    }

    public void ApplyMode(ResourceManagerComputeZoneMode next)
    {
        lock (lifecycle)
        {
            mode = next;
            if (next == ResourceManagerComputeZoneMode.Freeze)
            {
                Stop();
            }
            else if (leases > 0 && session is null && !disposed)
            {
                Start();
            }
        }
    }

    private void Start()
    {
        lock (gate)
        {
            ledger = new PresentFrameLedger(ReadProcessStartKey);
            failed = false;
            observedEventsLost = 0;
            lastLossUtcTicks = 0;
        }

        var name = ResourceManagerEtwSessionNames.Create(
            ResourceManagerEtwSessionNames.PresentFramesPrefix, ResourceManagerEtwSessionNames.CurrentOwner);
        try
        {
            ProcessOwnedEtwSessionOrphanReconciler.CreateDefault(logger).Reconcile(
                ResourceManagerEtwSessionNames.PresentFramesPrefix, ResourceManagerEtwSessionNames.CurrentOwner, name);
            var current = new TraceEventSession(name) { StopOnDispose = true, BufferSizeMB = 16, EnableProviderTimeoutMSec = 2000 };
            session = current;
            current.Source.Dynamic.All += OnEvent;
            processor = Task.Factory.StartNew(() =>
            {
                try { current.Source.Process(); }
                catch (Exception error) { logger.LogWarning(error, "Present frame event stream ended."); }
                finally { lock (gate) { if (ReferenceEquals(session, current)) failed = true; } }
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            current.EnableProvider(KernelProcessProvider, TraceEventLevel.Informational, KernelProcessKeyword,
                new TraceEventProviderOptions { EventIDsToEnable = [ProcessStart, ProcessStop] });
            current.EnableProvider(DxgiProvider, TraceEventLevel.Verbose, RuntimeEventsKeyword,
                new TraceEventProviderOptions { EventIDsToEnable = [DxgiPresentStart, DxgiMultiplaneOverlayPresentStart] });
            current.EnableProvider(Direct3D9Provider, TraceEventLevel.Verbose, RuntimeEventsKeyword,
                new TraceEventProviderOptions { EventIDsToEnable = [Direct3D9PresentStart] });
            current.EnableProvider(GraphicsKernelProvider, TraceEventLevel.Verbose, GraphicsKernelPresentKeyword,
                new TraceEventProviderOptions { EventIDsToEnable = [GraphicsKernelPresent] });
            flushTimer = new Timer(_ => Flush(current), null, FlushInterval, FlushInterval);
        }
        catch (Exception error)
        {
            lock (gate) failed = true;
            logger.LogWarning(error, "Present frame session {Session} could not start.", name);
            Stop();
        }
    }

    private void Flush(TraceEventSession current)
    {
        try
        {
            current.Flush();
        }
        catch (Exception error) when (error is InvalidOperationException or ObjectDisposedException or System.ComponentModel.Win32Exception or System.Runtime.InteropServices.COMException)
        {
            // 会话正在停止时冲刷会失败，停止流程会处理会话本身。
            logger.LogDebug(error, "Present frame session flush skipped.");
        }
    }

    private void OnEvent(TraceEvent data)
    {
        lock (gate)
        {
            try
            {
                Apply(ledger, data);
            }
            catch (Exception error)
            {
                failed = true;
                logger.LogWarning(error, "Present frame event {Provider}/{Event} could not be applied.", data.ProviderName, (int)data.ID);
            }
        }
    }

    internal static void Apply(PresentFrameLedger target, TraceEvent data)
    {
        var id = (int)data.ID;
        if (data.ProviderGuid == KernelProcessProvider)
        {
            var processId = Convert.ToInt32(data.PayloadByName("ProcessID"));
            if (id == ProcessStart && data.PayloadByName("CreateTime") is DateTime created)
            {
                target.ProcessStarted(processId, created.ToFileTimeUtc());
            }
            else if (id == ProcessStop)
            {
                target.ProcessStopped(processId);
            }

            return;
        }

        var timestamp = data.TimeStamp.ToUniversalTime().Ticks;
        if (data.ProviderGuid == DxgiProvider && id is DxgiPresentStart or DxgiMultiplaneOverlayPresentStart)
        {
            target.Present(data.ProcessID, FramePresentSource.Dxgi, ReadPointer(data.PayloadByName("pIDXGISwapChain")), timestamp);
        }
        else if (data.ProviderGuid == Direct3D9Provider && id == Direct3D9PresentStart)
        {
            target.Present(data.ProcessID, FramePresentSource.Direct3D9, ReadPointer(data.PayloadByName("pSwapchain")), timestamp);
        }
        else if (data.ProviderGuid == GraphicsKernelProvider && id == GraphicsKernelPresent)
        {
            target.Present(data.ProcessID, FramePresentSource.GraphicsKernel, 0, timestamp);
        }
    }

    private static ulong ReadPointer(object? value) => value switch
    {
        ulong pointer => pointer,
        long pointer => unchecked((ulong)pointer),
        uint pointer => pointer,
        int pointer => unchecked((uint)pointer),
        _ => 0
    };

    // 与现有 ProcessStartKey 同一口径：进程启动时间的 UTC FILETIME。
    private static long? ReadProcessStartKey(int processId)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(processId);
            return process.StartTime.ToFileTimeUtc();
        }
        catch
        {
            return null;
        }
    }

    private void Release()
    {
        lock (lifecycle)
        {
            if (leases > 0 && --leases == 0)
            {
                Stop();
            }
        }
    }

    private void Stop()
    {
        flushTimer?.Dispose();
        flushTimer = null;
        var current = session;
        session = null;
        current?.Dispose();
        if (processor is not null && !processor.Wait(TimeSpan.FromSeconds(3)))
        {
            logger.LogWarning("Present frame event processing did not stop within 3 seconds.");
        }

        processor = null;
        lock (gate) ledger = new PresentFrameLedger(ReadProcessStartKey);
    }

    public void Dispose()
    {
        lock (lifecycle)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            leases = 0;
            Stop();
        }
    }

    private sealed class Lease(PresentFrameEtwSource owner) : IDisposable
    {
        private PresentFrameEtwSource? owner = owner;
        public void Dispose() => Interlocked.Exchange(ref owner, null)?.Release();
    }
}
