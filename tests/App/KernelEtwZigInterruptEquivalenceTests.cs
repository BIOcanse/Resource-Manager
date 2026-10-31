using Resource_Manager_APP.Tests.Oracles.KernelEtw;
using System.Runtime.InteropServices;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using ResourceManager.App.Domain.SystemHealth;
using ResourceManager.App.Infrastructure.SystemHealth.Interrupts;

namespace Resource_Manager_APP.Tests;

public sealed class KernelEtwZigInterruptEquivalenceTests
{
    private const ulong KernelBase = 0xFFFF_F800_0000_0000UL;
    private readonly Xunit.Abstractions.ITestOutputHelper output;
    public KernelEtwZigInterruptEquivalenceTests(Xunit.Abstractions.ITestOutputHelper output) => this.output = output;

    [EnvironmentVariableFact("RESOURCE_MANAGER_KERNEL_ETL_REPLAY_PATH")]
    public unsafe void NativeConsumerInterruptsMatchManagedParserForTheOwnedFiniteEtl()
    {
        var path = Environment.GetEnvironmentVariable("RESOURCE_MANAGER_KERNEL_ETL_REPLAY_PATH");
        Assert.True(File.Exists(path), $"Owned kernel ETL does not exist: {path}");
        var raw = ReadRawClock(path!);
        output.WriteLine($"Owned ETL raw: records={raw.Records}; headerRecord={raw.HeaderRecord}; headerQpc={raw.HeaderQpc}; "
            + $"firstImageRecord={raw.FirstImageRecord}; firstInterruptRecord={raw.FirstInterruptRecord}; firstSwitchRecord={raw.FirstSwitchRecord}; "
            + $"imagesBeforeHeader={raw.ImagesBeforeHeader}; interruptsBeforeHeader={raw.InterruptsBeforeHeader}; switchesBeforeHeader={raw.SwitchesBeforeHeader}; "
            + $"lostMarkers={raw.LostMarkers}; ISR50={raw.Isr50}; ISR67={raw.Isr67}.");
        using var source = new ETWTraceEventSource(path);
        var frequencyProperty = typeof(TraceEventSource).GetProperty("QPCFreq",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        var frequency = Assert.IsType<long>(frequencyProperty?.GetValue(source));
        Assert.True(frequency > 0);
        Assert.Equal(raw.Frequency, frequency);
        using var replay = new Replay(recorded: true, nativeInputs: false,
            processorCount: checked((uint)source.NumberOfProcessors));
        var parser = new KernelTraceEventParser(source);
        // The managed dispatch order cannot define the clock: read the same
        // raw EventTrace/Header as NativeCore. Observe typed dispatch separately.
        var headerQpc = raw.HeaderQpc;
        long? dispatchedHeaderQpc = null;
        long through = 1000;
        long records = 0;
        long switches = 0;
        long firstImageDispatch = 0;
        long headerDispatch = 0;
        var images = 0;
        var validInterrupts = new long[4];
        long isrCallbacks = 0;
        long durationDifferences = 0;
        var lost = checked((ulong)source.EventsLost);
        replay.Loss(1000, lost);

        long Tick(long qpc)
        {
            return checked(1000 + (long)(((Int128)qpc - headerQpc) * 1000 / frequency));
        }
        void Interrupt(uint kind, double duration, ulong routine, TraceEvent data)
        {
            if (kind == 1) ++isrCallbacks;
            var initial = Assert.IsType<long>(data.GetType().GetProperty("InitialTimeQPC",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.GetValue(data));
            double Relative(long qpc) => (Math.Max(qpc, headerQpc) - headerQpc) * 1000.0 / frequency;
            var rawDuration = Relative(data.TimeStampQPC) - Relative(initial);
            if (rawDuration != duration && ++durationDifferences <= 4)
                output.WriteLine($"Duration difference: kind={kind}; qpc={data.TimeStampQPC}; initial={initial}; "
                    + $"TraceEvent={duration:R}; rawHeaderRelative={rawDuration:R}.");
            replay.Event(kind, duration, routine, Tick(data.TimeStampQPC));
            if (double.IsFinite(duration) && duration > 0) ++validInterrupts[kind - 1];
        }
#pragma warning disable CS0618 // Both consumers share the recorded EventTrace/Header QPC anchor.
        parser.EventTraceHeader += data =>
        {
            dispatchedHeaderQpc ??= data.TimeStampQPC;
            if (headerDispatch == 0) headerDispatch = records + 1;
            output.WriteLine($"Managed EventTrace/Header: provider={data.ProviderGuid}; task={data.TaskGuid}; opcode={data.Opcode}; qpc={data.TimeStampQPC}.");
        };
        source.AllEvents += data =>
        {
            through = Math.Max(through, Tick(data.TimeStampQPC));
            var currentLost = checked((ulong)source.EventsLost);
            if (currentLost != lost)
            {
                replay.Loss(through, currentLost);
                lost = currentLost;
            }
            ++records;
        };
        void Image(ulong address, int size, string? name, long qpc)
        {
            if (images == 0) firstImageDispatch = records + 1;
            replay.Image(address, size, name, Tick(qpc));
            ++images;
        }
        parser.ImageDCStart += data => Image(data.ImageBase, data.ImageSize, data.FileName, data.TimeStampQPC);
        parser.ImageLoad += data => Image(data.ImageBase, data.ImageSize, data.FileName, data.TimeStampQPC);
        parser.ImageUnload += data => { replay.Unload(data.ImageBase, Tick(data.TimeStampQPC)); ++images; };
        parser.ThreadCSwitch += _ => ++switches;
        parser.PerfInfoISR += data => Interrupt(1, data.ElapsedTimeMSec, data.Routine, data);
        parser.PerfInfoDPC += data => Interrupt(2, data.ElapsedTimeMSec, data.Routine, data);
        parser.PerfInfoThreadedDPC += data => Interrupt(3, data.ElapsedTimeMSec, data.Routine, data);
        parser.PerfInfoTimerDPC += data => Interrupt(4, data.ElapsedTimeMSec, data.Routine, data);
#pragma warning restore CS0618
        source.Process(); // The owned capture is finite; compare the complete file, without a prefix limit.
        output.WriteLine($"Owned ETL oracle: records={records}; images={images}; ISR={validInterrupts[0]}; "
            + $"DPC={validInterrupts[1]}; threadedDPC={validInterrupts[2]}; timerDPC={validInterrupts[3]}; "
            + $"switches={switches}; elapsedMs={through - 1000}; lost={lost}; firstImageDispatch={firstImageDispatch}; "
            + $"headerDispatch={headerDispatch}; dispatchedHeaderQpc={dispatchedHeaderQpc?.ToString() ?? "absent"}; "
            + $"anchor={headerQpc} (raw EventTrace/Header); durationDifferences={durationDifferences}; "
            + $"sourceStartQpc={typeof(TraceEventSource).GetField("sessionStartTimeQPC", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.GetValue(source)}.");
        if (dispatchedHeaderQpc is { } dispatched) Assert.Equal(headerQpc, dispatched);
        Assert.Equal(0, durationDifferences);
        Assert.Equal((ulong)isrCallbacks, raw.Isr50 + raw.Isr67);
        Assert.True(validInterrupts[0] > 0, "The owned capture lacks valid ISR events; direct ISR parity is not evidenced.");
        Assert.True(validInterrupts.Skip(1).Sum() > 0, "The owned capture lacks valid DPC events; direct DPC parity is not evidenced.");
        Assert.True(through >= 11000, "The owned capture does not contain ten seconds of recorded coverage.");

        var configuration = new ResidencyConfiguration { Size = (uint)Marshal.SizeOf<ResidencyConfiguration>(), Version = 0x10000,
            Frequency = frequency, WindowMilliseconds = 5000, MaximumPairs = 65536,
            MaximumProcesses = 32768, MaximumThreads = 131072,
            MaximumProcessors = 4096, MaximumNameBytes = 8 * 1024 * 1024, ByteCap = 256 * 1024 * 1024 };
        nint residency;
        // The owner copies the expected processors at create; the pin only spans the call.
        fixed (int* processors = Enumerable.Range(0, source.NumberOfProcessors).ToArray())
        {
            configuration.ProcessorCount = checked((uint)source.NumberOfProcessors);
            configuration.Processors = (nint)processors;
            Assert.Equal(0, CreateResidency(ref configuration, out residency));
        }
        nint trace = 0;
        try
        {
            fixed (char* text = path)
                Assert.Equal(0, OpenTrace(residency, text, (uint)path!.Length, 0, out trace));
            Assert.Equal(0, BindInterrupts(trace, replay.Handle, headerQpc, 1000, 1));
            Assert.Equal(0, QueryPathMapping(trace, out var mapping, (uint)Marshal.SizeOf<PathMappingStatus>()));
            Assert.Equal(0u, mapping.Entries); // No path conversion has requested lazy local defaults yet.
            Assert.Equal(0u, mapping.Flags);
            var result = ProcessTrace(trace, 0);
            Assert.Equal(0, QueryPathMapping(trace, out mapping, (uint)Marshal.SizeOf<PathMappingStatus>()));
            output.WriteLine($"Owned ETL path mapping: entries={mapping.Entries}; flags={mapping.Flags}; failedDriveMask={mapping.FailedDriveMask:x}.");
            Assert.Equal(1u, mapping.Flags & 1); // This file reaches image conversion before recorded namespace additions.
            Assert.InRange(mapping.Entries, 6u, 32u);
            Assert.Equal(0, QueryTraceStatus(trace, out var status, (uint)Marshal.SizeOf<NativeTraceStatus>()));
            output.WriteLine($"Owned ETL native: result={result}; windows={status.WindowsStatus}; owner={status.OwnerStatus}; "
                + $"records={status.Records}; decoded={status.Decoded}; rejected={status.Rejected}; "
                + $"unhandled={status.Unhandled}; switches={status.Switches}; lost={status.EventsLost}; buffersLost={status.BuffersLost}.");
            Assert.True(result == 0, $"Native consumer failed: result={result}, Windows={status.WindowsStatus}, owner={status.OwnerStatus}.");
            Assert.Equal(0UL, status.Rejected);
            Assert.Equal(raw.Records, status.Records);
            Assert.Equal((ulong)switches, status.Switches);
            Assert.Equal(lost, (ulong)status.EventsLost);
            Assert.Equal(0u, status.BuffersLost);
            Assert.Equal(0, QueryTraceClock(trace, out var clock, (uint)Marshal.SizeOf<NativeTraceClock>()));
            Assert.Equal(frequency, clock.Frequency);
            Assert.True((clock.Flags & 2) != 0);
            Assert.Equal(headerQpc, clock.HeaderQpc);
            Assert.Equal(0, CloseTrace(trace));
            trace = 0;
            Assert.True(replay.Compare(through, output.WriteLine), "The direct native interrupt view must contain a complete ten-second bucket.");
        }
        finally
        {
            if (trace != 0) Assert.Equal(0, CloseTrace(trace));
            Assert.Equal(0, DestroyResidency(residency));
        }
    }

    [EnvironmentVariableFact("RESOURCE_MANAGER_ETL_REPLAY_PATH")]
    public void RecordedKernelEtlInterruptsMatchCSharpFromTheSameParserStream()
    {
        var path = Environment.GetEnvironmentVariable("RESOURCE_MANAGER_ETL_REPLAY_PATH");
        Assert.True(File.Exists(path), $"Kernel ETL does not exist: {path}");
        using var source = new ETWTraceEventSource(path);
        var frequencyProperty = typeof(TraceEventSource).GetProperty("QPCFreq",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        var frequency = Assert.IsType<long>(frequencyProperty?.GetValue(source));
        Assert.True(frequency > 0);
        using var replay = new Replay(recorded: true);
        var parser = new KernelTraceEventParser(source);
        const int maximumEvents = 10_000_000;
        const long maximumElapsedMilliseconds = 20_000;
        long? firstQpc = null;
        long lastTick = 1000;
        var allEvents = 0;
        var imageEvents = 0;
        var interruptEvents = new int[4];
        var validInterrupts = 0;
        var stopReason = "end-of-file";
        var snapshots = 0;
        var completeSnapshots = 0;
        var lastAvailable = false;
        long lastLossTick = 1000;
        var lost = checked((ulong)source.EventsLost);
        replay.Loss(1000, lost);

        long Tick(long qpc)
        {
            firstQpc ??= qpc;
            // Integer QPC conversion is deterministic and independent of replay
            // speed or the machine's wall clock. Both owners receive this tick.
            return checked(1000 + (long)(((Int128)qpc - firstQpc.Value) * 1000 / frequency));
        }
        void Compare()
        {
            lastAvailable = replay.Compare(lastTick);
            if (lastAvailable) ++completeSnapshots;
            ++snapshots;
        }
        void Image(ulong address, int size, string? name, long qpc, bool unload)
        {
            var tick = Tick(qpc);
            if (unload) replay.Unload(address, tick);
            else replay.Image(address, size, name, tick);
            ++imageEvents;
        }
        void Interrupt(uint kind, double duration, ulong routine, long qpc)
        {
            replay.Event(kind, duration, routine, Tick(qpc));
            ++interruptEvents[kind - 1];
            if (double.IsFinite(duration) && duration > 0) ++validInterrupts;
        }
#pragma warning disable CS0618 // The raw QPC clock is the normalized replay input.
        parser.ImageDCStart += data => Image(data.ImageBase, data.ImageSize, data.FileName, data.TimeStampQPC, false);
        parser.ImageLoad += data => Image(data.ImageBase, data.ImageSize, data.FileName, data.TimeStampQPC, false);
        parser.ImageUnload += data => Image(data.ImageBase, data.ImageSize, data.FileName, data.TimeStampQPC, true);
        parser.PerfInfoISR += data => Interrupt(1, data.ElapsedTimeMSec, data.Routine, data.TimeStampQPC);
        parser.PerfInfoDPC += data => Interrupt(2, data.ElapsedTimeMSec, data.Routine, data.TimeStampQPC);
        parser.PerfInfoThreadedDPC += data => Interrupt(3, data.ElapsedTimeMSec, data.Routine, data.TimeStampQPC);
        parser.PerfInfoTimerDPC += data => Interrupt(4, data.ElapsedTimeMSec, data.Routine, data.TimeStampQPC);
        source.AllEvents += data =>
        {
            lastTick = Math.Max(lastTick, Tick(data.TimeStampQPC));
            var currentLost = checked((ulong)source.EventsLost);
            if (currentLost != lost)
            {
                replay.Loss(lastTick, currentLost);
                lost = currentLost;
                lastLossTick = lastTick;
            }
            if (++allEvents % 10000 == 0) Compare();
            if (allEvents >= maximumEvents || lastTick - 1000 >= maximumElapsedMilliseconds)
            {
                stopReason = allEvents >= maximumEvents ? "all-record-limit" : "recorded-time-limit";
                source.StopProcessing();
            }
        };
#pragma warning restore CS0618
        source.Process();
        Compare();
        output.WriteLine($"ETL interrupt parity: events={allEvents}; images={imageEvents}; "
            + $"ISR={interruptEvents[0]}; DPC={interruptEvents[1]}; threadedDPC={interruptEvents[2]}; timerDPC={interruptEvents[3]}; "
            + $"validInterrupts={validInterrupts}; relevantRecords={imageEvents + interruptEvents.Sum()}; "
            + $"elapsedMs={lastTick - 1000}; snapshots={snapshots}; completeSnapshots={completeSnapshots}; lost={lost}; stop={stopReason}.");
        Assert.True(validInterrupts > 0,
            $"The recorded ETL scan has no valid ISR/DPC events ({allEvents} total records, "
            + $"{imageEvents + interruptEvents.Sum()} relevant records, {lastTick - 1000} ms, stop={stopReason}); interrupt parity is not evidenced.");
        if (interruptEvents[0] == 0) output.WriteLine("The selected ETL prefix contains no ISR events; ISR replay coverage is absent.");
        if (lastTick - lastLossTick >= 10000)
            Assert.True(lastAvailable, "Ten seconds of ETL coverage since the last loss must publish a complete interrupt bucket.");
        else if (completeSnapshots == 0)
            output.WriteLine("The selected ETL prefix does not provide a complete loss-free ten-second interrupt bucket.");
    }

    [Theory]
    [InlineData("overlap-unload-reuse")]
    [InlineData("partial-slices")]
    [InlineData("loss-and-stale-events")]
    [InlineData("unicode-spelling")]
    [InlineData("top-sixteen")]
    [InlineData("overflow-range")]
    [InlineData("invalid-duration")]
    [InlineData("user-mode-images")]
    public void NormalizedInterruptReplayMatchesCSharpOracle(string scenario)
    {
        using var replay = new Replay();
        replay.Compare(1000);
        switch (scenario)
        {
            case "user-mode-images":
                replay.Image(0x7FF6_0000_0000UL, 0x100, "user.dll", 1000);
                replay.Event(1, 1, 0x7FF6_0000_0010UL, 1001);
                replay.Unload(0x7FF6_0000_0000UL, 1002);
                replay.Image(KernelBase + 100, 10, "kernel.sys", 1002);
                replay.Event(2, 2, KernelBase + 100, 1003);
                replay.Compare(11000);
                break;
            case "overlap-unload-reuse":
                replay.Image(KernelBase + 100, 1000, "C:\\wide.sys", 1000);
                replay.Image(KernelBase + 200, 10, "C:\\nested.sys", 1000);
                replay.Image(KernelBase + 300, 10, "C:\\later.sys", 1000);
                replay.Event(1, 2, KernelBase + 205, 1000);
                replay.Event(2, 3, KernelBase + 210, 1001);
                replay.Event(3, 1, KernelBase + 305, 1002);
                replay.Unload(KernelBase + 200, 1003);
                replay.Event(4, .5, KernelBase + 205, 1003);
                replay.Image(KernelBase + 100, 5, "C:\\replacement.sys", 2000);
                replay.Event(1, 4, KernelBase + 100, 2000);
                replay.Event(2, 4, KernelBase + 205, 2000);
                replay.Compare(11000);
                replay.Compare(62000);
                break;
            case "partial-slices":
                replay.Image(KernelBase + 100, 10, "slice.sys", 1000);
                replay.Event(1, 8, KernelBase + 100, 1001);
                replay.Event(2, 4, KernelBase + 100, 2001);
                replay.Event(3, 2, KernelBase + 100, 61000);
                replay.Compare(10999);
                replay.Compare(11000);
                replay.Compare(61500);
                replay.Compare(121500);
                break;
            case "loss-and-stale-events":
                replay.Image(KernelBase + 100, 10, "old.sys", 1000);
                replay.Event(1, 3, KernelBase + 100, 1001);
                replay.Compare(11000);
                replay.Loss(20000, 3);
                replay.Image(KernelBase + 100, 10, "stale.sys", 19999);
                replay.Event(1, 100, KernelBase + 100, 19999);
                replay.Event(2, 1, KernelBase + 100, 20000);
                replay.Compare(29999);
                replay.Compare(30000);
                replay.Loss(40000, 3); // unchanged loss count must not reset
                replay.Compare(40000);
                break;
            case "unicode-spelling":
                replay.Image(KernelBase + 100, 10, "  C:\\Ä.sys  ", 1000);
                replay.Event(1, 1, KernelBase + 100, 1000);
                replay.Image(KernelBase + 100, 10, "c:\\ä.SYS", 1001);
                replay.Event(2, 2, KernelBase + 100, 1001);
                replay.Unload(KernelBase + 100, 1002);
                replay.Image(KernelBase + 200, 10, "c:\\ä.SYS", 2000);
                replay.Event(3, 3, KernelBase + 200, 2000);
                replay.Compare(11000);
                replay.Compare(61500);
                break;
            case "top-sixteen":
                for (var i = 19; i >= 0; --i)
                {
                    var address = KernelBase + (ulong)(100 + i * 10);
                    replay.Image(address, 5, $"C:\\a{i:00}.sys", 1000);
                    replay.Event(1, i == 19 ? 10 : 1, address, 1000);
                }
                replay.Compare(11000);
                break;
            case "overflow-range":
                replay.Image(ulong.MaxValue - 3, 10, "last.sys", 1000);
                replay.Event(1, 1, ulong.MaxValue, 1000);
                replay.Image(ulong.MaxValue - 1, 1, null, 1001);
                replay.Event(2, 2, ulong.MaxValue - 1, 1001);
                replay.Event(3, 3, ulong.MaxValue, 1002);
                replay.Compare(11000);
                break;
            case "invalid-duration":
                replay.Image(KernelBase + 100, -1, "invalid.sys", 1000);
                replay.Image(KernelBase + 100, 0, "zero.sys", 1000);
                replay.Event(1, double.NaN, 0, 1000);
                replay.Event(1, double.PositiveInfinity, 0, 1000);
                replay.Event(1, 0, 0, 1000);
                replay.Event(1, -1, 0, 1000);
                replay.Event(1, 1, 0, 999);
                replay.Compare(11000);
                break;
        }
        replay.Compare(130000);
    }

    [Fact]
    public void NativePublicationRetainsStringsAcrossLossAndReportsCappedUsage()
    {
        using var replay = new Replay();
        replay.Image(KernelBase + 100, 10, "lease.sys", 1000);
        replay.Event(1, 1, KernelBase + 100, 1000);
        Assert.Equal(0, Acquire(replay.Handle, 11000, 2, out var view));
        try
        {
            Assert.Equal(3, Acquire(replay.Handle, 11000, 2, out _));
            replay.Loss(12000, 1);
            Assert.Equal("lease.sys", Text(view.MaximumModule.Name, view.MaximumModule.NameLength));
            Assert.Equal(0, QueryUsage(replay.Handle, out var usage, (uint)Marshal.SizeOf<Usage>()));
            Assert.True(usage.Current > 0);
            Assert.InRange(usage.Current, 1UL, usage.Peak);
            Assert.InRange(usage.Peak, 1UL, usage.Cap);
            Assert.Equal(1UL, usage.EventsLost);
        }
        finally { Release(replay.Handle); }
        replay.Compare(22000);
    }

    [Fact]
    public void ExplicitCapacityFailureInvalidatesPublicationUntilReset()
    {
        using var replay = new Replay(maxSlices: 1);
        Assert.Equal(0, Observe(replay.Handle, 1, 1, 0, 1000));
        Assert.Equal(5, Observe(replay.Handle, 1, 1, 0, 2000));
        Assert.Equal(3, Acquire(replay.Handle, 11000, 1, out _));
        Assert.Equal(0, Reset(replay.Handle, 20000, 0));
        Assert.Equal(0, Acquire(replay.Handle, 30000, 1, out var view));
        try { Assert.Equal(0UL, view.EventCount); }
        finally { Release(replay.Handle); }
    }

    private static string? Text(nint pointer, uint length)
        => pointer == 0 ? null : Marshal.PtrToStringUni(pointer, checked((int)length));
    private static string Name(Identity value) => Text(value.Name, value.NameLength) ?? OracleKernelModuleAddressMap.UnknownModuleName;
    private static string? Kind(uint value) => value switch
    {
        0 => null, 1 => SystemInterruptEventKinds.Isr, 2 => SystemInterruptEventKinds.Dpc,
        3 => SystemInterruptEventKinds.ThreadedDpc, 4 => SystemInterruptEventKinds.TimerDpc,
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };

    private sealed class Replay : IDisposable
    {
        private readonly OracleKernelModuleAddressMap modules = new();
        private readonly OracleSystemInterruptWindowAggregator aggregator = new();
        private long started = 1000;
        private ulong lost;
        private readonly bool nativeInputs;
        private readonly uint processorCount;
        public nint Handle { get; }
        public Replay(uint maxSlices = 128, bool recorded = false, bool nativeInputs = true, uint processorCount = 2)
        {
            this.nativeInputs = nativeInputs;
            this.processorCount = processorCount;
            var config = new Configuration { Size = (uint)Marshal.SizeOf<Configuration>(), Version = 0x10000,
                MaxModules = recorded ? 16384u : 64u, MaxPathUnits = 4096,
                MaxIdentities = recorded ? 32768u : 128u, MaxSlices = maxSlices,
                MaxSliceDrivers = recorded ? 65536u : 1024u,
                ByteCap = (recorded ? 64UL : 4UL) * 1024 * 1024, Generation = 7, StartedTick = started };
            Assert.Equal(0, Create(ref config, out var handle));
            Assert.NotEqual(0, handle);
            Handle = handle;
        }
        public unsafe void Image(ulong address, int size, string? path, long tick)
        {
            if (nativeInputs)
                fixed (char* text = path)
                    Assert.Equal(0, ImageNative(Handle, address, size > 0 ? (ulong)size : 0, text, (uint)(path?.Length ?? 0), tick, 0));
            if (tick >= started && OracleKernelModuleAddressMap.IsKernelAddress(address)) modules.Upsert(address, size, path);
        }
        public unsafe void Unload(ulong address, long tick)
        {
            if (nativeInputs) Assert.Equal(0, ImageNative(Handle, address, 0, null, 0, tick, 1));
            if (tick >= started && OracleKernelModuleAddressMap.IsKernelAddress(address)) modules.Remove(address);
        }
        public void Event(uint kind, double duration, ulong routine, long tick)
        {
            if (nativeInputs) Assert.Equal(0, Observe(Handle, kind, duration, routine, tick));
            if (!double.IsFinite(duration) || duration <= 0 || tick < started) return;
            var module = modules.Resolve(routine);
            aggregator.Observe(new(tick - started, Kind(kind)!, duration, routine, module.Name, module.Path));
        }
        public void Loss(long tick, ulong count)
        {
            if (nativeInputs) Assert.Equal(0, RefreshLoss(Handle, tick, count));
            if (count == lost) return;
            started = tick;
            lost = count;
            modules.Clear();
            aggregator.Reset();
        }
        public bool Compare(long tick, Action<string>? describe = null)
        {
            var now = new DateTimeOffset(2026, 10, 27, 0, 0, 0, TimeSpan.Zero).AddMilliseconds(tick);
            var expected = aggregator.CreateSnapshot(now, tick - started, 7, 0, checked((int)processorCount),
                new("etw-system-interrupts", SystemInterruptProviderStates.Running, "", checked((int)lost)));
            Assert.Equal(0, Acquire(Handle, tick, processorCount, out var actual));
            try
            {
                describe?.Invoke($"Interrupt snapshot: expectedCount={expected.EventCount}; nativeCount={actual.EventCount}; "
                    + $"expectedTotal={expected.TotalDurationMilliseconds:R}; nativeTotal={actual.TotalDuration:R}; "
                    + $"expectedMaximum={expected.MaximumSingleDurationMilliseconds:R}; nativeMaximum={actual.MaximumDuration:R}.");
                Assert.Equal(expected.Available, (actual.Flags & 1) != 0);
                Assert.Equal((ulong)expected.SourceGeneration, actual.Generation);
                Assert.Equal(lost, actual.EventsLost);
                Assert.Equal(expected.Window.Ticks, actual.WindowMilliseconds * TimeSpan.TicksPerMillisecond);
                Assert.Equal(expected.LogicalProcessorCount, (int)actual.ProcessorCount);
                Assert.Equal(expected.TotalDurationMilliseconds, actual.TotalDuration);
                Assert.Equal(expected.MaximumSingleDurationMilliseconds, actual.MaximumDuration);
                Assert.Equal(expected.CpuCapacityPercent, actual.CapacityPercent);
                Assert.Equal((ulong)expected.EventCount, actual.EventCount);
                Assert.Equal((ulong)expected.EventsAtOrAboveOneMillisecond, actual.LongCount);
                Assert.Equal(expected.MaximumEventKind, Kind(actual.MaximumKind));
                Assert.Equal(expected.MaximumDriverName, actual.MaximumKind == 0 ? null : Name(actual.MaximumModule));
                Assert.Equal(expected.Buckets.Count, (int)actual.BucketCount);
                for (var i = 0; i < expected.Buckets.Count; ++i)
                {
                    var row = Marshal.PtrToStructure<Bucket>(actual.Buckets + i * Marshal.SizeOf<Bucket>());
                    var oracle = expected.Buckets[i];
                    Assert.Equal(oracle.StartedAt, now.AddMilliseconds(row.Start - tick));
                    Assert.Equal(oracle.EndedAt, now.AddMilliseconds(row.End - tick));
                    Assert.Equal(oracle.TotalDurationMilliseconds, row.TotalDuration);
                    Assert.Equal(oracle.CpuCapacityPercent, row.CapacityPercent);
                    Assert.Equal((ulong)oracle.EventCount, row.EventCount);
                }
                Assert.Equal(expected.Drivers.Count, (int)actual.DriverCount);
                for (var i = 0; i < expected.Drivers.Count; ++i)
                {
                    var row = Marshal.PtrToStructure<Driver>(actual.Drivers + i * Marshal.SizeOf<Driver>());
                    var oracle = expected.Drivers[i];
                    Assert.Equal(oracle.ModuleName, Name(row.Module));
                    Assert.Equal(oracle.ModulePath, Text(row.Module.Path, row.Module.PathLength));
                    Assert.Equal(oracle.TotalDurationMilliseconds, row.TotalDuration);
                    Assert.Equal(oracle.MaximumSingleDurationMilliseconds, row.MaximumDuration);
                    Assert.Equal(oracle.CpuCapacityPercent, row.CapacityPercent);
                    Assert.Equal((ulong)oracle.EventCount, row.EventCount);
                    Assert.Equal((ulong)oracle.EventsAtOrAboveOneMillisecond, row.LongCount);
                }
            }
            finally { Release(Handle); }
            return expected.Available;
        }
        public void Dispose() => Destroy(Handle);
    }

    // Independent test-only Windows SDK reader. It never calls the Zig decoder
    // or TraceEvent and retains only counters and the first EventTrace/Header.
    // x64 EVENT_TRACE_LOGFILEW / EVENT_RECORD offsets from evntrace.h/evntcons.h.
    internal sealed class RawClock
    {
        public ulong Records, HeaderRecord, FirstImageRecord, FirstInterruptRecord, FirstSwitchRecord,
            ImagesBeforeHeader, InterruptsBeforeHeader, SwitchesBeforeHeader, LostMarkers, Isr50, Isr67;
        public long HeaderQpc, Frequency;
    }
    internal static unsafe RawClock ReadRawClock(string path)
    {
        Assert.Equal(8, IntPtr.Size);
        var result = new RawClock();
        var header = new Guid("68fdd900-4a3e-11d1-84f4-0000f80464e3");
        var image = new Guid("2cb15d1d-5fc1-11d2-abe1-00a0c911f518");
        var perf = new Guid("ce1dbfb4-137e-4da6-87b0-3f59aa102cbc");
        var thread = new Guid("3d6fa8d1-fe05-11d0-9dda-00c04fd7ba7c");
        var lost = new Guid("6a399ae0-4bc6-4de9-870b-3657f8947e7e");
        RawEventCallback callback = record =>
        {
            ++result.Records;
            if (record->Provider == lost && record->Opcode == 32) ++result.LostMarkers;
            if (result.HeaderRecord == 0 && record->Provider == header && record->Opcode == 0 && record->Qpc >= 0)
            {
                result.HeaderRecord = result.Records;
                result.HeaderQpc = record->Qpc;
            }
            if (record->Provider == image)
            {
                if (result.FirstImageRecord == 0) result.FirstImageRecord = result.Records;
                if (result.HeaderRecord == 0) ++result.ImagesBeforeHeader;
            }
            if (record->Provider == perf && (record->Opcode == 50 || record->Opcode is >= 66 and <= 69))
            {
                if (record->Opcode == 50) ++result.Isr50;
                if (record->Opcode == 67) ++result.Isr67;
                if (result.FirstInterruptRecord == 0) result.FirstInterruptRecord = result.Records;
                if (result.HeaderRecord == 0) ++result.InterruptsBeforeHeader;
            }
            if (record->Provider == thread && record->Opcode == 36)
            {
                if (result.FirstSwitchRecord == 0) result.FirstSwitchRecord = result.Records;
                if (result.HeaderRecord == 0) ++result.SwitchesBeforeHeader;
            }
        };
        fixed (char* text = path)
        {
            var logfile = new RawLogFile
            {
                FileName = (nint)text,
                ProcessMode = 0x10001000, // EVENT_RECORD | RAW_TIMESTAMP, identical to the native consumer.
                EventCallback = Marshal.GetFunctionPointerForDelegate(callback)
            };
            var handle = RawOpenTrace(ref logfile);
            Assert.True(handle != ulong.MaxValue, $"OpenTraceW failed: {Marshal.GetLastWin32Error()}.");
            try
            {
                result.Frequency = logfile.Frequency;
                Assert.Equal(1u, logfile.ClockType);
                Assert.True(result.Frequency > 0);
                Assert.Equal(0u, RawProcessTrace(ref handle, 1, 0, 0));
            }
            finally
            {
                var closed = RawCloseTrace(handle);
                GC.KeepAlive(callback);
                Assert.Equal(0u, closed);
            }
        }
        Assert.True(result.HeaderRecord > 0, "The finite ETL must contain a raw EventTrace/Header QPC.");
        return result;
    }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private unsafe delegate void RawEventCallback(RawEventRecord* record);
    [StructLayout(LayoutKind.Explicit, Size = 112)] private struct RawEventRecord
    {
        [FieldOffset(16)] public long Qpc;
        [FieldOffset(24)] public Guid Provider;
        [FieldOffset(45)] public byte Opcode;
    }
    [StructLayout(LayoutKind.Explicit, Size = 448)] private struct RawLogFile
    {
        [FieldOffset(0)] public nint FileName;
        [FieldOffset(28)] public uint ProcessMode;
        [FieldOffset(376)] public long Frequency;
        [FieldOffset(392)] public uint ClockType;
        [FieldOffset(424)] public nint EventCallback;
    }
    [DllImport("advapi32.dll", EntryPoint = "OpenTraceW", SetLastError = true)]
    private static extern ulong RawOpenTrace(ref RawLogFile logfile);
    [DllImport("advapi32.dll", EntryPoint = "ProcessTrace")]
    private static extern uint RawProcessTrace(ref ulong handle, uint count, nint start, nint end);
    [DllImport("advapi32.dll", EntryPoint = "CloseTrace")]
    private static extern uint RawCloseTrace(ulong handle);

    [StructLayout(LayoutKind.Sequential)] private struct Configuration
    {
        public uint Size, Version, MaxModules, MaxPathUnits, MaxIdentities, MaxSlices, MaxSliceDrivers, Reserved;
        public ulong ByteCap, Generation;
        public long StartedTick;
        public ulong EventsLost;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Identity { public nint Path, Name; public uint PathLength, NameLength; }
    [StructLayout(LayoutKind.Sequential)] private struct Driver
    {
        public Identity Module;
        public double TotalDuration, MaximumDuration, CapacityPercent;
        public ulong EventCount, LongCount;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Bucket
    {
        public long Start, End;
        public double TotalDuration, CapacityPercent;
        public ulong EventCount;
    }
    [StructLayout(LayoutKind.Sequential)] private struct View
    {
        public ulong Generation, EventsLost;
        public long Start, End, WindowMilliseconds;
        public uint ProcessorCount, Flags;
        public double TotalDuration, MaximumDuration, CapacityPercent;
        public ulong EventCount, LongCount;
        public uint MaximumKind, Reserved;
        public Identity MaximumModule;
        public uint DriverCount, BucketCount;
        public nint Drivers, Buckets;
        public ulong CurrentBytes, PeakBytes;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Usage
    {
        public ulong Current, Peak, Cap, FailedAllocations;
        public int LastError;
        public uint TraceError;
        public ulong EventsLost;
    }
    [StructLayout(LayoutKind.Sequential)] private struct ResidencyConfiguration
    {
        public uint Size, Version;
        public long Frequency;
        public uint WindowMilliseconds, MaximumPairs,
            MaximumProcesses, MaximumThreads, MaximumProcessors, MaximumNameBytes;
        public ulong ByteCap;
        public uint ProcessorCount, Reserved;
        public nint Processors;
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativeTraceStatus
    {
        public uint WindowsStatus;
        public int OwnerStatus;
        public ulong Records, Decoded, Rejected, Unhandled, Switches;
        public uint EventsLost, BuffersLost;
        public long Frequency;
        public uint ProcessorCount, OsBufferSize;
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativeTraceClock
    {
        public long Frequency, HeaderQpc, QpcBefore, QpcAfter, Filetime;
        public uint Flags, Reserved;
    }
    [StructLayout(LayoutKind.Sequential)] private struct PathMappingStatus
    {
        public uint Entries, FailedDriveMask, Flags, Reserved;
    }
    private const string Library = "ResourceManager.NativeCore";
    [DllImport(Library, EntryPoint = "rm_kernel_etw_interrupt_create", CallingConvention = CallingConvention.Cdecl)]
    private static extern int Create(ref Configuration config, out nint handle);
    [DllImport(Library, EntryPoint = "rm_kernel_etw_interrupt_destroy", CallingConvention = CallingConvention.Cdecl)]
    private static extern void Destroy(nint handle);
    [DllImport(Library, EntryPoint = "rm_kernel_etw_interrupt_image", CallingConvention = CallingConvention.Cdecl)]
    private static extern unsafe int ImageNative(nint handle, ulong address, ulong size, char* path, uint length, long tick, uint unload);
    [DllImport(Library, EntryPoint = "rm_kernel_etw_interrupt_observe", CallingConvention = CallingConvention.Cdecl)]
    private static extern int Observe(nint handle, uint kind, double duration, ulong routine, long tick);
    [DllImport(Library, EntryPoint = "rm_kernel_etw_interrupt_reset", CallingConvention = CallingConvention.Cdecl)]
    private static extern int Reset(nint handle, long tick, ulong lost);
    [DllImport(Library, EntryPoint = "rm_kernel_etw_interrupt_refresh_loss", CallingConvention = CallingConvention.Cdecl)]
    private static extern int RefreshLoss(nint handle, long tick, ulong lost);
    [DllImport(Library, EntryPoint = "rm_kernel_etw_interrupt_acquire_view", CallingConvention = CallingConvention.Cdecl)]
    private static extern int Acquire(nint handle, long tick, uint processorCount, out View view);
    [DllImport(Library, EntryPoint = "rm_kernel_etw_interrupt_release_view", CallingConvention = CallingConvention.Cdecl)]
    private static extern void Release(nint handle);
    [DllImport(Library, EntryPoint = "rm_kernel_etw_interrupt_query_usage", CallingConvention = CallingConvention.Cdecl)]
    private static extern int QueryUsage(nint handle, out Usage usage, uint size);
    [DllImport(Library, EntryPoint = "rm_kernel_etw_create", CallingConvention = CallingConvention.Cdecl)]
    private static extern int CreateResidency(ref ResidencyConfiguration config, out nint handle);
    [DllImport(Library, EntryPoint = "rm_kernel_etw_destroy", CallingConvention = CallingConvention.Cdecl)]
    private static extern int DestroyResidency(nint handle);
    [DllImport(Library, EntryPoint = "rm_kernel_etw_trace_open", CallingConvention = CallingConvention.Cdecl)]
    private static extern unsafe int OpenTrace(nint owner, char* path, uint length, uint realtime, out nint trace);
    [DllImport(Library, EntryPoint = "rm_kernel_etw_trace_bind_interrupts", CallingConvention = CallingConvention.Cdecl)]
    private static extern int BindInterrupts(nint trace, nint interrupts, long qpcOrigin, long tickOrigin, uint replayClock);
    [DllImport(Library, EntryPoint = "rm_kernel_etw_trace_process", CallingConvention = CallingConvention.Cdecl)]
    private static extern int ProcessTrace(nint trace, ulong switchLimit);
    [DllImport(Library, EntryPoint = "rm_kernel_etw_trace_status", CallingConvention = CallingConvention.Cdecl)]
    private static extern int QueryTraceStatus(nint trace, out NativeTraceStatus status, uint size);
    [DllImport(Library, EntryPoint = "rm_kernel_etw_trace_clock", CallingConvention = CallingConvention.Cdecl)]
    private static extern int QueryTraceClock(nint trace, out NativeTraceClock clock, uint size);
    [DllImport(Library, EntryPoint = "rm_kernel_etw_trace_query_path_mapping", CallingConvention = CallingConvention.Cdecl)]
    private static extern int QueryPathMapping(nint trace, out PathMappingStatus status, uint size);
    [DllImport(Library, EntryPoint = "rm_kernel_etw_trace_close", CallingConvention = CallingConvention.Cdecl)]
    private static extern int CloseTrace(nint trace);
}
