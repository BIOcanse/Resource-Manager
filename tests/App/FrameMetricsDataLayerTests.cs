using System.Reflection;
using System.Runtime.InteropServices;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Infrastructure.Telemetry.Etw;
using ResourceManager.ReadWrite.Windows.Frames;

namespace Resource_Manager_APP.Tests;

/// <summary>
/// Frame pipeline data layer (docs/unified_monitoring_items_2026-10-31.md §3.4 phase 1): CPU frame timing,
/// display stream values and reasons, output resolution sources, the PID filter, frame_pipeline freedom
/// points and event-version layout validation. Synthetic event sequences only; no live ETW session.
/// </summary>
public sealed class FrameMetricsDataLayerTests
{
    private const int ProcessId = 4100;
    private const long ProcessBirth = 0x01DC_0000_0000_0001;
    private const ulong SwapChain = 0xA000;
    private const long QpcFrequency = 10_000_000;
    private static readonly long Epoch = new DateTime(2026, 10, 31, 0, 0, 0, DateTimeKind.Utc).Ticks;
    private static readonly PresentFrameLedgerLimits Limits = MonitoringSettings.ProductDefaults.FramePipeline.LedgerLimits;

    private static long Ms(double milliseconds) => Epoch + TimeSpan.FromMilliseconds(milliseconds).Ticks;
    private static long Qpc(double milliseconds) => (long)(milliseconds * QpcFrequency / 1000d) + 1_000_000;

    private static PresentFrameLedger CreateLedger()
    {
        var ledger = new PresentFrameLedger(static _ => ProcessBirth, Limits);
        ledger.ProcessStarted(ProcessId, ProcessBirth);
        return ledger;
    }

    private static void Present(PresentFrameLedger ledger, double startMs, double? stopMs, int threadId = 7)
    {
        ledger.Present(ProcessId, FramePresentSource.Dxgi, SwapChain, Ms(startMs), threadId, Qpc(startMs), QpcFrequency);
        if (stopMs is { } stop) ledger.PresentStopped(ProcessId, threadId, Ms(stop), Qpc(stop), 0);
    }

    [Fact]
    public void CpuBusyWaitAndFrameTimeFollowThePresentMonCpuStartRule()
    {
        var ledger = CreateLedger();
        Present(ledger, 0, 2);
        Present(ledger, 10, 13);
        Present(ledger, 20, 21);

        var intervals = ledger.ReadIntervals(Ms(-1), Ms(30));
        Assert.Equal(2, intervals.Count);
        // cpuStart = previous stop: busy = 10 - 2, wait = 13 - 10, frame = busy + wait = 13 - 2.
        var second = Assert.IsType<FrameCpuTiming>(intervals[0].Cpu);
        Assert.Equal(8d, second.BusyMs, 6);
        Assert.Equal(3d, second.WaitMs, 6);
        Assert.Equal(11d, second.FrameTimeMs, 6);
        var third = Assert.IsType<FrameCpuTiming>(intervals[1].Cpu);
        Assert.Equal(7d, third.BusyMs, 6);
        Assert.Equal(1d, third.WaitMs, 6);
        Assert.Equal(10d, intervals[1].DurationMs, 6);

        var stream = Assert.Single(Assert.Single(ledger.Read(Ms(30), TimeSpan.FromSeconds(1))).Streams);
        var cpu = Assert.IsType<FrameCpuStatistics>(stream.Cpu);
        Assert.Null(stream.CpuUnavailableReason);
        Assert.Equal(2, cpu.Busy.Count);
        Assert.Equal(7.5d, cpu.Busy.AverageMs, 6);
        Assert.Equal(8d, cpu.Busy.MaxMs, 6);
        // 1% "low" equivalent: the slowest ceil(N/100) values averaged, here the single slowest.
        Assert.Equal(8d, cpu.Busy.OnePercentHighMs, 6);
        Assert.Equal(2d, cpu.Wait.AverageMs, 6);
        Assert.Equal(9.5d, cpu.FrameTime.AverageMs, 6);
    }

