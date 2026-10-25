using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Information;
using ResourceManager.ReadWrite.Windows.Frames;

namespace Resource_Manager_APP.Tests;

public sealed class FrameDisplayCompletionTests
{
    private const int ProcessId = 73;
    private const int SecondProcessId = 74;
    private const int ThreadId = 9;
    private const long ProcessBirth = 134056632000000000;
    private const ulong SwapChain = 0xA0;
    private const long QpcFrequency = 10_000_000;
    private const long FirstQpc = 1_000_000;
    private static readonly long EpochTicks = DateTimeOffset.UnixEpoch.UtcDateTime.Ticks;
    private static readonly TimeSpan Window = TimeSpan.FromMilliseconds(100);

    [Fact]
    public void LegacyFlipUsesTheRecordedQpcCompletionAndKeepsZeroDropsDistinctFromNoSample()
    {
        var ledger = CreateLedger();
        ledger.ProcessStarted(ProcessId, ProcessBirth);
        RecordLegacyFlip(ledger, ProcessId, ThreadId, 0, FirstQpc, 1);
        RecordLegacyFlip(ledger, ProcessId, ThreadId, 20, FirstQpc + 200_000, 2);

        var stream = Assert.Single(ledger.ReadDisplayStreams(EpochTicks + TimeSpan.FromMilliseconds(100).Ticks, Window));

        Assert.Equal(ProcessId, stream.ProcessId);
        Assert.Equal(ProcessBirth, stream.ProcessStartKey);
        Assert.Equal(FramePresentSource.Dxgi, stream.Source);
        Assert.Equal(SwapChain, stream.SwapChain);
        Assert.True(stream.Complete);
        Assert.Equal<ulong?>(0UL, stream.DroppedCount);
        Assert.Equal(20d, Assert.IsType<FrameIntervalStatistics>(stream.Statistics).P50FrameTimeMs);
        Assert.Collection(stream.Frames,
            first => Assert.Equal(FrameTimingDisplayOutcome.Displayed, first.Outcome),
            second => Assert.Equal(FrameTimingDisplayOutcome.Displayed, second.Outcome));
        Assert.Equal(QpcFrequency, stream.Frames[0].QpcFrequency);
        Assert.Null(stream.Frames[0].AdapterLuid);
        Assert.Null(stream.Frames[0].MonitorDevicePath);
    }

    [Fact]
    public void ImmediateFlipUsesItsDocumentedMmioFlipScreenTimeWithoutInventingVsync()
    {
        var ledger = CreateLedger();
        ledger.ProcessStarted(ProcessId, ProcessBirth);
        ledger.Present(ProcessId, FramePresentSource.Dxgi, SwapChain,
            EpochTicks, ThreadId, FirstQpc, QpcFrequency);
        ledger.LegacyFlip(ProcessId, ThreadId, mmioFlip: true, FirstQpc + 1_000);
        ledger.QueuePacketStart(ProcessId, ThreadId, 11, 0xCC, packetType: 0,
            isPresentPacket: true, FirstQpc + 2_000);
        ledger.MmioFlip(11, EpochTicks + 3_000, FirstQpc + 3_000, flipImmediate: true);

        var stream = Assert.Single(ledger.ReadDisplayStreams(EpochTicks + TimeSpan.FromMilliseconds(10).Ticks,
            TimeSpan.FromMilliseconds(10)));
        var frame = Assert.Single(stream.Frames);

        Assert.True(stream.Complete);
        Assert.Equal<ulong?>(0UL, stream.DroppedCount);
        Assert.Equal<long?>(FirstQpc + 3_000, frame.DisplayedQpc);
        Assert.Equal<long?>(EpochTicks + 3_000, frame.OutcomeAt?.UtcTicks);
        Assert.Null(stream.Statistics);
    }

