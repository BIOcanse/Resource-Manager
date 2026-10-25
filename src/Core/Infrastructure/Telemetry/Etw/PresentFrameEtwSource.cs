using Microsoft.Diagnostics.Tracing;
using ResourceManager.ReadWrite.Windows.Processes.Etw;
using ResourceManager.Adapter;
using ResourceManager.App.Application.Adaptation;
using ResourceManager.App.Application.Monitoring;
using ResourceManager.App.Domain.Adaptation;
using ResourceManager.ReadWrite.Windows.Frames;
using ResourceManager.App.Application.RuntimeSpecialization.BusinessBlocks;
using ResourceManager.App.Infrastructure.RuntimeSpecialization.BusinessBlocks;

namespace ResourceManager.App.Infrastructure.Telemetry.Etw;

/// <summary>
/// 按进程采集 Present 帧时间。独立 ETW 会话，不并入内核会话：内核会话在订阅变化或丢事件时会整体重启，
/// 高频的 Present 事件不能连累其他采集。有订阅才开，最后一个订阅释放就停；功能区冻结时停。
/// 事件口径见 docs/performance_overlay_and_targeted_report_2026-10-02.md 的“第 1 片合同”。
/// </summary>
public sealed partial class PresentFrameEtwSource(
    ILogger<PresentFrameEtwSource> logger,
    BusinessBlockController controller,
    IEtwConsumerThreadObserver? consumerThreadObserver = null)
    : IFrameTimingObservationSource, IResourceManagerSelfComputeZone, IBusinessBlock, IBusinessBlockStartup, IDisposable
{
    internal static readonly Guid DxgiProvider = new("CA11C036-0102-4A2D-A6AD-F03CFED5D3C9");
    internal static readonly Guid Direct3D9Provider = new("783ACA0A-790E-4D7F-8451-AA850511C6B9");
    internal static readonly Guid GraphicsKernelProvider = new("802EC45A-1E99-4B83-9920-87C98277BA9D");
    internal static readonly Guid KernelProcessProvider = new("22FB2CD6-0E7B-422B-A0C7-2FAD1FD0E716");
    internal static readonly Guid Win32kProvider = new("8C416C79-D49B-4F01-A467-E56D3AA8234C");
    private const ulong RuntimeEventsKeyword = 0x2;
    private const ulong GraphicsKernelDisplayKeyword = 0x4000000008000001;
    private const ulong Win32kDisplayKeyword = 0x8000000400001000;
    private const ulong KernelProcessKeyword = 0x10;
    private const int DxgiPresentStart = 42;
    private const int DxgiPresentStop = 43;
    private const int DxgiMultiplaneOverlayPresentStart = 55;
    private const int DxgiMultiplaneOverlayPresentStop = 56;
    private const int Direct3D9PresentStart = 1;
    private const int Direct3D9PresentStop = 2;
    private const int GraphicsKernelPresent = 184;
    private const int GraphicsKernelFlip = 168;
    private const int GraphicsKernelFlipMultiPlaneOverlay = 252;
    private const int GraphicsKernelQueuePacketStart = 178;
    private const int GraphicsKernelMmioFlip = 116;
    private const int GraphicsKernelVSyncDpc = 17;
    private const int Win32kTokenCompositionSurfaceObject = 201;
    private const int Win32kTokenStateChanged = 301;
    private const uint FlipImmediate = 2;
    private const int ProcessStart = 1;
    private const int ProcessStop = 2;
    // 帧事件量小，实时缓冲很久才会写满；定时冲刷把投递延迟限制在这个间隔内（叠加层按 250–500 ms 刷新）。
    internal static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(250);
    private static readonly ulong ZoneObjectKey =
        AdapterResourceKey.FromString($"resource-manager:zone:{MonitoringSourceZoneIds.EtwPresentFrames}");

    private readonly object lifecycle = new();
    private readonly Func<string, IPresentFrameEtwSession> createSession = name =>
    {
        ProcessOwnedEtwSessionOrphanReconciler.CreateDefault(logger).Reconcile(
            ResourceManagerEtwSessionNames.PresentFramesPrefix, ResourceManagerEtwSessionNames.CurrentOwner, name);
        return new TraceEventPresentFrameEtwSession(name);
    };
    private readonly TimeProvider timeProvider = TimeProvider.System;
    private readonly object gate = new();
    private PresentFrameLedger ledger = new(ReadProcessStartKey);
    private IPresentFrameEtwSession? session;
    private ITimer? flushTimer;
    private readonly HashSet<FrameSubscription> frameSubscriptions = [];
    private int frameLeases;
    private bool disposed;
    private bool hasFrameCurrent;
    private long frameStoppedAt;
    private int observedEventsLost;
    private long lastLossUtcTicks;
    private ResourceManagerComputeZoneMode mode = ResourceManagerComputeZoneMode.Normal;

    public ulong ZoneKey => ZoneObjectKey;
    public string DisplayName => MonitoringSourceZoneIds.EtwPresentFrames;
    public ResourceManagerComputeZoneMode CurrentMode => mode;

    internal PresentFrameEtwSource(ILogger<PresentFrameEtwSource> logger,
        BusinessBlockController controller,
        Func<string, IPresentFrameEtwSession> createSession,
        TimeProvider? timeProvider = null,
        IEtwConsumerThreadObserver? consumerThreadObserver = null) : this(logger, controller, consumerThreadObserver)
    {
        this.createSession = createSession;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public IDisposable AcquireSubscription()
    {
        lock (lifecycle)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var previousFrameLeases = frameLeases;
            var dependency = controller.AcquireDependency("present-etw",
                new(BusinessBlockRequestKind.Dependency, "frame-observation", "present-frame-subscription", controller.CurrentPlanVersion),
                outputs: ["frame-timing"]);
            var subscription = new FrameSubscription(this, dependency);
            frameSubscriptions.Add(subscription);
            Volatile.Write(ref frameLeases, frameSubscriptions.Count(value => !value.Released.IsCompleted));
            UpdateOutputDemand(previousFrameLeases);
            subscription.ObserveRelease();
            return subscription;
        }
    }

    public FrameTimingSnapshot? Read(TimeSpan window)
    {
        lock (gate)
        {
            if (!hasFrameCurrent) return null;
            var now = frameStoppedAt != 0 ? frameStoppedAt : timeProvider.GetUtcNow().UtcTicks;
            var complete = (lastLossUtcTicks == 0 || lastLossUtcTicks < now - window.Ticks)
                && ledger.IsComplete(now - window.Ticks);
            return new FrameTimingSnapshot(
                new DateTimeOffset(now, TimeSpan.Zero), window, complete, ledger.Read(now, window))
            {
                LastPresents = ledger.ReadLastPresents(now, window),
                DisplayStreams = ledger.ReadDisplayStreams(now, window)
            };
        }
    }

    public FrameIntervalBatch? ReadIntervals(DateTimeOffset after, DateTimeOffset through)
    {
        if (through <= after || through - after > PresentFrameLedger.Retention)
        {
            throw new ArgumentOutOfRangeException(nameof(through));
        }

        lock (gate)
        {
            if (!hasFrameCurrent) return null;
            var throughTicks = frameStoppedAt != 0 ? Math.Min(through.UtcTicks, frameStoppedAt) : through.UtcTicks;
            return new FrameIntervalBatch(through,
                throughTicks == through.UtcTicks && (lastLossUtcTicks == 0 || lastLossUtcTicks < after.UtcTicks)
                    && ledger.IsComplete(after.UtcTicks),
                ledger.ReadIntervals(after.UtcTicks, throughTicks));
        }
    }

    public void ApplyMode(ResourceManagerComputeZoneMode next)
        => _ = ApplyModeAsync(next);

    internal Task ApplyModeAsync(ResourceManagerComputeZoneMode next)
    {
        lock (lifecycle)
        {
            mode = next;
            return controller.SetEnabledAsync("present-etw", next != ResourceManagerComputeZoneMode.Freeze,
                new(BusinessBlockRequestKind.Configuration, "compute-zone", MonitoringSourceZoneIds.EtwPresentFrames, controller.CurrentPlanVersion));
        }
    }

    private void UpdateProviders(IPresentFrameEtwSession current, int? previousFrameLeases)
    {
        if (previousFrameLeases is null)
        {
            current.EnableProvider(KernelProcessProvider, TraceEventLevel.Informational, KernelProcessKeyword,
                [ProcessStart, ProcessStop]);
            current.EnableProvider(DxgiProvider, TraceEventLevel.Verbose, RuntimeEventsKeyword,
                [DxgiPresentStart, DxgiPresentStop, DxgiMultiplaneOverlayPresentStart, DxgiMultiplaneOverlayPresentStop]);
            current.EnableProvider(Direct3D9Provider, TraceEventLevel.Verbose, RuntimeEventsKeyword,
                [Direct3D9PresentStart, Direct3D9PresentStop]);
        }
        if (frameLeases > 0 && (previousFrameLeases is null or 0))
        {
            current.EnableProvider(GraphicsKernelProvider, TraceEventLevel.Verbose, GraphicsKernelDisplayKeyword,
                [GraphicsKernelPresent, GraphicsKernelFlip, GraphicsKernelFlipMultiPlaneOverlay,
                    GraphicsKernelQueuePacketStart, GraphicsKernelMmioFlip, GraphicsKernelVSyncDpc]);
            current.EnableProvider(Win32kProvider, TraceEventLevel.Verbose, Win32kDisplayKeyword,
                [Win32kTokenCompositionSurfaceObject, Win32kTokenStateChanged]);
            lock (gate)
            {
                // Events buffered before both providers were enabled cannot fill this window.
                ledger = new PresentFrameLedger(ReadProcessStartKey);
                lastLossUtcTicks = timeProvider.GetUtcNow().UtcTicks;
                frameStoppedAt = 0;
                hasFrameCurrent = true;
            }
        }
        else if (frameLeases == 0 && previousFrameLeases > 0)
        {
            current.DisableProvider(GraphicsKernelProvider);
            current.DisableProvider(Win32kProvider);
            lock (gate)
            {
                frameStoppedAt = timeProvider.GetUtcNow().UtcTicks;
                ledger.ReleaseCorrelationState();
            }
        }
    }

    private void Flush(IPresentFrameEtwSession current)
    {
        var run = Volatile.Read(ref activeRun);
        if (run is null || Volatile.Read(ref run.Accepting) == 0 || !ReferenceEquals(run.Session, current)) return;
        try
        {
            using (controller.MeasureSegment("present-etw"))
            {
                current.Flush();
                if (Volatile.Read(ref frameLeases) == 0) return;
                var lost = current.EventsLost;
                lock (gate)
                    if (frameLeases > 0 && lost != observedEventsLost)
                    {
                        observedEventsLost = lost;
                        lastLossUtcTicks = timeProvider.GetUtcNow().UtcTicks;
                        ledger.EventsLost();
                    }
            }
        }
        catch (Exception error)
        {
            SignalFailure(error);
        }
    }

    private void OnEvent(TraceEvent data)
    {
        lock (gate)
        {
            try
            {
                ApplyGraphicsApiEvent(data);
                // An API-only lease uses the supplied process birth identity. It does not
                // invoke the frame ledger's process-handle fallback for other processes.
                if (Volatile.Read(ref frameLeases) > 0
                    && data.TimeStamp.ToUniversalTime().Ticks >= lastLossUtcTicks) Apply(ledger, data);
            }
            catch (Exception error)
            {
                SignalFailure(error);
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
#pragma warning disable CS0618 // Raw QPC ticks are required; relative milliseconds lose integer tick precision.
        var timestampQpc = data.TimeStampQPC;
#pragma warning restore CS0618
        // This callback belongs to our local real-time QPC session (ClientContext=1).
        // Recorded ledger consumers supply their recording's frequency separately.
        var qpcFrequency = System.Diagnostics.Stopwatch.Frequency;
        if (data.ProviderGuid == DxgiProvider && id is DxgiPresentStart or DxgiMultiplaneOverlayPresentStart)
        {
            var threadId = data.ThreadID;
            target.Present(data.ProcessID, FramePresentSource.Dxgi,
                ReadPointer(data.PayloadByName("pIDXGISwapChain")), timestamp, threadId, timestampQpc, qpcFrequency);
            if (id == DxgiMultiplaneOverlayPresentStart
                || (ReadUInt32(data.PayloadByName("Flags")) & 1) != 0)
            {
                target.UnsupportedDisplayPath(data.ProcessID, threadId);
            }
        }
        else if (data.ProviderGuid == DxgiProvider && id is DxgiPresentStop or DxgiMultiplaneOverlayPresentStop)
        {
            target.PresentStopped(data.ProcessID, data.ThreadID, timestamp, timestampQpc,
                unchecked((int)ReadUInt32(data.PayloadByName("Result"))));
        }
        else if (data.ProviderGuid == Direct3D9Provider && id == Direct3D9PresentStart)
        {
            target.Present(data.ProcessID, FramePresentSource.Direct3D9,
                ReadPointer(data.PayloadByName("pSwapchain")), timestamp, data.ThreadID, timestampQpc, qpcFrequency);
        }
        else if (data.ProviderGuid == Direct3D9Provider && id == Direct3D9PresentStop)
        {
            target.PresentStopped(data.ProcessID, data.ThreadID, timestamp, timestampQpc,
                unchecked((int)ReadUInt32(data.PayloadByName("Result"))));
        }
        else if (data.ProviderGuid == GraphicsKernelProvider && id == GraphicsKernelPresent)
        {
            target.Present(data.ProcessID, FramePresentSource.GraphicsKernel, 0, timestamp);
        }
        else if (data.ProviderGuid == GraphicsKernelProvider && id == GraphicsKernelFlip)
        {
            target.LegacyFlip(data.ProcessID, data.ThreadID,
                ReadBoolean(data.PayloadByName("MMIOFlip")), timestampQpc);
        }
        else if (data.ProviderGuid == GraphicsKernelProvider && id == GraphicsKernelFlipMultiPlaneOverlay)
        {
            target.UnsupportedDisplayPath(data.ProcessID, data.ThreadID,
                ReadUInt32(data.PayloadByName("VidPnSourceId")));
        }
        else if (data.ProviderGuid == GraphicsKernelProvider && id == GraphicsKernelQueuePacketStart)
        {
            target.QueuePacketStart(data.ProcessID, data.ThreadID,
                ReadUInt32(data.PayloadByName("SubmitSequence")),
                ReadUInt64(data.PayloadByName("hContext")),
                ReadUInt32(data.PayloadByName("PacketType")),
                ReadBoolean(data.PayloadByName("bPresent")), timestampQpc);
        }
        else if (data.ProviderGuid == GraphicsKernelProvider && id == GraphicsKernelMmioFlip)
        {
            target.MmioFlip(ReadUInt32(data.PayloadByName("FlipSubmitSequence")),
                timestamp, timestampQpc,
                (ReadUInt32(data.PayloadByName("Flags")) & FlipImmediate) != 0);
        }
        else if (data.ProviderGuid == GraphicsKernelProvider && id == GraphicsKernelVSyncDpc)
        {
            var fenceId = ReadUInt64(data.PayloadByName("FlipFenceId"));
            var submitSequence = (uint)(fenceId >> 32);
            if (submitSequence != 0) target.VSyncDpc(submitSequence, timestamp, timestampQpc);
        }
        else if (data.ProviderGuid == Win32kProvider && id == Win32kTokenCompositionSurfaceObject)
        {
            target.TrackWin32kToken(data.ProcessID, data.ThreadID,
                ReadUInt64(data.PayloadByName("CompositionSurfaceLuid")),
                ReadUInt64(data.PayloadByName("PresentCount")),
                ReadUInt64(data.PayloadByName("BindId")));
        }
        else if (data.ProviderGuid == Win32kProvider && id == Win32kTokenStateChanged)
        {
            target.Win32kTokenStateChanged(
                ReadUInt64(data.PayloadByName("CompositionSurfaceLuid")),
                ReadUInt32(data.PayloadByName("PresentCount")),
                ReadUInt64(data.PayloadByName("BindId")),
                ReadUInt32(data.PayloadByName("NewState")), timestamp, timestampQpc);
        }
    }

    private static bool ReadBoolean(object? value) => value switch
    {
        bool flag => flag,
        byte flag => flag != 0,
        sbyte flag => flag != 0,
        ushort flag => flag != 0,
        short flag => flag != 0,
        uint flag => flag != 0,
        int flag => flag != 0,
        ulong flag => flag != 0,
        long flag => flag != 0,
        _ => throw new InvalidDataException("The ETW payload is not a supported Boolean value.")
    };

    private static uint ReadUInt32(object? value)
    {
        var wide = ReadUInt64(value);
        return wide <= uint.MaxValue
            ? (uint)wide
            : throw new InvalidDataException("The ETW payload exceeds an unsigned 32-bit value.");
    }

    private static ulong ReadUInt64(object? value) => value switch
    {
        byte number => number,
        sbyte number => unchecked((byte)number),
        ushort number => number,
        short number => unchecked((ushort)number),
        uint number => number,
        int number => unchecked((uint)number),
        ulong number => number,
        long number => unchecked((ulong)number),
        _ => throw new InvalidDataException("The ETW payload is not an integer value.")
    };

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

}