    [Fact]
    public void OverlappingPresentsClampBusyAtZeroAndKeepTheirOwnWait()
    {
        var ledger = CreateLedger();
        // Thread 7 is still inside Present when thread 8 presents the same swap chain.
        ledger.Present(ProcessId, FramePresentSource.Dxgi, SwapChain, Ms(0), 7, Qpc(0), QpcFrequency);
        ledger.Present(ProcessId, FramePresentSource.Dxgi, SwapChain, Ms(10), 8, Qpc(10), QpcFrequency);
        ledger.PresentStopped(ProcessId, 8, Ms(12), Qpc(12), 0);
        ledger.PresentStopped(ProcessId, 7, Ms(15), Qpc(15), 0);

        var cpu = Assert.IsType<FrameCpuTiming>(Assert.Single(ledger.ReadIntervals(Ms(-1), Ms(20))).Cpu);
        Assert.Equal(0d, cpu.BusyMs);
        Assert.Equal(2d, cpu.WaitMs, 6);
        Assert.Equal(2d, cpu.FrameTimeMs, 6);
    }

    [Fact]
    public void MissingStopsAndKernelStreamsCarryConcreteCpuReasons()
    {
        var ledger = CreateLedger();
        Present(ledger, 0, null);
        Present(ledger, 10, 11);
        var interval = Assert.Single(ledger.ReadIntervals(Ms(-1), Ms(20)));
        Assert.Null(interval.Cpu);
        var stream = Assert.Single(Assert.Single(ledger.Read(Ms(20), TimeSpan.FromSeconds(1))).Streams);
        Assert.Null(stream.Cpu);
        Assert.Equal(FrameTimingReasons.PresentStopNotObserved, stream.CpuUnavailableReason);
        Assert.Equal(FrameTimingReasons.GpuTierNotRequested, stream.GpuUnavailableReason);

        var kernel = new PresentFrameLedger(static _ => ProcessBirth, Limits);
        kernel.ProcessStarted(ProcessId, ProcessBirth);
        kernel.Present(ProcessId, FramePresentSource.GraphicsKernel, 0, Ms(0));
        kernel.Present(ProcessId, FramePresentSource.GraphicsKernel, 0, Ms(10));
        var kernelStream = Assert.Single(Assert.Single(kernel.Read(Ms(20), TimeSpan.FromSeconds(1))).Streams);
        Assert.Equal(FrameTimingReasons.GraphicsKernelHasNoPresentStop, kernelStream.CpuUnavailableReason);
    }

    // Legacy flip displayed through MMIO immediate flip: Present -> Flip(MMIO) -> QueuePacket -> MmioFlip(immediate).
    private static void DisplayedLegacyFlip(PresentFrameLedger ledger, double startMs, double stopMs, double screenMs, uint submitSequence)
    {
        ledger.Present(ProcessId, FramePresentSource.Dxgi, SwapChain, Ms(startMs), 7, Qpc(startMs), QpcFrequency);
        ledger.LegacyFlip(ProcessId, 7, true, Qpc(startMs + 0.1));
        ledger.QueuePacketStart(ProcessId, 7, submitSequence, 0x55, 3, true, Qpc(startMs + 0.2));
        ledger.PresentStopped(ProcessId, 7, Ms(stopMs), Qpc(stopMs), 0);
        ledger.MmioFlip(submitSequence, Ms(screenMs), Qpc(screenMs), true);
    }