    [Fact]
    public void DisplayWindowIncludesBothClosedUtcBoundariesAndItsCrossWindowStart()
    {
        var ledger = CreateLedger();
        ledger.ProcessStarted(ProcessId, ProcessBirth);
        RecordLegacyFlipAtCompletion(ledger, startMilliseconds: 0, completionMilliseconds: 90, submitSequence: 21);
        RecordLegacyFlipAtCompletion(ledger, startMilliseconds: 50, completionMilliseconds: 100, submitSequence: 22);

        var stream = Assert.Single(ledger.ReadDisplayStreams(
            EpochTicks + TimeSpan.FromMilliseconds(100).Ticks, TimeSpan.FromMilliseconds(50)));

        Assert.True(stream.Complete);
        Assert.Equal<ulong?>(0UL, stream.DroppedCount);
        Assert.Equal(10d, Assert.IsType<FrameIntervalStatistics>(stream.Statistics).P50FrameTimeMs);
        Assert.Equal(2, stream.Frames.Count);
        Assert.Contains(stream.Frames, frame => frame.OutcomeAt?.UtcTicks == EpochTicks + TimeSpan.FromMilliseconds(90).Ticks);
        Assert.Contains(stream.Frames, frame => frame.OutcomeAt?.UtcTicks == EpochTicks + TimeSpan.FromMilliseconds(100).Ticks);
    }

    [Fact]
    public void UnknownOutcomeStopsTaintingWindowsAfterItsRetainedEvidenceAgesOut()
    {
        var ledger = CreateLedger();
        ledger.ProcessStarted(ProcessId, ProcessBirth);
        ledger.Present(ProcessId, FramePresentSource.Dxgi, SwapChain,
            EpochTicks, ThreadId, FirstQpc, QpcFrequency);
        ledger.UnsupportedDisplayPath(ProcessId, ThreadId);

        var firstRead = ledger.ReadDisplayStreams(
            EpochTicks + TimeSpan.FromMilliseconds(10).Ticks, TimeSpan.FromMilliseconds(10));
        Assert.False(Assert.Single(firstRead).Complete);

        RecordLegacyFlipAtCompletion(ledger, startMilliseconds: 30_020,
            completionMilliseconds: 30_025, submitSequence: 31);
        RecordLegacyFlipAtCompletion(ledger, startMilliseconds: 30_070,
            completionMilliseconds: 30_075, submitSequence: 32);

        var recovered = ledger.ReadDisplayStreams(
            EpochTicks + TimeSpan.FromMilliseconds(30_100).Ticks, Window);

        var stream = Assert.Single(recovered);
        Assert.True(stream.Complete);
        Assert.Equal<ulong?>(0UL, stream.DroppedCount);
        Assert.Equal(50d, Assert.IsType<FrameIntervalStatistics>(stream.Statistics).P50FrameTimeMs);
    }

