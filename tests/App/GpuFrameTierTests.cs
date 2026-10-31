using System.ComponentModel;
using System.Reflection;
using System.Runtime.InteropServices;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Infrastructure.Telemetry.Etw;
using ResourceManager.ReadWrite.Windows.Frames;

namespace Resource_Manager_APP.Tests;

public sealed class GpuFrameTierTests
{
    private const int Pid = 41;
    private const long Birth = 111;
    private static readonly long Epoch = DateTimeOffset.UnixEpoch.AddDays(1).UtcTicks;
    private static long At(int ms) => Epoch + TimeSpan.FromMilliseconds(ms).Ticks;
    private static (PresentFrameLedger Ledger, GpuFrameTracker Tracker) Create(GpuFrameTrackerLimits? limits = null)
    {
        var ledger = new PresentFrameLedger(static _ => Birth, MonitoringSettings.ProductDefaults.FramePipeline.LedgerLimits);
        var tracker = new GpuFrameTracker(static _ => Birth, limits ?? new(100, 100, 10), static pid => pid == Pid);
        ledger.GpuFrames = tracker;
        ledger.ProcessStarted(Pid, Birth);
        tracker.DeviceStarted(1, 100, Pid);
        tracker.ContextStarted(2, 1, 0, 0); // DCStart inherits Device owner, not the tracing process.
        return (ledger, tracker);
    }
    private static void Present(PresentFrameLedger ledger, int start, int stop)
    {
        ledger.Present(Pid, FramePresentSource.Dxgi, 10, At(start), 7, At(start), TimeSpan.TicksPerSecond);
        ledger.PresentStopped(Pid, 7, At(stop), At(stop), 0);
    }

    [Fact]
    public void DmaContextAndPresentQueueCompletionProduceGpuTimingAndWindowStatistics()
    {
        var (ledger, tracker) = Create();
        Present(ledger, 0, 2);
        tracker.DmaStarted(2, 1, At(4)); tracker.DmaCompleted(2, 1, At(7));
        tracker.DmaStarted(2, 2, At(9)); tracker.DmaCompleted(2, 2, At(13));
        ledger.Present(Pid, FramePresentSource.Dxgi, 10, At(10), 7, At(10), TimeSpan.TicksPerSecond);
        tracker.QueueStarted(2, 3, Pid, ledger.CurrentGpuOwner(Pid, 7));
        ledger.PresentStopped(Pid, 7, At(11), At(11), 0);
        tracker.QueueStopped(2, 3, At(14));
        var gpu = Assert.IsType<FrameGpuTiming>(Assert.Single(ledger.ReadIntervals(At(-1), At(20))).Gpu);
        Assert.Equal(2d, gpu.LatencyMs); // first DMA 4 - previous Present stop 2
        Assert.Equal(7d, gpu.BusyMs); Assert.Equal(2d, gpu.WaitMs); Assert.Equal(9d, gpu.FrameTimeMs);
        Assert.Null(gpu.VideoBusyMs);
        var stats = Assert.Single(Assert.Single(ledger.Read(At(20), TimeSpan.FromSeconds(1))).Streams).Gpu!;
        Assert.Equal(7d, stats.Busy.AverageMs); Assert.Equal(7d, stats.Busy.OnePercentHighMs);
        Assert.Equal(9d, stats.FrameTime.P99Ms);
        tracker.Refuse(FrameTimingReasons.EventsLost);
        var unavailable = Assert.Single(Assert.Single(ledger.Read(At(20), TimeSpan.FromSeconds(1))).Streams);
        Assert.Null(unavailable.Gpu); Assert.Equal(FrameTimingReasons.EventsLost, unavailable.GpuUnavailableReason);
    }

    [Fact]
    public void QueuedDmaOnOneNodeWaitsForTheFrontAndParallelNodesCountOnlyOnce()
    {
        var (ledger, tracker) = Create();
        tracker.ContextStarted(3, 1, 1, Pid);
        // An unrequested process occupies node 0 first. Its DMA cannot be charged to Pid.
        tracker.DeviceStarted(4, 100, 99); tracker.ContextStarted(5, 4, 0, 99);
        Present(ledger, 0, 1);
        tracker.DmaStarted(5, 1, At(2));
        tracker.DmaStarted(2, 2, At(3));
        tracker.DmaCompleted(5, 1, At(5)); // Pid starts executing on node 0 at 5, not enqueue time 3.
        tracker.DmaStarted(3, 3, At(6));
        tracker.DmaCompleted(2, 2, At(9)); tracker.DmaCompleted(3, 3, At(10));
        ledger.Present(Pid, FramePresentSource.Dxgi, 10, At(11), 7, At(11), TimeSpan.TicksPerSecond);
        tracker.QueueStarted(2, 4, Pid, ledger.CurrentGpuOwner(Pid, 7));
        ledger.PresentStopped(Pid, 7, At(12), At(12), 0);
        tracker.QueueStopped(2, 4, At(13));
        var gpu = Assert.Single(ledger.ReadIntervals(At(-1), At(20))).Gpu!;
        Assert.Equal(4d, gpu.LatencyMs); Assert.Equal(5d, gpu.BusyMs); Assert.Equal(0d, gpu.WaitMs);
    }

