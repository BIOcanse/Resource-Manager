using BlockUids = ResourceManager.App.StateItems.BlockUids;
using Microsoft.Diagnostics.Tracing;
using ResourceManager.ReadWrite.Windows.Processes.Etw;
using ResourceManager.ReadWrite.Windows.Frames;
using ResourceManager.App.Application.RuntimeSpecialization.BusinessBlocks;
using ResourceManager.App.Domain.GpuPlacement;
using ResourceManager.App.Infrastructure.RuntimeSpecialization.BusinessBlocks;

namespace ResourceManager.App.Infrastructure.Telemetry.Etw;

/// <summary>
/// 按进程采集 Present 帧时间。独立 ETW 会话，不并入内核会话：内核会话在订阅变化或丢事件时会整体重启，
/// 高频的 Present 事件不能连累其他采集。订阅活动由上级决策节点判断；运行入口独占会话的取得和释放。
/// 事件口径见 docs/performance_overlay_and_targeted_report_2026-10-02.md 的“第 1 片合同”。
/// </summary>
public sealed partial class PresentFrameEtwSource(
    ILogger<PresentFrameEtwSource> logger,
    BusinessBlockScheduler controller,
    BusinessBlockDiagnosticsRegistry diagnostics,
    NativeEtwSessionAvailability availability,
    Func<EtwSessionBuffers> readSessionBuffers,
    IEtwConsumerThreadObserver? consumerThreadObserver = null)
    : IFrameTimingObservationSource, IBusinessBlock, IDisposable
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

    private readonly object lifecycle = new();
    private readonly Func<string, IPresentFrameEtwSession> createSession = name =>
    {
        ProcessOwnedEtwSessionOrphanReconciler.CreateDefault(logger).Reconcile(
            ResourceManagerEtwSessionNames.PresentFramesPrefix, ResourceManagerEtwSessionNames.CurrentOwner, name);
        // The buffer pool is read when each session is created.
        return new TraceEventPresentFrameEtwSession(name, readSessionBuffers(), logger);
    };
    private readonly TimeProvider timeProvider = TimeProvider.System;
    private readonly Func<GpuPlacementProcessInstance, bool> isCurrentGraphicsApiProcess =
        static process => ReadProcessStartKey(process.ProcessId) == (long)process.ProcessStartKey;
    private readonly object gate = new();
    private PresentFrameLedger ledger = new(ReadProcessStartKey);
    private readonly HashSet<FrameSubscription> frameSubscriptions = [];
    private int frameLeases;
    private long nextHolderId;
    private bool hasActiveSubscriptions;
    private bool disposed;
    private bool hasFrameCurrent;
    private long frameStoppedAt;
    private int observedEventsLost;
    private long lastLossUtcTicks;

    internal PresentFrameEtwSource(ILogger<PresentFrameEtwSource> logger,
        BusinessBlockScheduler controller,
        BusinessBlockDiagnosticsRegistry diagnostics,
        NativeEtwSessionAvailability availability,
        Func<string, IPresentFrameEtwSession> createSession,
        TimeProvider? timeProvider = null,
        IEtwConsumerThreadObserver? consumerThreadObserver = null,
        Func<GpuPlacementProcessInstance, bool>? isCurrentGraphicsApiProcess = null)
        : this(logger, controller, diagnostics, availability, InjectedSessionBuffers, consumerThreadObserver)
    {
        this.createSession = createSession;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        if (isCurrentGraphicsApiProcess is not null) this.isCurrentGraphicsApiProcess = isCurrentGraphicsApiProcess;
    }

    // An injected session creator sizes its own sessions; nothing reads this.
    private static EtwSessionBuffers InjectedSessionBuffers()
        => throw new InvalidOperationException("Injected Present sessions own their buffer pool.");

    public IDisposable AcquireSubscription()
    {
        lock (lifecycle)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var wasActive = HasActiveSubscriptions;
            var previousFrameLeases = frameLeases;
            var subscription = new FrameSubscription(this, ++nextHolderId);
            frameSubscriptions.Add(subscription);
            Volatile.Write(ref frameLeases, frameSubscriptions.Count);
            UpdateOutputProviders(previousFrameLeases);
            PublishSubscriptionActivity(wasActive);
            return subscription;
        }
    }

    public bool HasActiveSubscriptions => Volatile.Read(ref hasActiveSubscriptions);
    public event Action? SubscriptionsChanged;
    internal event Action? HoldersChanged;
    private void PublishSubscriptionActivity(bool wasActive)
    {
        bool active;
        lock (gate) active = frameSubscriptions.Count != 0 || graphicsApiSubscriptions.Any(item => item.NeedsEvents);
        Volatile.Write(ref hasActiveSubscriptions, active);
        HoldersChanged?.Invoke();
        if (wasActive != active) SubscriptionsChanged?.Invoke();
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

    private void UpdateProviders(IPresentFrameEtwSession current, int? previousFrameLeases)
    {
        var run = activeRun!;
        int[] observedPids;
        lock (gate) observedPids = graphicsApiSubscriptions.Where(item => item.NeedsEvents)
            .Select(item => item.Process.ProcessId).Distinct().Order().ToArray();
        var pending = observedPids.Length != 0;
        var frames = frameLeases > 0;
        // ETW permits at most eight PIDs per provider. Above that limit preserve
        // coverage on this session; frame consumers also require all processes.
        var pidFilter = !frames && observedPids.Length <= 8 ? observedPids : null;
        var desired = new List<ProviderSelection>();
        // Only the frame ledger consumes machine-wide process lifetimes.
        // API-only attempts validate birth on the matching Present instead.
        if (frames) desired.Add(new(KernelProcessProvider, TraceEventLevel.Informational, KernelProcessKeyword,
            [ProcessStart, ProcessStop]));
        if (frames || pending)
        {
            desired.Add(new(DxgiProvider, TraceEventLevel.Verbose, RuntimeEventsKeyword,
                frames ? [DxgiPresentStart, DxgiPresentStop, DxgiMultiplaneOverlayPresentStart, DxgiMultiplaneOverlayPresentStop]
                    : [DxgiPresentStart, DxgiMultiplaneOverlayPresentStart], pidFilter));
            desired.Add(new(Direct3D9Provider, TraceEventLevel.Verbose, RuntimeEventsKeyword,
                frames ? [Direct3D9PresentStart, Direct3D9PresentStop] : [Direct3D9PresentStart], pidFilter));
        }
        if (frames)
        {
            desired.Add(new(GraphicsKernelProvider, TraceEventLevel.Verbose, GraphicsKernelDisplayKeyword,
                [GraphicsKernelPresent, GraphicsKernelFlip, GraphicsKernelFlipMultiPlaneOverlay,
                    GraphicsKernelQueuePacketStart, GraphicsKernelMmioFlip, GraphicsKernelVSyncDpc]));
            desired.Add(new(Win32kProvider, TraceEventLevel.Verbose, Win32kDisplayKeyword,
                [Win32kTokenCompositionSurfaceObject, Win32kTokenStateChanged]));
        }
        foreach (var selection in desired)
        {
            if (run.Providers.TryGetValue(selection.Provider, out var old) && old.SameFilter(selection)) continue;
            current.EnableProvider(selection.Provider, selection.Level, selection.Keywords, selection.EventIds, selection.ProcessIds);
            run.Providers[selection.Provider] = selection;
        }
        foreach (var provider in run.Providers.Keys.Where(provider => !desired.Any(item => item.Provider == provider)).ToArray())
        {
            current.DisableProvider(provider);
            run.Providers.Remove(provider);
        }
        if (frameLeases > 0 && (previousFrameLeases is null or 0))
        {
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
            using (diagnostics.MeasureSegment(BlockUids.PresentFrameEtwSource))
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

    private bool OnEvent(TraceEvent data)
    {
        bool used;
        var graphicsApiUseChanged = false;
        // The session already dropped consumed events without a matching fixed layout.
        if (PresentFrameEtwLayouts.Catalog.Find(data, out var payload) is EtwFixedLayoutStatus.UnknownVersion or EtwFixedLayoutStatus.ShortPayload)
            return false;
        lock (gate)
        {
            used = false;
            try
            {
                graphicsApiUseChanged = used = ApplyGraphicsApiEvent(data, payload);
                // An API-only lease uses the supplied process birth identity. It does not
                // invoke the frame ledger's process-handle fallback for other processes.
                if (Volatile.Read(ref frameLeases) > 0
                    && data.TimeStamp.ToUniversalTime().Ticks >= lastLossUtcTicks) used |= Apply(ledger, data, payload);
            }
            catch (Exception error)
            {
                SignalFailure(error);
                logger.LogWarning(error, "Present frame event {Provider}/{Event} could not be applied.", data.ProviderGuid, (int)data.ID);
            }
        }
        // Provider mutation/authority notification must leave the aggregation
        // gate first: a control callback can bind or drain on another thread.
        if (graphicsApiUseChanged) SynchronizeGraphicsApiUse();
        return used;
    }

    private sealed record ProviderSelection(Guid Provider, TraceEventLevel Level, ulong Keywords, int[] EventIds, int[]? ProcessIds = null)
    {
        internal bool SameFilter(ProviderSelection other) => Level == other.Level && Keywords == other.Keywords
            && EventIds.SequenceEqual(other.EventIds)
            && (ProcessIds is null ? other.ProcessIds is null : other.ProcessIds is not null && ProcessIds.SequenceEqual(other.ProcessIds));
    }

    // Payload fields come from the event's fixed layout (PresentFrameEtwLayouts); the
    // values equal what TraceEvent's dynamic payload produced for the same manifest fields.
    internal static bool Apply(PresentFrameLedger target, TraceEvent data, EtwFixedPayload payload)
    {
        var id = (int)data.ID;
        if (data.ProviderGuid == KernelProcessProvider)
        {
            if (id == ProcessStart && payload.TryFileTime((int)PresentFrameField.CreateTime, out var created))
            {
                target.ProcessStarted(payload.Int32((int)PresentFrameField.ProcessID), created.ToFileTimeUtc());
                return true;
            }
            else if (id == ProcessStop)
            {
                target.ProcessStopped(payload.Int32((int)PresentFrameField.ProcessID));
                return true;
            }

            return false;
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
                payload.Pointer((int)PresentFrameField.pIDXGISwapChain), timestamp, threadId, timestampQpc, qpcFrequency);
            if (id == DxgiMultiplaneOverlayPresentStart
                || (payload.UInt32((int)PresentFrameField.Flags) & 1) != 0)
            {
                target.UnsupportedDisplayPath(data.ProcessID, threadId);
            }
        }
        else if (data.ProviderGuid == DxgiProvider && id is DxgiPresentStop or DxgiMultiplaneOverlayPresentStop)
        {
            target.PresentStopped(data.ProcessID, data.ThreadID, timestamp, timestampQpc,
                payload.Int32((int)PresentFrameField.Result));
        }
        else if (data.ProviderGuid == Direct3D9Provider && id == Direct3D9PresentStart)
        {
            target.Present(data.ProcessID, FramePresentSource.Direct3D9,
                payload.Pointer((int)PresentFrameField.pSwapchain), timestamp, data.ThreadID, timestampQpc, qpcFrequency);
        }
        else if (data.ProviderGuid == Direct3D9Provider && id == Direct3D9PresentStop)
        {
            target.PresentStopped(data.ProcessID, data.ThreadID, timestamp, timestampQpc,
                payload.Int32((int)PresentFrameField.Result));
        }
        else if (data.ProviderGuid == GraphicsKernelProvider && id == GraphicsKernelPresent)
        {
            target.Present(data.ProcessID, FramePresentSource.GraphicsKernel, 0, timestamp);
        }
        else if (data.ProviderGuid == GraphicsKernelProvider && id == GraphicsKernelFlip)
        {
            target.LegacyFlip(data.ProcessID, data.ThreadID,
                payload.Boolean((int)PresentFrameField.MMIOFlip), timestampQpc);
        }
        else if (data.ProviderGuid == GraphicsKernelProvider && id == GraphicsKernelFlipMultiPlaneOverlay)
        {
            target.UnsupportedDisplayPath(data.ProcessID, data.ThreadID,
                payload.UInt32((int)PresentFrameField.VidPnSourceId));
        }
        else if (data.ProviderGuid == GraphicsKernelProvider && id == GraphicsKernelQueuePacketStart)
        {
            target.QueuePacketStart(data.ProcessID, data.ThreadID,
                payload.UInt32((int)PresentFrameField.SubmitSequence),
                payload.Pointer((int)PresentFrameField.hContext),
                payload.UInt32((int)PresentFrameField.PacketType),
                payload.Boolean((int)PresentFrameField.bPresent), timestampQpc);
        }
        else if (data.ProviderGuid == GraphicsKernelProvider && id == GraphicsKernelMmioFlip)
        {
            target.MmioFlip(payload.UInt32((int)PresentFrameField.FlipSubmitSequence),
                timestamp, timestampQpc,
                (payload.UInt32((int)PresentFrameField.Flags) & FlipImmediate) != 0);
        }
        else if (data.ProviderGuid == GraphicsKernelProvider && id == GraphicsKernelVSyncDpc)
        {
            var fenceId = payload.UInt64((int)PresentFrameField.FlipFenceId);
            var submitSequence = (uint)(fenceId >> 32);
            if (submitSequence != 0) target.VSyncDpc(submitSequence, timestamp, timestampQpc);
            else return false;
        }
        else if (data.ProviderGuid == Win32kProvider && id == Win32kTokenCompositionSurfaceObject)
        {
            target.TrackWin32kToken(data.ProcessID, data.ThreadID,
                payload.UInt64((int)PresentFrameField.CompositionSurfaceLuid),
                payload.UInt64((int)PresentFrameField.PresentCount),
                payload.UInt64((int)PresentFrameField.BindId));
        }
        else if (data.ProviderGuid == Win32kProvider && id == Win32kTokenStateChanged)
        {
            target.Win32kTokenStateChanged(
                payload.UInt64((int)PresentFrameField.CompositionSurfaceLuid),
                payload.UInt32((int)PresentFrameField.PresentCount),
                payload.UInt64((int)PresentFrameField.BindId),
                payload.UInt32((int)PresentFrameField.NewState), timestamp, timestampQpc);
        }
        else return false;
        return true;
    }

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