    [Fact]
    public void DisplayedFramesExposeDisplayedTimeBetweenChangesLatencyAndPresentMode()
    {
        var ledger = CreateLedger();
        DisplayedLegacyFlip(ledger, 0, 1, 5, 11);
        DisplayedLegacyFlip(ledger, 10, 12, 16, 12);
        DisplayedLegacyFlip(ledger, 20, 21, 30, 13);

        var stream = Assert.Single(ledger.ReadDisplayStreams(Ms(40), TimeSpan.FromSeconds(1)));
        Assert.True(stream.Complete);
        Assert.Empty(stream.IncompleteReasons);
        Assert.Equal(0UL, stream.DroppedCount);
        var frames = stream.Frames.OrderBy(static frame => frame.PresentStartQpc).ToArray();
        Assert.All(frames, static frame => Assert.Equal(FramePresentMode.HardwareLegacyFlip, frame.PresentMode));
        Assert.All(frames, static frame => Assert.Null(frame.UnavailableReason));
        // Between display change: 16 - 5, 30 - 16; displayed time: next screen - this screen.
        Assert.Null(frames[0].BetweenDisplayChangeMs);
        Assert.Equal(11d, frames[1].BetweenDisplayChangeMs!.Value, 3);
        Assert.Equal(14d, frames[2].BetweenDisplayChangeMs!.Value, 3);
        Assert.Equal(11d, frames[0].DisplayedTimeMs!.Value, 3);
        Assert.Null(frames[2].DisplayedTimeMs);
        Assert.Equal(5d, frames[0].UntilDisplayedMs!.Value, 3);
        // Display latency = screen - cpuStart (previous Present's stop); the first frame has no previous stop.
        Assert.Null(frames[0].DisplayLatencyMs);
        Assert.Equal(15d, frames[1].DisplayLatencyMs!.Value, 3);
        Assert.Equal(18d, frames[2].DisplayLatencyMs!.Value, 3);
        var statistics = Assert.IsType<FrameIntervalStatistics>(stream.Statistics);
        Assert.Equal(2, statistics.IntervalCount);
        Assert.Equal(2000d / 25d, statistics.AverageFps, 6);
        Assert.Equal(16.5d, Assert.IsType<FrameDurationStatistics>(stream.DisplayLatency).AverageMs, 3);
        Assert.Equal(12.5d, Assert.IsType<FrameDurationStatistics>(stream.DisplayedTime).AverageMs, 3);
    }

    [Theory]
    [InlineData("mpo")]
    [InlineData("test")]
    [InlineData("flip-without-mmio")]
    [InlineData("composed")]
    [InlineData("no-path")]
    [InlineData("pending")]
    [InlineData("failed")]
    public void UnparsedDisplayPathsCarryTheirConcreteReason(string path)
    {
        var ledger = CreateLedger();
        ledger.Present(ProcessId, FramePresentSource.Dxgi, SwapChain, Ms(0), 7, Qpc(0), QpcFrequency);
        var (expectedReason, expectedMode) = path switch
        {
            "mpo" => (FrameTimingReasons.MultiplaneOverlayNotParsed, FramePresentMode.Unknown),
            "test" => (FrameTimingReasons.TestPresentNotDisplayed, FramePresentMode.Unknown),
            "flip-without-mmio" => (FrameTimingReasons.LegacyFlipWithoutMmioNotParsed, FramePresentMode.Unknown),
            "composed" => (FrameTimingReasons.ComposedScreenTimeNotParsed, FramePresentMode.ComposedFlip),
            "no-path" => (FrameTimingReasons.DisplayPathNotParsed, FramePresentMode.Unknown),
            "failed" => (FrameTimingReasons.PresentFailed, FramePresentMode.Unknown),
            _ => (FrameTimingReasons.DisplayOutcomePending, FramePresentMode.Unknown)
        };
        switch (path)
        {
            case "mpo": ledger.UnsupportedDisplayPath(ProcessId, 7, FrameTimingReasons.MultiplaneOverlayNotParsed, 1); break;
            case "test": ledger.UnsupportedDisplayPath(ProcessId, 7, FrameTimingReasons.TestPresentNotDisplayed); break;
            case "flip-without-mmio": ledger.LegacyFlip(ProcessId, 7, false, Qpc(0.1)); break;
            case "composed":
                ledger.TrackWin32kToken(ProcessId, 7, 0x77, 1, 2, Ms(0.1), 2560, 1440);
                ledger.Win32kTokenStateChanged(0x77, 1, 2, 3, Ms(3), Qpc(3));
                ledger.Win32kTokenStateChanged(0x77, 1, 2, 5, Ms(9), Qpc(9));
                break;
        }
        if (path == "failed") ledger.PresentStopped(ProcessId, 7, Ms(1), Qpc(1), -1);
        else if (path != "pending") ledger.PresentStopped(ProcessId, 7, Ms(1), Qpc(1), 0);

        var stream = Assert.Single(ledger.ReadDisplayStreams(Ms(20), TimeSpan.FromSeconds(1)));
        Assert.False(stream.Complete);
        Assert.Null(stream.Statistics);
        Assert.Null(stream.DroppedCount);
        Assert.Contains(expectedReason, stream.IncompleteReasons);
        var frame = Assert.Single(stream.Frames);
        Assert.Equal(expectedReason, frame.UnavailableReason);
        Assert.Equal(expectedMode, frame.PresentMode);
    }