    [Fact]
    public void RundownCanBindAnUnknownContextOwnerFromQueueSubmissionAndNotInterruptPid()
    {
        var (ledger, tracker) = Create();
        tracker.DeviceStarted(8, 100, 0); tracker.ContextStarted(9, 8, 1, 0);
        tracker.QueueStarted(9, 1, Pid, null);
        Present(ledger, 0, 1);
        tracker.DmaStarted(9, 2, At(2)); tracker.DmaCompleted(9, 2, At(5));
        ledger.Present(Pid, FramePresentSource.Dxgi, 10, At(6), 7, At(6), TimeSpan.TicksPerSecond);
        tracker.QueueStarted(9, 3, Pid, ledger.CurrentGpuOwner(Pid, 7));
        tracker.QueueStopped(9, 3, At(8));
        Assert.Equal(3d, Assert.Single(ledger.ReadIntervals(At(-1), At(20))).Gpu!.BusyMs);
        tracker.ProcessStopped(Pid, At(9));
        Assert.Equal(0, tracker.PacketCount);
        Assert.Equal(0, tracker.PendingFrameCount);
    }

    [Fact]
    public void HardwareSchedulingAndMissingPacketSequencesCarryConcreteReasons()
    {
        var (_, tracker) = Create(); tracker.HardwareQueue(2);
        Assert.Equal(FrameTimingReasons.GpuHardwareScheduledQueue, tracker.UnavailableReason(Pid, Birth));
        Assert.Equal(FrameTimingReasons.GpuTierNotRequested, tracker.UnavailableReason(99, Birth));
        var (_, missing) = Create(); missing.DmaStarted(2, 1, At(1)); missing.DmaStarted(2, 2, At(2));
        missing.DmaCompleted(2, 2, At(3));
        Assert.Equal(FrameTimingReasons.GpuPacketSequenceIncomplete, missing.UnavailableReason(Pid, Birth));
        Assert.Equal(0, missing.PacketCount);
    }

    [Fact]
    public void PacketObjectAndPendingFrameCapsRefuseWithoutUnboundedRetention()
    {
        var (_, packets) = Create(new(100, 1, 10));
        packets.DmaStarted(2, 1, At(1)); packets.DmaStarted(2, 2, At(2));
        Assert.Equal(FrameTimingReasons.GpuTrackerCapacity, packets.Failure); Assert.Equal(0, packets.PacketCount);
        var (_, objects) = Create(new(2, 10, 10)); objects.ContextStarted(3, 1, 1, Pid);
        Assert.Equal(FrameTimingReasons.GpuTrackerCapacity, objects.Failure); Assert.Equal(0, objects.ObjectCount);
        var (ledger, frames) = Create(new(100, 10, 1));
        ledger.Present(Pid, FramePresentSource.Dxgi, 10, At(1), 7, At(1), TimeSpan.TicksPerSecond);
        frames.QueueStarted(2, 1, Pid, ledger.CurrentGpuOwner(Pid, 7));
        frames.QueueStarted(2, 2, Pid, ledger.CurrentGpuOwner(Pid, 7));
        Assert.Equal(FrameTimingReasons.GpuTrackerCapacity, frames.Failure); Assert.Equal(0, frames.PendingFrameCount);
    }