    [Fact]
    public void Win32kDiscardedIsCountedOnlyBeforeInFrameAndRetiredIsNotACompletion()
    {
        var discardedLedger = CreateLedger();
        discardedLedger.ProcessStarted(ProcessId, ProcessBirth);
        discardedLedger.Present(ProcessId, FramePresentSource.Dxgi, SwapChain,
            EpochTicks, ThreadId, FirstQpc, QpcFrequency);
        discardedLedger.TrackWin32kToken(ProcessId, ThreadId, 0x1122, 3, 4);
        discardedLedger.PresentStopped(ProcessId, ThreadId, EpochTicks + 1_000, FirstQpc + 1_000, 0);
        discardedLedger.Win32kTokenStateChanged(0x1122, 3, 4, 6, EpochTicks + 2_000, FirstQpc + 2_000);

        var discarded = Assert.Single(discardedLedger.ReadDisplayStreams(EpochTicks + TimeSpan.FromMilliseconds(10).Ticks,
            TimeSpan.FromMilliseconds(10)));
        Assert.True(discarded.Complete);
        Assert.Equal<ulong?>(1UL, discarded.DroppedCount);
        Assert.Null(discarded.Statistics);
        Assert.Equal(FrameTimingDisplayOutcome.Discarded, Assert.Single(discarded.Frames).Outcome);

        var inFrameLedger = CreateLedger();
        inFrameLedger.ProcessStarted(ProcessId, ProcessBirth);
        inFrameLedger.Present(ProcessId, FramePresentSource.Dxgi, SwapChain,
            EpochTicks, ThreadId, FirstQpc, QpcFrequency);
        inFrameLedger.TrackWin32kToken(ProcessId, ThreadId, 0x1122, 3, 4);
        inFrameLedger.Win32kTokenStateChanged(0x1122, 3, 4, 3, EpochTicks + 1_000, FirstQpc + 1_000);
        inFrameLedger.Win32kTokenStateChanged(0x1122, 3, 4, 6, EpochTicks + 2_000, FirstQpc + 2_000);

        var inFrame = Assert.Single(inFrameLedger.ReadDisplayStreams(EpochTicks + TimeSpan.FromMilliseconds(10).Ticks,
            TimeSpan.FromMilliseconds(10)));
        Assert.False(inFrame.Complete);
        Assert.Null(inFrame.DroppedCount);
        Assert.Null(inFrame.Statistics);
        Assert.Equal(FrameTimingDisplayOutcome.Unknown, Assert.Single(inFrame.Frames).Outcome);

        var retiredLedger = CreateLedger();
        retiredLedger.ProcessStarted(ProcessId, ProcessBirth);
        retiredLedger.Present(ProcessId, FramePresentSource.Dxgi, SwapChain,
            EpochTicks, ThreadId, FirstQpc, QpcFrequency);
        retiredLedger.TrackWin32kToken(ProcessId, ThreadId, 0x1122, 3, 4);
        retiredLedger.Win32kTokenStateChanged(0x1122, 3, 4, 5, EpochTicks + 2_000, FirstQpc + 2_000);

        var retired = Assert.Single(retiredLedger.ReadDisplayStreams(EpochTicks + TimeSpan.FromMilliseconds(10).Ticks,
            TimeSpan.FromMilliseconds(10)));
        Assert.False(retired.Complete);
        Assert.Null(retired.DroppedCount);
        Assert.Equal(FrameTimingDisplayOutcome.Retired, Assert.Single(retired.Frames).Outcome);

        var unmatchedLedger = CreateLedger();
        unmatchedLedger.ProcessStarted(ProcessId, ProcessBirth);
        unmatchedLedger.Present(ProcessId, FramePresentSource.Dxgi, SwapChain,
            EpochTicks, ThreadId, FirstQpc, QpcFrequency);
        unmatchedLedger.TrackWin32kToken(ProcessId, ThreadId + 1, 0x1122, 3, 4);
        unmatchedLedger.Win32kTokenStateChanged(0x1122, 3, 4, 6,
            EpochTicks + 2_000, FirstQpc + 2_000);

        var unmatched = Assert.Single(unmatchedLedger.ReadDisplayStreams(
            EpochTicks + TimeSpan.FromMilliseconds(10).Ticks, TimeSpan.FromMilliseconds(10)));
        Assert.False(unmatched.Complete);
        Assert.Null(unmatched.DroppedCount);
        Assert.Equal(FrameTimingDisplayOutcome.Unknown, Assert.Single(unmatched.Frames).Outcome);
    }

    [Fact]
    public void FailedPresentIsNotReportedAsDisplayedOrAsAZeroDrop()
    {
        var ledger = CreateLedger();
        ledger.ProcessStarted(ProcessId, ProcessBirth);
        ledger.Present(ProcessId, FramePresentSource.Dxgi, SwapChain,
            EpochTicks, ThreadId, FirstQpc, QpcFrequency);
        ledger.PresentStopped(ProcessId, ThreadId, EpochTicks + 1_000,
            FirstQpc + 1_000, unchecked((int)0x80004005));

        var stream = Assert.Single(ledger.ReadDisplayStreams(
            EpochTicks + TimeSpan.FromMilliseconds(10).Ticks, TimeSpan.FromMilliseconds(10)));

        Assert.False(stream.Complete);
        Assert.Null(stream.DroppedCount);
        Assert.Null(stream.Statistics);
        Assert.Equal(FrameTimingDisplayOutcome.Failed, Assert.Single(stream.Frames).Outcome);
    }