    [Fact]
    public void LostEventsNameTheReasonOnFramesWithoutOutcome()
    {
        var ledger = CreateLedger();
        ledger.Present(ProcessId, FramePresentSource.Dxgi, SwapChain, Ms(0), 7, Qpc(0), QpcFrequency);
        ledger.EventsLost();
        var frame = Assert.Single(Assert.Single(ledger.ReadDisplayStreams(Ms(20), TimeSpan.FromSeconds(1))).Frames);
        Assert.Equal(FrameTimingReasons.EventsLost, frame.UnavailableReason);
    }

    [Fact]
    public void OutputResolutionKeepsEachSourceAndItsLatestValue()
    {
        var ledger = CreateLedger();
        ledger.SwapChainSize(ProcessId, SwapChain, 1920, 1080, Ms(0));
        Present(ledger, 1, 2);
        ledger.Present(ProcessId, FramePresentSource.Dxgi, SwapChain, Ms(10), 7, Qpc(10), QpcFrequency);
        ledger.TrackWin32kToken(ProcessId, 7, 0x77, 1, 2, Ms(10.1), 2560, 1440);
        ledger.PresentStopped(ProcessId, 7, Ms(11), Qpc(11), 0);
        // A later ResizeBuffers replaces the DXGI value; a zero size (unknown) keeps the last one.
        ledger.SwapChainSize(ProcessId, SwapChain, 3840, 2160, Ms(12));
        ledger.SwapChainSize(ProcessId, SwapChain, 0, 0, Ms(13));
        Present(ledger, 20, 21);

        var stream = Assert.Single(Assert.Single(ledger.Read(Ms(30), TimeSpan.FromSeconds(1))).Streams);
        Assert.Equal(2, stream.OutputResolutions.Count);
        var dxgi = Assert.Single(stream.OutputResolutions, static size => size.Source == FrameOutputResolutionSource.DxgiSwapChain);
        Assert.Equal((3840, 2160), (dxgi.Width, dxgi.Height));
        Assert.Equal(Ms(12), dxgi.ObservedAt.UtcTicks);
        var win32k = Assert.Single(stream.OutputResolutions, static size => size.Source == FrameOutputResolutionSource.Win32kCompositionToken);
        Assert.Equal((2560, 1440), (win32k.Width, win32k.Height));

        // Another swap chain of the same process does not inherit this size.
        ledger.SwapChainSize(ProcessId, SwapChain + 1, 800, 600, Ms(31));
        Assert.Equal(3840, Assert.Single(Assert.Single(ledger.Read(Ms(31), TimeSpan.FromSeconds(1))).Streams)
            .OutputResolutions.Single(static size => size.Source == FrameOutputResolutionSource.DxgiSwapChain).Width);
    }