    [Fact]
    public async Task GpuEventsOpenOnlyWithTheTierAndCloseOnReleaseWithoutASecondSession()
    {
        await using var harness = PresentFrameEtwTestHarness.Create();
        var basic = harness.Source.AcquireSubscription(new([Pid], FrameDetail.None));
        await harness.WaitRunningAsync();
        var session = Assert.Single(harness.Sessions.Snapshot());
        PresentEtwProviderOperation Kernel() => session.Operations.Last(item => item.Kind == "enable" && item.Provider == PresentFrameEtwSource.GraphicsKernelProvider);
        Assert.DoesNotContain(175, Kernel().EventIds!);
        var gpu = harness.Source.AcquireSubscription(new([Pid], FrameDetail.GpuFrames));
        Assert.Contains(175, Kernel().EventIds!); Assert.Contains(177, Kernel().EventIds!); Assert.Contains(180, Kernel().EventIds!);
        Assert.Contains(29, Kernel().EventIds!); Assert.Contains(32, Kernel().EventIds!); Assert.Contains(424, Kernel().EventIds!);
        Assert.Null(Kernel().ProcessIds);
        Assert.Single(session.Operations, item => item.Kind == "capture-state" && item.Provider == PresentFrameEtwSource.GraphicsKernelProvider);
        gpu.Dispose(); Assert.DoesNotContain(175, Kernel().EventIds!);
        Assert.Single(harness.Sessions.Snapshot());
        var ledger = (PresentFrameLedger)typeof(PresentFrameEtwSource).GetField("ledger", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(harness.Source)!;
        Assert.Null(ledger.GpuFrames);
        await harness.ReleaseAsync(basic);
    }

    [Fact]
    public async Task FirstCombinedTierRequestsRundownAfterAllDisplayProvidersAreEnabled()
    {
        await using var harness = PresentFrameEtwTestHarness.Create();
        var lease = harness.Source.AcquireSubscription(new([Pid], FrameDetail.GpuFrames | FrameDetail.OutputResolution));
        await harness.WaitRunningAsync();
        var operations = Assert.Single(harness.Sessions.Snapshot()).Operations;
        var win32k = Array.FindIndex(operations, item => item.Kind == "enable" && item.Provider == PresentFrameEtwSource.Win32kProvider);
        var dxgiCapture = Array.FindIndex(operations, item => item.Kind == "capture-state" && item.Provider == PresentFrameEtwSource.DxgiProvider);
        var gpuCapture = Array.FindIndex(operations, item => item.Kind == "capture-state" && item.Provider == PresentFrameEtwSource.GraphicsKernelProvider);
        Assert.True(win32k >= 0 && dxgiCapture > win32k && gpuCapture > win32k);
        await harness.ReleaseAsync(lease);
    }

    [Fact]
    public async Task GpuProviderSessionLimitRestoresBasicEventsAndSurfacesReason()
    {
        await using var harness = PresentFrameEtwTestHarness.Create();
        var basic = harness.Source.AcquireSubscription(); await harness.WaitRunningAsync();
        var session = Assert.Single(harness.Sessions.Snapshot());
        session.AfterEnable = provider =>
        {
            if (provider != PresentFrameEtwSource.GraphicsKernelProvider) return;
            session.AfterEnable = null;
            throw new Win32Exception(1450);
        };
        using var gpu = harness.Source.AcquireSubscription(new([Pid], FrameDetail.GpuFrames));
        Assert.True(harness.Source.HasActiveSubscriptions);
        var ledger = (PresentFrameLedger)typeof(PresentFrameEtwSource).GetField("ledger", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(harness.Source)!;
        Assert.Equal(FrameTimingReasons.GpuEtwProviderSessionLimit, ledger.GpuFrames!.UnavailableReason(Pid, Birth));
        var kernel = session.Operations.Last(item => item.Kind == "enable" && item.Provider == PresentFrameEtwSource.GraphicsKernelProvider);
        Assert.DoesNotContain(175, kernel.EventIds!);
        await harness.ReleaseAsync(basic);
    }

    [Theory]
    [InlineData(27, 2, 28)] [InlineData(32, 0, 48)] [InlineData(175, 1, 32)]
    [InlineData(177, 1, 24)] [InlineData(180, 1, 32)] [InlineData(424, 0, 24)]
    public void GpuPayloadPrefixesAreBoundedAndUnknownVersionsAreRejected(int id, int version, int length)
    {
        var pointer = Marshal.AllocHGlobal(length);
        try
        {
            var catalog = PresentFrameEtwLayouts.Catalog;
            Assert.Equal(EtwFixedLayoutStatus.Decoded, catalog.Find(PresentFrameEtwSource.GraphicsKernelProvider, id, version, new EtwRawPayload(pointer, length, 8), out _));
            Assert.Equal(EtwFixedLayoutStatus.ShortPayload, catalog.Find(PresentFrameEtwSource.GraphicsKernelProvider, id, version, new EtwRawPayload(pointer, length - 1, 8), out _));
            Assert.Equal(EtwFixedLayoutStatus.UnknownVersion, catalog.Find(PresentFrameEtwSource.GraphicsKernelProvider, id, version + 10, new EtwRawPayload(pointer, length, 8), out _));
        }
        finally { Marshal.FreeHGlobal(pointer); }
    }
}