    [Fact]
    public async Task OwnerProjectsOnlyTheExactCompletedDisplayStreamAndPreservesUnavailableNulls()
    {
        var query = new PresentFrameFieldQuery(ProcessId, ProcessBirth, FramePresentSource.Dxgi,
            SwapChain, Window, FrameTimePercentile.P95);
        var displayStatistics = FrameIntervalStatistics.Compute([10d, 20d, 30d])!;
        var target = new FrameTimingDisplayStream(ProcessId, ProcessBirth, FramePresentSource.Dxgi,
            SwapChain, true, displayStatistics, 0UL, []);
        FrameTimingDisplayStream[] decoys =
        [
            target,
            new(ProcessId + 1, ProcessBirth, FramePresentSource.Dxgi, SwapChain, true, displayStatistics, 17UL, []),
            new(ProcessId, ProcessBirth + 1, FramePresentSource.Dxgi, SwapChain, true, displayStatistics, 18UL, []),
            new(ProcessId, ProcessBirth, FramePresentSource.Direct3D9, SwapChain, true, displayStatistics, 19UL, []),
            new(ProcessId, ProcessBirth, FramePresentSource.Dxgi, SwapChain + 1, true, displayStatistics, 20UL, [])
        ];
        var observedAt = DateTimeOffset.UnixEpoch.AddSeconds(1);
        var source = new RecordedSource(new FrameTimingSnapshot(observedAt, Window, true, [])
        {
            DisplayStreams = decoys
        });
        using var owner = new WindowsPresentFrameFieldOwner(source, query);
        var backend = owner.CreateBackend();

        await owner.CaptureInformationAsync();

        Assert.Equal<double?>(displayStatistics.DurationMs / displayStatistics.IntervalCount,
            backend.ReadCached(query.Field(FrameFieldRegistry.DisplayIntervalMs)).Value?.Number);
        Assert.Equal<double?>(displayStatistics.AverageFps,
            backend.ReadCached(query.Field(FrameFieldRegistry.DisplayedFps)).Value?.Number);
        var droppedCount = backend.ReadCached(query.Field(FrameFieldRegistry.DroppedCount));
        Assert.Equal(FieldObservationStatus.Current, droppedCount.Status);
        Assert.Equal(MetricInteger.Parse("0"), droppedCount.Value?.Integer);
        Assert.Equal<double?>(displayStatistics.P95FrameTimeMs,
            backend.ReadCached(query.Field(FrameFieldRegistry.DisplayIntervalP95Ms)).Value?.Number);
        Assert.Equal<double?>(displayStatistics.P99FrameTimeMs,
            backend.ReadCached(query.Field(FrameFieldRegistry.DisplayIntervalP99Ms)).Value?.Number);
        Assert.Equal(1, source.Acquisitions);

        var absentSource = new RecordedSource(new FrameTimingSnapshot(observedAt, Window, true, []));
        using var absentOwner = new WindowsPresentFrameFieldOwner(absentSource, query);
        await absentOwner.CaptureInformationAsync();
        var noSample = absentOwner.CreateBackend().ReadCached(query.Field(FrameFieldRegistry.DroppedCount));
        Assert.Equal(FieldObservationStatus.Unavailable, noSample.Status);
        Assert.Null(noSample.Value);

        var lostEventsSource = new RecordedSource(new FrameTimingSnapshot(observedAt, Window, false, [])
        {
            DisplayStreams = [target]
        });
        using var lostEventsOwner = new WindowsPresentFrameFieldOwner(lostEventsSource, query);
        await lostEventsOwner.CaptureInformationAsync();
        var afterLoss = lostEventsOwner.CreateBackend().ReadCached(query.Field(FrameFieldRegistry.DroppedCount));
        Assert.Equal(FieldObservationStatus.Unavailable, afterLoss.Status);
        Assert.Null(afterLoss.Value);
    }

    [Fact]
    public void AmbiguousSequenceAndUnsupportedMpoNeverPublishDisplayOrDropZero()
    {
        var ledger = CreateLedger();
        ledger.ProcessStarted(ProcessId, ProcessBirth);
        ledger.ProcessStarted(SecondProcessId, ProcessBirth + 1);
        ledger.Present(ProcessId, FramePresentSource.Dxgi, SwapChain,
            EpochTicks, ThreadId, FirstQpc, QpcFrequency);
        ledger.LegacyFlip(ProcessId, ThreadId, mmioFlip: true, FirstQpc + 1_000);
        ledger.QueuePacketStart(ProcessId, ThreadId, 91, 0xCC, packetType: 0,
            isPresentPacket: true, FirstQpc + 2_000);
        ledger.Present(SecondProcessId, FramePresentSource.Dxgi, SwapChain + 1,
            EpochTicks + 1_000, ThreadId + 1, FirstQpc + 10_000, QpcFrequency);
        ledger.LegacyFlip(SecondProcessId, ThreadId + 1, mmioFlip: true, FirstQpc + 11_000);
        ledger.QueuePacketStart(SecondProcessId, ThreadId + 1, 91, 0xDD, packetType: 0,
            isPresentPacket: true, FirstQpc + 12_000);
        ledger.MmioFlip(91, EpochTicks + 13_000, FirstQpc + 13_000, flipImmediate: false);

        var ambiguous = ledger.ReadDisplayStreams(EpochTicks + TimeSpan.FromMilliseconds(10).Ticks,
            TimeSpan.FromMilliseconds(10));
        Assert.Equal(2, ambiguous.Count);
        Assert.All(ambiguous, stream =>
        {
            Assert.False(stream.Complete);
            Assert.Null(stream.DroppedCount);
            Assert.Null(stream.Statistics);
        });

        var mpoLedger = CreateLedger();
        mpoLedger.ProcessStarted(ProcessId, ProcessBirth);
        mpoLedger.Present(ProcessId, FramePresentSource.Dxgi, SwapChain,
            EpochTicks, ThreadId, FirstQpc, QpcFrequency);
        mpoLedger.UnsupportedDisplayPath(ProcessId, ThreadId, vidPnSourceId: 2);
        var mpo = Assert.Single(mpoLedger.ReadDisplayStreams(EpochTicks + TimeSpan.FromMilliseconds(10).Ticks,
            TimeSpan.FromMilliseconds(10)));
        Assert.False(mpo.Complete);
        Assert.Null(mpo.DroppedCount);
        Assert.Null(mpo.Statistics);
        Assert.Equal<uint?>(2, Assert.Single(mpo.Frames).VidPnSourceId);
    }