    [Fact]
    public void SwapChainAndCompositionTokenPayloadsDecodeAtTheDeclaredOffsets()
    {
        var dxgi = PresentFrameEtwSource.DxgiProvider;
        // SwapChain_Start (10) v0 on a 64-bit producer: 4 pointers, 2 bytes, 3 pointers, then Width and Height.
        var created = new byte[10 + 7 * 8];
        BitConverter.GetBytes(0xA000UL).CopyTo(created, 0);
        BitConverter.GetBytes(1920u).CopyTo(created, 2 + 7 * 8);
        BitConverter.GetBytes(1080u).CopyTo(created, 6 + 7 * 8);
        Decode(dxgi, 10, 0, created, payload =>
        {
            Assert.Equal(0xA000UL, payload.Pointer((int)PresentFrameField.pIDXGISwapChain));
            Assert.Equal(1920u, payload.UInt32((int)PresentFrameField.Width));
            Assert.Equal(1080u, payload.UInt32((int)PresentFrameField.Height));
        });
        Decode(dxgi, 12, 0, created, payload => Assert.Equal(1080u, payload.UInt32((int)PresentFrameField.Height)));
        // ResizeBuffers_Stop (46) v0: pointer, ReturnValue, 2 bytes, 3 pointers, NewWidth, NewHeight.
        var resized = new byte[14 + 4 * 8];
        BitConverter.GetBytes(0xA000UL).CopyTo(resized, 0);
        BitConverter.GetBytes(unchecked((int)0x887A0001)).CopyTo(resized, 8);
        BitConverter.GetBytes(2560u).CopyTo(resized, 6 + 4 * 8);
        BitConverter.GetBytes(1440u).CopyTo(resized, 10 + 4 * 8);
        Decode(dxgi, 46, 0, resized, payload =>
        {
            Assert.True(payload.Int32((int)PresentFrameField.ReturnValue) < 0);
            Assert.Equal(2560u, payload.UInt32((int)PresentFrameField.NewWidth));
            Assert.Equal(1440u, payload.UInt32((int)PresentFrameField.NewHeight));
        });
        // Win32k TokenCompositionSurfaceObject (201) v1: 2 pointers, SwapChainIndex, PresentCount, Luid, BindId, FlipInterval, DestWidth, DestHeight.
        var token = new byte[40 + 2 * 8];
        BitConverter.GetBytes(1280u).CopyTo(token, 32 + 2 * 8);
        BitConverter.GetBytes(720u).CopyTo(token, 36 + 2 * 8);
        Decode(PresentFrameEtwSource.Win32kProvider, 201, 1, token, payload =>
        {
            Assert.Equal(1280u, payload.UInt32((int)PresentFrameField.DestWidth));
            Assert.Equal(720u, payload.UInt32((int)PresentFrameField.DestHeight));
        });
        // A shorter payload than the declared prefix is rejected instead of read past its end.
        Decode(dxgi, 10, 0, created[..^1], null);
    }

    private static void Decode(Guid provider, int id, int version, byte[] bytes, Action<EtwFixedPayload>? check)
    {
        var buffer = Marshal.AllocHGlobal(Math.Max(bytes.Length, 1));
        try
        {
            Marshal.Copy(bytes, 0, buffer, bytes.Length);
            var status = PresentFrameEtwLayouts.Catalog.Find(provider, id, version, new EtwRawPayload(buffer, bytes.Length, 8), out var payload);
            if (check is null)
            {
                Assert.Equal(EtwFixedLayoutStatus.ShortPayload, status);
                return;
            }

            Assert.Equal(EtwFixedLayoutStatus.Decoded, status);
            check(payload);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    [Fact]
    public async Task InjectedSectionSizeIsReadOnlyForResolutionTargetsAndNamedAsItsSource()
    {
        var injected = new RecordingInjectedSizes(new FrameOutputResolution(FrameOutputResolutionSource.InjectedSharedSection, 1707, 960, default));
        var clock = new PresentFrameEtwManualTimeProvider();
        await using var harness = PresentFrameEtwTestHarness.Create(timeProvider: clock, injectedSizes: injected);
        using var whole = harness.Source.AcquireSubscription();
        await harness.WaitRunningAsync();
        var ledger = Ledger(harness.Source);
        var at = clock.GetUtcNow().UtcTicks;
        ledger.ProcessStarted(41, 9);
        ledger.Present(41, FramePresentSource.Dxgi, 3, at + TimeSpan.FromMilliseconds(10).Ticks);
        ledger.Present(41, FramePresentSource.Dxgi, 3, at + TimeSpan.FromMilliseconds(20).Ticks);
        clock.Advance(TimeSpan.FromMilliseconds(30));

        // A whole-machine lease without the resolution tier never opens a section.
        Assert.Empty(Assert.Single(harness.Source.Read(TimeSpan.FromSeconds(1))!.Processes).Streams.Single().OutputResolutions);
        Assert.Empty(injected.Reads);

        using var targeted = harness.Source.AcquireSubscription(new FrameSubscriptionRequest([41], FrameDetail.OutputResolution));
        var observedProcess = Assert.Single(harness.Source.Read(TimeSpan.FromSeconds(1))!.Processes);
        var size = Assert.Single(observedProcess.OutputResolutions);
        Assert.Empty(observedProcess.Streams.Single().OutputResolutions);
        Assert.Equal(FrameOutputResolutionSource.InjectedSharedSection, size.Source);
        Assert.Equal((1707, 960), (size.Width, size.Height));
        Assert.Equal(new[] { (41, 9L) }, injected.Reads);
    }

    [Fact]
    public async Task PresentPidFilterFollowsLeaseTargetsUpToTheDeclaredCap()
    {
        await using var harness = PresentFrameEtwTestHarness.Create();
        var first = harness.Source.AcquireSubscription(new FrameSubscriptionRequest([100, 200], FrameDetail.None));
        await harness.WaitRunningAsync();
        var session = Assert.Single(harness.Sessions.Snapshot());
        void AssertFilter(int[]? pids)
        {
            foreach (var provider in new[] { PresentFrameEtwSource.DxgiProvider, PresentFrameEtwSource.Direct3D9Provider })
                Assert.Equal(pids, session.Operations.Last(item => item.Provider == provider && item.Kind == "enable").ProcessIds);
        }
        AssertFilter([100, 200]);
        // The cap is frame_pipeline.process_filter_capacity (8); nine targets in the union read the whole machine.
        Assert.Equal(8, MonitoringSettings.ProductDefaults.FramePipeline.ProcessFilterCapacity);
        var second = harness.Source.AcquireSubscription(new FrameSubscriptionRequest([300, 400, 500, 600, 700, 800], FrameDetail.None));
        AssertFilter([100, 200, 300, 400, 500, 600, 700, 800]);
        second.Update(new FrameSubscriptionRequest([300, 400, 500, 600, 700, 800, 900], FrameDetail.None));
        AssertFilter(null);
        second.Update(new FrameSubscriptionRequest([300], FrameDetail.None));
        AssertFilter([100, 200, 300]);
        // Any whole-machine lease removes the filter; releasing it restores the targets.
        var whole = harness.Source.AcquireSubscription();
        AssertFilter(null);
        whole.Dispose();
        AssertFilter([100, 200, 300]);
        second.Dispose();
        AssertFilter([100, 200]);
        // Machine-wide providers keep no PID filter.
        Assert.Null(session.Operations.Last(item => item.Provider == PresentFrameEtwSource.GraphicsKernelProvider && item.Kind == "enable").ProcessIds);
        await harness.ReleaseAsync(first);
    }

    [Fact]
    public void ProcessFilterRulesCoverEmptyTargetsAndADisabledCap()
    {
        FrameSubscriptionRequest Targets(params int[] pids) => new(pids, FrameDetail.None);
        Assert.Equal(new[] { 5 }, PresentFrameEtwSource.ProcessFilter(false, [], [5], 8));
        Assert.Null(PresentFrameEtwSource.ProcessFilter(false, [], [5], 0));
        Assert.Null(PresentFrameEtwSource.ProcessFilter(true, [Targets(1)], [], 0));
        // Frame leases whose targets have no running process need no runtime Present at all.
        Assert.Equal(Array.Empty<int>(), PresentFrameEtwSource.ProcessFilter(true, [Targets()], [], 8));
        Assert.Equal(new[] { 1, 5 }, PresentFrameEtwSource.ProcessFilter(true, [Targets(1)], [5], 8));
        Assert.Null(PresentFrameEtwSource.ProcessFilter(true, [Targets(1), FrameSubscriptionRequest.AllProcesses], [], 8));
    }

    [Fact]
    public async Task ResolutionTierEnablesSwapChainEventsAndAsksForTheirRundownOnce()
    {
        await using var harness = PresentFrameEtwTestHarness.Create();
        var plain = harness.Source.AcquireSubscription();
        await harness.WaitRunningAsync();
        var session = Assert.Single(harness.Sessions.Snapshot());
        var dxgi = session.Operations.Last(item => item.Provider == PresentFrameEtwSource.DxgiProvider && item.Kind == "enable");
        Assert.DoesNotContain(10, dxgi.EventIds!);
        Assert.Equal(0x2UL, dxgi.Keywords);

        var resolution = harness.Source.AcquireSubscription(new FrameSubscriptionRequest(null, FrameDetail.OutputResolution));
        dxgi = session.Operations.Last(item => item.Provider == PresentFrameEtwSource.DxgiProvider && item.Kind == "enable");
        Assert.Equal(0x3UL, dxgi.Keywords);
        Assert.Superset(new HashSet<int> { 10, 12, 46 }, dxgi.EventIds!.ToHashSet());
        Assert.Single(session.Operations, item => item.Kind == "capture-state" && item.Provider == PresentFrameEtwSource.DxgiProvider);

        resolution.Dispose();
        dxgi = session.Operations.Last(item => item.Provider == PresentFrameEtwSource.DxgiProvider && item.Kind == "enable");
        Assert.Equal(0x2UL, dxgi.Keywords);
        await harness.ReleaseAsync(plain);
    }

    [Fact]
    public async Task LeaseSnapshotsItsTargetsAndTargetUpdatesRecomputeThePidFilter()
    {
        await using var harness = PresentFrameEtwTestHarness.Create();
        var targets = new[] { 41 };
        var lease = harness.Source.AcquireSubscription(new(targets, FrameDetail.None));
        await harness.WaitRunningAsync();
        targets[0] = 99;
        var session = Assert.Single(harness.Sessions.Snapshot());
        var before = session.Operations.Last(item => item.Kind == "enable" && item.Provider == PresentFrameEtwSource.DxgiProvider);
        Assert.Equal(new[] { 41 }, before.ProcessIds);
        lease.Update(new([42], FrameDetail.None));
        var after = session.Operations.Last(item => item.Kind == "enable" && item.Provider == PresentFrameEtwSource.DxgiProvider);
        Assert.Equal(new[] { 42 }, after.ProcessIds);
        Assert.Throws<ArgumentOutOfRangeException>(() => lease.Update(new([0], FrameDetail.None)));
        await harness.ReleaseAsync(lease);
    }

    [Fact]
    public void FramePipelineFreedomPointDeclaresEveryFrameBehaviourValue()
    {
        var pipeline = MonitoringSettings.ProductDefaults.FramePipeline;
        Assert.Equal(30_000, pipeline.RetentionMilliseconds);
        Assert.Equal(1000, pipeline.PresentRateCapacityHz);
        Assert.Equal(256, pipeline.StreamCapacity);
        Assert.Equal(250, pipeline.FlushIntervalMilliseconds);
        Assert.Equal(29_000, pipeline.RecordingFrameWindowMilliseconds);
        Assert.Equal(250, pipeline.RecordingFrameReadLagMilliseconds);
        Assert.Equal(8, pipeline.ProcessFilterCapacity);
        // ceil(1000 Hz x 30 s): a whole retention window fits up to 1000 Presents per second.
        Assert.Equal(new PresentFrameLedgerLimits(TimeSpan.FromSeconds(30), 30_001, 256), pipeline.LedgerLimits);

        Assert.Throws<InvalidDataException>(() => (pipeline with { ProcessFilterCapacity = 9 }).Validate());
        Assert.Throws<InvalidDataException>(() => (pipeline with { RecordingFrameWindowMilliseconds = 29_900 }).Validate());
        Assert.Throws<InvalidDataException>(() => (pipeline with { PresentRateCapacityHz = 0 }).Validate());
    }

    [Fact]
    public void PresentRateAboveTheDeclaredCapacityNamesItsReason()
    {
        var ledger = new PresentFrameLedger(static _ => ProcessBirth, new PresentFrameLedgerLimits(TimeSpan.FromSeconds(30), 100, 256));
        ledger.ProcessStarted(ProcessId, ProcessBirth);
        for (var index = 0; index < 150; index++) ledger.Present(ProcessId, FramePresentSource.Dxgi, SwapChain, Ms(index));
        Assert.Equal(new[] { FrameTimingReasons.PresentRateCapacity }, ledger.IncompleteReasons(Ms(0)));
        Assert.Empty(ledger.IncompleteReasons(Ms(60)));

        var streams = new PresentFrameLedger(static _ => ProcessBirth, new PresentFrameLedgerLimits(TimeSpan.FromSeconds(30), 100, 1));
        streams.ProcessStarted(ProcessId, ProcessBirth);
        streams.Present(ProcessId, FramePresentSource.Dxgi, SwapChain, Ms(0));
        streams.Present(ProcessId, FramePresentSource.Dxgi, SwapChain + 1, Ms(1));
        Assert.Equal(new[] { FrameTimingReasons.StreamCapacity }, streams.IncompleteReasons(Ms(0)));
    }

    [Fact]
    public async Task FrameSubscriptionOpensSessionWithoutOsBuildConfiguration()
    {
        await using var harness = PresentFrameEtwTestHarness.Create();
        var lease = harness.Source.AcquireSubscription();
        await harness.WaitRunningAsync();
        Assert.Single(harness.Sessions.Snapshot());
        await harness.ReleaseAsync(lease);
    }
    [Fact]
    public void DurationStatisticsShareTheIntervalAlgorithm()
    {
        double[] values = [4, 1, 3, 2, 10];
        var durations = Assert.IsType<FrameDurationStatistics>(FrameDurationStatistics.Compute(values));
        var intervals = Assert.IsType<FrameIntervalStatistics>(FrameIntervalStatistics.Compute(values));
        Assert.Equal(intervals.P50FrameTimeMs, durations.P50Ms);
        Assert.Equal(intervals.P99FrameTimeMs, durations.P99Ms);
        Assert.Equal(intervals.OnePercentLowFps, 1000d / durations.OnePercentHighMs, 9);
        Assert.Equal(intervals.AverageFps, 1000d / durations.AverageMs, 9);
        // Durations may be zero (a Present that does not wait); intervals may not.
        Assert.Equal(0d, FrameDurationStatistics.Compute([0d, 0d])!.MaxMs);
        Assert.Throws<ArgumentOutOfRangeException>(() => FrameIntervalStatistics.Compute([0d]));
    }

    private static PresentFrameLedger Ledger(PresentFrameEtwSource source)
        => Assert.IsType<PresentFrameLedger>(typeof(PresentFrameEtwSource)
            .GetField("ledger", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(source));

    private sealed class RecordingInjectedSizes(FrameOutputResolution size) : IInjectedOverlaySizeReader
    {
        public List<(int, long)> Reads { get; } = [];
        public FrameOutputResolution? Read(int processId, long processStartKey, DateTimeOffset now)
        {
            Reads.Add((processId, processStartKey));
            return size with { ObservedAt = now };
        }
    }
}