    private static PresentFrameLedger CreateLedger() => new(static _ => ProcessBirth);

    private static void RecordLegacyFlip(PresentFrameLedger ledger, int processId, int threadId,
        int startMilliseconds, long startQpc, uint submitSequence)
    {
        ledger.Present(processId, FramePresentSource.Dxgi, SwapChain,
            EpochTicks + TimeSpan.FromMilliseconds(startMilliseconds).Ticks,
            threadId, startQpc, QpcFrequency);
        ledger.LegacyFlip(processId, threadId, mmioFlip: true, startQpc + 1_000);
        ledger.QueuePacketStart(processId, threadId, submitSequence, 0xCC,
            packetType: 0, isPresentPacket: true, startQpc + 2_000);
        ledger.MmioFlip(submitSequence,
            EpochTicks + TimeSpan.FromMilliseconds(startMilliseconds + 3).Ticks,
            startQpc + 3_000, flipImmediate: false);
        ledger.VSyncDpc(submitSequence,
            EpochTicks + TimeSpan.FromMilliseconds(startMilliseconds + 5).Ticks,
            startQpc + 5_000);
    }

    private static void RecordLegacyFlipAtCompletion(PresentFrameLedger ledger,
        int startMilliseconds, int completionMilliseconds, uint submitSequence)
    {
        var startQpc = FirstQpc + startMilliseconds * 10_000L;
        ledger.Present(ProcessId, FramePresentSource.Dxgi, SwapChain,
            EpochTicks + TimeSpan.FromMilliseconds(startMilliseconds).Ticks,
            ThreadId, startQpc, QpcFrequency);
        ledger.LegacyFlip(ProcessId, ThreadId, mmioFlip: true, timestampQpc: startQpc + 1_000);
        ledger.QueuePacketStart(ProcessId, ThreadId, submitSequence, 0xCC,
            packetType: 0, isPresentPacket: true, timestampQpc: startQpc + 2_000);
        ledger.MmioFlip(submitSequence,
            EpochTicks + TimeSpan.FromMilliseconds(startMilliseconds + 3).Ticks,
            startQpc + 3_000, flipImmediate: false);
        var completionQpc = FirstQpc + completionMilliseconds * 10_000L;
        ledger.VSyncDpc(submitSequence,
            EpochTicks + TimeSpan.FromMilliseconds(completionMilliseconds).Ticks,
            completionQpc);
    }

    private sealed class RecordedSource(FrameTimingSnapshot snapshot) : IFrameTimingObservationSource
    {
        public int Acquisitions { get; private set; }

        public IDisposable AcquireSubscription()
        {
            Acquisitions++;
            return new Lease();
        }

        public FrameTimingSnapshot? Read(TimeSpan window)
        {
            Assert.Equal(snapshot.Window, window);
            return snapshot;
        }

        public FrameIntervalBatch? ReadIntervals(DateTimeOffset after, DateTimeOffset through)
            => new(through, true, []);

        private sealed class Lease : IDisposable
        {
            public void Dispose() { }
        }
    }
}
