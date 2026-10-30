using Resource_Manager_APP.Tests.Oracles.KernelEtw;
using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using ResourceManager.App.Domain.SystemHealth;
using ResourceManager.App.Infrastructure.CpuTopology;
using ResourceManager.App.Infrastructure.SystemHealth.Interrupts;
using ResourceManager.App.Infrastructure.Telemetry.Etw;

#pragma warning disable CS0618 // Raw ETL QPC values are diagnostic inputs and digest fields.

namespace Resource_Manager_APP.Tests;

/// <summary>
/// Opt-in diagnostic probes for the finite ETW inputs. These tests never write to
/// the ETLs and never make timing a correctness threshold.
/// </summary>
public sealed class KernelEtwZigReplayCostTests
{
    private const string Library = "ResourceManager.NativeCore";
    private const long MaximumCostInputBytes = 128L * 1024 * 1024;
    private const int MeasurementRounds = 2;
    private const int WarmupSwitchLimit = 20_000;
    private const long WarmupRecordLimit = 100_000;
    private const int MaximumInventoryShapes = 256;
    private const long MaximumInventoryRecords = 100_000_000;
    private const ulong FnvOffsetBasis = 14695981039346656037UL;
    private const ulong FnvPrime = 1099511628211UL;
    private static readonly TimeSpan MaximumInventoryDuration = TimeSpan.FromSeconds(120);

    private readonly Xunit.Abstractions.ITestOutputHelper output;

    public KernelEtwZigReplayCostTests(Xunit.Abstractions.ITestOutputHelper output) => this.output = output;

    [EnvironmentVariableFact("RESOURCE_MANAGER_ETW_REPLAY_BENCHMARK", "1")]
    public void CpuResidencyReplayReportsPerRawEventCostWithoutTimingThresholds()
    {
        var path = ResolveReplayPath();
        var file = new FileInfo(path);
        Assert.True(file.Exists, $"The finite ETW replay input does not exist: {path}");
        Assert.InRange(file.Length, 1, MaximumCostInputBytes);

        var metadata = ReadInputMetadata(path);
        output.WriteLine($"scope=CPU residency only; enabled=Process|Thread|ContextSwitch; windowMs=5000; bucketMs=50; "
            + "publication=excluded; interrupts=excluded; timing=diagnostic-only; production-CPU-forecast=not-claimed.");
        output.WriteLine($"input={path}; bytes={file.Length}; qpcFrequency={metadata.QpcFrequency}; "
            + $"processors={metadata.ProcessorCount}; initialEventsLost={metadata.InitialEventsLost}.");

        // Exercise TraceEvent/JIT and the native DLL before measuring. Warm-up is
        // a bounded prefix; measured rounds use the complete finite file.
        var managedWarmup = RunManaged(path, metadata, warmup: true);
        var nativeWarmup = RunNative(path, metadata, warmup: true, managedWarmup.CaptureSeconds);
        output.WriteLine($"warmup managed: records={managedWarmup.RawRecords}; switches={managedWarmup.Switches}; stop={managedWarmup.StopReason}.");
        output.WriteLine($"warmup native: records={nativeWarmup.Trace.Records}; switches={nativeWarmup.Trace.Switches}; "
            + $"windows={nativeWarmup.Trace.WindowsStatus}; rejected={nativeWarmup.Trace.Rejected}.");

        var managed = new ManagedRun[MeasurementRounds];
        var native = new NativeRun[MeasurementRounds];
        for (var round = 0; round < MeasurementRounds; round++)
        {
            // Alternate the order to avoid always favoring the second pass.
            if ((round & 1) == 0)
            {
                managed[round] = RunManaged(path, metadata, warmup: false);
                native[round] = RunNative(path, metadata, warmup: false, managed[round].CaptureSeconds);
            }
            else
            {
                native[round] = RunNative(path, metadata, warmup: false, managed[0].CaptureSeconds);
                managed[round] = RunManaged(path, metadata, warmup: false);
            }
        }

        for (var round = 0; round < MeasurementRounds; round++)
        {
            WriteManagedRun(round + 1, managed[round]);
            WriteNativeRun(round + 1, native[round]);
            Assert.Equal("eof", managed[round].StopReason);
            Assert.Equal(0, native[round].ProcessStatus);
            Assert.Equal(0, native[round].Trace.OwnerStatus);
            Assert.Equal(managed[round].RawRecords, checked((long)native[round].Trace.Records));
            Assert.Equal((ulong)managed[round].Switches, native[round].Trace.Switches);
            Assert.Equal(managed[round].EventsLost, native[round].Trace.EventsLost);
            Assert.Equal(metadata.QpcFrequency, native[round].Trace.QpcFrequency);
            Assert.Equal(metadata.ProcessorCount, native[round].Trace.ProcessorCount);
            Assert.True(managed[round].Switches > 0, "The cost replay must exercise CPU residency context switches.");
        }

        var managedPerEvent = managed.Average(static run => run.CpuNanosecondsPerRawEvent);
        var nativePerEvent = native.Average(static run => run.CpuNanosecondsPerRawEvent);
        output.WriteLine($"mean CPU ns/raw-event: managed={managedPerEvent:F1}; native={nativePerEvent:F1}; "
            + $"ratio(native/managed)={(managedPerEvent == 0 ? double.NaN : nativePerEvent / managedPerEvent):F3}.");
        output.WriteLine("These measurements include only the CPU-residency subset. They do not estimate the full production ETW thread after migration.");
    }

    [EnvironmentVariableFact("RESOURCE_MANAGER_ETW_REPLAY_BENCHMARK", "1")]
    public void CpuResidencyAndInterruptOwnersReplayTheSameFiniteEtlWithoutPublication()
    {
        var path = ResolveReplayPath();
        var file = new FileInfo(path);
        Assert.True(file.Exists, $"The finite ETW replay input does not exist: {path}");
        Assert.InRange(file.Length, 1, MaximumCostInputBytes);

        var metadata = ReadInputMetadata(path);
        output.WriteLine("scope=CPU residency + kernel image/interrupt owners; enabled=Process|Thread|ContextSwitch|Interrupt|DPC|ImageLoad; "
            + "CPU windowMs=5000; CPU bucketMs=50; interrupt window/buckets=production defaults; publication=excluded; timing=diagnostic-only.");
        output.WriteLine($"input={path}; bytes={file.Length}; qpcFrequency={metadata.QpcFrequency}; processors={metadata.ProcessorCount}; "
            + $"initialEventsLost={metadata.InitialEventsLost}; eventTickOriginQpc={metadata.SessionStartQpc}; "
            + "native interrupt-duration anchor=ETL EventTrace/Header QPC (unchanged).");

        var managedWarmup = RunManagedWithInterrupts(path, metadata, warmup: true);
        var nativeWarmup = RunNativeWithInterrupts(path, metadata, warmup: true, managedWarmup.CaptureSeconds);
        output.WriteLine($"combined warmup managed: records={managedWarmup.RawRecords}; switches={managedWarmup.Switches}; "
            + $"images={managedWarmup.Images}; validInterrupts={managedWarmup.ValidInterrupts}; stop={managedWarmup.StopReason}.");
        output.WriteLine($"combined warmup native: records={nativeWarmup.Trace.Records}; switches={nativeWarmup.Trace.Switches}; "
            + $"rejected={nativeWarmup.Trace.Rejected}; residencyPeak={nativeWarmup.ResidencyUsage.PeakBytes}; "
            + $"interruptPeak={nativeWarmup.InterruptUsage.Peak}.");

        var managed = new ManagedCombinedRun[MeasurementRounds];
        var native = new NativeCombinedRun[MeasurementRounds];
        for (var round = 0; round < MeasurementRounds; round++)
        {
            if ((round & 1) == 0)
            {
                managed[round] = RunManagedWithInterrupts(path, metadata, warmup: false);
                native[round] = RunNativeWithInterrupts(path, metadata, warmup: false, managed[round].CaptureSeconds);
            }
            else
            {
                native[round] = RunNativeWithInterrupts(path, metadata, warmup: false, managed[0].CaptureSeconds);
                managed[round] = RunManagedWithInterrupts(path, metadata, warmup: false);
            }
        }

        for (var round = 0; round < MeasurementRounds; round++)
        {
            WriteManagedCombinedRun(round + 1, managed[round]);
            WriteNativeCombinedRun(round + 1, native[round]);
            Assert.Equal("eof", managed[round].StopReason);
            Assert.Equal(0, native[round].ProcessStatus);
            Assert.Equal(0, native[round].Trace.OwnerStatus);
            Assert.Equal(0u, native[round].Trace.WindowsStatus);
            Assert.Equal(managed[round].RawRecords, checked((long)native[round].Trace.Records));
            Assert.Equal((ulong)managed[round].Switches, native[round].Trace.Switches);
            Assert.Equal(managed[round].EventsLost, native[round].Trace.EventsLost);
            Assert.Equal((ulong)managed[round].EventsLost, native[round].InterruptUsage.EventsLost);
            Assert.Equal(managed[round].QpcFrequency, native[round].Trace.QpcFrequency);
            Assert.Equal(managed[round].ProcessorCount, native[round].Trace.ProcessorCount);
            Assert.True(managed[round].Images > 0, "The combined replay must exercise the interrupt owner's image map.");
            Assert.True(managed[round].ValidInterrupts > 0, "The combined replay must exercise the interrupt window owner.");
            Assert.True(managed[round].Switches > 0, "The combined replay must exercise CPU residency context switches.");
            Assert.True(native[round].InterruptUsage.Peak > 0, "The combined replay must exercise native interrupt-owner storage.");
        }

        var managedPerEvent = managed.Average(static run => run.CpuNanosecondsPerRawEvent);
        var nativePerEvent = native.Average(static run => run.CpuNanosecondsPerRawEvent);
        output.WriteLine($"combined mean CPU ns/raw-event: managed={managedPerEvent:F1}; native={nativePerEvent:F1}; "
            + $"ratio(native/managed)={(managedPerEvent == 0 ? double.NaN : nativePerEvent / managedPerEvent):F3}.");
        output.WriteLine("This measures both owner event paths and excludes snapshot publication; it has no timing pass/fail threshold.");
    }

    [EnvironmentVariableFact("RESOURCE_MANAGER_ETW_INPUT_INVENTORY", "1")]
    public void ExistingEtlsInventoryRelevantFamiliesAndRawTaskShapes()
    {
        var root = FindRepositoryRoot(Directory.GetCurrentDirectory())
            ?? FindRepositoryRoot(AppContext.BaseDirectory)
            ?? throw new DirectoryNotFoundException("Could not find the Resource Manager worktree root.");
        // RESOURCE_MANAGER_ETW_TRAY_INPUT optionally adds a tray-state CPU capture after the repository's interrupt capture.
        var interruptInput = Path.Combine(root, ".local", "etw-capture", "kernel-cpu-interrupt.etl");
        var trayInput = Environment.GetEnvironmentVariable("RESOURCE_MANAGER_ETW_TRAY_INPUT");
        var inputs = string.IsNullOrWhiteSpace(trayInput) ? new[] { interruptInput } : new[] { interruptInput, trayInput };

        foreach (var path in inputs)
        {
            var file = new FileInfo(path);
            Assert.True(file.Exists, $"Expected finite ETL inventory input does not exist: {path}");
            var result = RunInventory(path);
            output.WriteLine($"inventory input={path}; bytes={file.Length}; records={result.TotalRecords}; "
                + $"eventsLost={result.EventsLost}; stop={result.StopReason}; complete={result.Complete}; "
                + $"wallMs={result.WallMilliseconds:F0}; relevantShapes={result.Shapes.Count}; "
                + $"shapeOverflowRecords={result.ShapeOverflowRecords}.");

            for (var family = 0; family < (int)InputFamily.Count; family++)
            {
                output.WriteLine($"family {FamilyNames[family]}={result.FamilyCounts[family]}.");
                if (family <= (int)InputFamily.UdpReceiveV6)
                    output.WriteLine($"digest input={path}; variant={DigestNames[family]}; "
                        + $"count={result.FamilyCounts[family]}; fnv1a64={result.Digests[family]:x16}.");
                if (result.HasSample[family]) WriteSample(FamilyNames[family], result, family);
            }
            output.WriteLine($"inventory missingNetworkAddressRecords={result.MissingNetworkAddressRecords}.");

            foreach (var pair in result.Shapes.OrderBy(static pair => pair.Key.TaskGuid)
                         .ThenBy(static pair => pair.Key.Opcode)
                         .ThenBy(static pair => pair.Key.Version)
                         .ThenBy(static pair => pair.Key.PointerSize))
            {
                var shape = pair.Value;
                output.WriteLine($"shape taskGuid={pair.Key.TaskGuid:D}; opcode={pair.Key.Opcode}; version={pair.Key.Version}; "
                    + $"pointerSize={pair.Key.PointerSize}; payloadBytes={shape.MinimumPayloadBytes}..{shape.MaximumPayloadBytes}; "
                    + $"records={shape.Records}; families={FormatFamilies(shape.FamilyMask)}.");
            }

            Assert.True(result.TotalRecords > 0, $"No ETL records were observed in {path}.");
        }
    }

    private void WriteManagedRun(int round, ManagedRun run)
    {
        output.WriteLine($"managed round={round}; records={run.RawRecords}; switches={run.Switches}; "
            + $"rejected=not-exposed-by-TraceEvent; eventsLost={run.EventsLost}; "
            + $"threadCpuMs={run.ThreadCpuMilliseconds:F3}; wallMs={run.WallMilliseconds:F3}; "
            + $"cpuNsPerRawEvent={run.CpuNanosecondsPerRawEvent:F1}; "
            + $"recordedRate={run.RecordsPerCaptureSecond:F0}/s; "
            + $"cpuAtRecordedRate={run.CpuPercentAtRecordedRate:F3}% of one core; "
            + $"ownerRetainedBucketBytes={run.Owner.RetainedBucketBytes}; ownerMaximumBucketBytes={run.Owner.MaximumBucketBytes}; "
            + $"activePairs={run.Owner.ActivePairCount}; processes={run.Owner.CurrentProcessCount}; "
            + $"threads={run.Owner.CurrentThreadCount}; stop={run.StopReason}.");
    }

    private void WriteNativeRun(int round, NativeRun run)
    {
        output.WriteLine($"native round={round}; records={run.Trace.Records}; decoded={run.Trace.Decoded}; "
            + $"rejected={run.Trace.Rejected}; unhandled={run.Trace.Unhandled}; switches={run.Trace.Switches}; "
            + $"eventsLost={run.Trace.EventsLost}; buffersLost={run.Trace.BuffersLost}; "
            + $"threadCpuMs={run.ThreadCpuMilliseconds:F3}; wallMs={run.WallMilliseconds:F3}; "
            + $"cpuNsPerRawEvent={run.CpuNanosecondsPerRawEvent:F1}; "
            + $"recordedRate={run.RecordsPerCaptureSecond:F0}/s; "
            + $"cpuAtRecordedRate={run.CpuPercentAtRecordedRate:F3}% of one core; "
            + $"ownerCurrentBytes={run.Owner.CurrentBytes}; ownerPeakBytes={run.Owner.PeakBytes}; "
            + $"ownerByteCap={run.Owner.ByteCap}; failedAllocations={run.Owner.FailedAllocations}; "
            + $"ownerLastError={run.Owner.LastError}; ownerTraceError={run.Owner.TraceError}; "
            + $"windowsStatus={run.Trace.WindowsStatus}; ownerStatus={run.Trace.OwnerStatus}.");
    }

    private void WriteManagedCombinedRun(int round, ManagedCombinedRun run)
    {
        output.WriteLine($"managed combined round={round}; records={run.RawRecords}; switches={run.Switches}; "
            + $"images={run.Images}; ISR={run.InterruptCallbacks[0]}; DPC={run.InterruptCallbacks[1]}; "
            + $"threadedDPC={run.InterruptCallbacks[2]}; timerDPC={run.InterruptCallbacks[3]}; "
            + $"validInterrupts={run.ValidInterrupts}; eventsLost={run.EventsLost}; rejected=not-exposed-by-TraceEvent; "
            + $"threadCpuMs={run.ThreadCpuMilliseconds:F3}; wallMs={run.WallMilliseconds:F3}; "
            + $"cpuNsPerRawEvent={run.CpuNanosecondsPerRawEvent:F1}; recordedRate={run.RecordsPerCaptureSecond:F0}/s; "
            + $"cpuAtRecordedRate={run.CpuPercentAtRecordedRate:F3}% of one core; "
            + $"residencyRetainedBucketBytes={run.Residency.RetainedBucketBytes}; "
            + $"residencyMaximumBucketBytes={run.Residency.MaximumBucketBytes}; "
            + "interruptOwnerBytes=not-exposed-by-managed-owner; publication=excluded; "
            + $"eventTickOriginQpc={run.SessionStartQpc}; stop={run.StopReason}.");
    }

    private void WriteNativeCombinedRun(int round, NativeCombinedRun run)
    {
        output.WriteLine($"native combined round={round}; records={run.Trace.Records}; decoded={run.Trace.Decoded}; "
            + $"rejected={run.Trace.Rejected}; unhandled={run.Trace.Unhandled}; switches={run.Trace.Switches}; "
            + $"eventsLost={run.Trace.EventsLost}; buffersLost={run.Trace.BuffersLost}; "
            + $"threadCpuMs={run.ThreadCpuMilliseconds:F3}; wallMs={run.WallMilliseconds:F3}; "
            + $"cpuNsPerRawEvent={run.CpuNanosecondsPerRawEvent:F1}; recordedRate={run.RecordsPerCaptureSecond:F0}/s; "
            + $"cpuAtRecordedRate={run.CpuPercentAtRecordedRate:F3}% of one core; "
            + $"residencyOwnerCurrentBytes={run.ResidencyUsage.CurrentBytes}; residencyOwnerPeakBytes={run.ResidencyUsage.PeakBytes}; "
            + $"residencyOwnerByteCap={run.ResidencyUsage.ByteCap}; "
            + $"interruptOwnerCurrentBytes={run.InterruptUsage.Current}; interruptOwnerPeakBytes={run.InterruptUsage.Peak}; "
            + $"interruptOwnerByteCap={run.InterruptUsage.Cap}; interruptOwnerFailedAllocations={run.InterruptUsage.FailedAllocations}; "
            + $"interruptOwnerEventsLost={run.InterruptUsage.EventsLost}; headerQpc={run.Clock.HeaderQpc}; "
            + $"eventTickOriginQpc={run.EventTickOriginQpc}; publication=excluded.");
    }

    private static ManagedCombinedRun RunManagedWithInterrupts(string path, InputMetadata metadata, bool warmup)
        => OnDedicatedThread(() =>
        {
            using var source = new ETWTraceEventSource(path);
            var frequency = ReadQpcFrequency(source);
            Assert.Equal(metadata.QpcFrequency, frequency);
            // TraceEvent initializes sessionStartTimeQPC during processing.
            // An independent raw pass defines this origin before dispatch.
            var sessionStartQpc = metadata.SessionStartQpc;
            var residency = new OracleCpuResidencyAggregationBuffer(frequency, 5000, 65536, 50);
            if (metadata.InitialEventsLost != 0) residency.ResetAfterEventLoss();
            var interrupts = new ManagedInterruptOwner(1000, metadata.InitialEventsLost);
            var parser = new KernelTraceEventParser(source);
            var dispatcher = new OracleKernelEtwSubscriptionDispatcher();
            dispatcher.AttachParser(parser);
            var interruptCallbacks = new long[4];
            long validInterrupts = 0;
            long switches = 0;
            long images = 0;
            long rawRecords = 0;
            long? firstQpc = null;
            long lastQpc = 0;
            var lost = metadata.InitialEventsLost;
            var stopReason = "eof";

            long Tick(long qpc)
                => checked(1000 + (long)(((Int128)qpc - sessionStartQpc) * 1000 / frequency));

            void ObserveInterrupt(uint kind, double duration, ulong routine, long qpc)
            {
                interruptCallbacks[kind - 1]++;
                if (interrupts.Observe(kind, duration, routine, Tick(qpc))) validInterrupts++;
            }

            dispatcher.InitializeSubscriptions(
                [new(1, "cpu-residency-and-interrupt-replay-cost",
                    KernelTraceEventParser.Keywords.Process | KernelTraceEventParser.Keywords.Thread
                        | KernelTraceEventParser.Keywords.ContextSwitch | KernelTraceEventParser.Keywords.Interrupt
                        | KernelTraceEventParser.Keywords.DeferedProcedureCalls | KernelTraceEventParser.Keywords.ImageLoad,
                    (callbacks, _) =>
                    {
                        OracleCpuResidencyEventAdapter.BindKernelEventCallbacks(callbacks, residency,
                            () => !warmup || switches < WarmupSwitchLimit, static _ => null);
                        callbacks.ThreadCSwitch += _ =>
                        {
                            switches++;
                            if (warmup && switches >= WarmupSwitchLimit)
                            {
                                stopReason = "warmup-switch-limit";
                                source.StopProcessing();
                            }
                        };
                        callbacks.ImageDCStart += data =>
                        {
                            images++;
                            interrupts.Image(data.ImageBase, data.ImageSize, data.FileName, false, Tick(data.TimeStampQPC));
                        };
                        callbacks.ImageLoad += data =>
                        {
                            images++;
                            interrupts.Image(data.ImageBase, data.ImageSize, data.FileName, false, Tick(data.TimeStampQPC));
                        };
                        callbacks.ImageUnload += data =>
                        {
                            images++;
                            interrupts.Image(data.ImageBase, data.ImageSize, data.FileName, true, Tick(data.TimeStampQPC));
                        };
                        callbacks.PerfInfoISR += data => ObserveInterrupt(1, data.ElapsedTimeMSec, data.Routine, data.TimeStampQPC);
                        callbacks.PerfInfoDPC += data => ObserveInterrupt(2, data.ElapsedTimeMSec, data.Routine, data.TimeStampQPC);
                        callbacks.PerfInfoThreadedDPC += data => ObserveInterrupt(3, data.ElapsedTimeMSec, data.Routine, data.TimeStampQPC);
                        callbacks.PerfInfoTimerDPC += data => ObserveInterrupt(4, data.ElapsedTimeMSec, data.Routine, data.TimeStampQPC);
                    })], new(1, DateTimeOffset.UnixEpoch));

            source.AllEvents += data =>
            {
                rawRecords++;
                firstQpc = Math.Min(firstQpc ?? data.TimeStampQPC, data.TimeStampQPC);
                lastQpc = Math.Max(lastQpc, data.TimeStampQPC);
                var currentLost = checked((uint)source.EventsLost);
                if (currentLost != lost)
                {
                    var tick = Tick(data.TimeStampQPC);
                    residency.ResetAfterEventLoss();
                    interrupts.Reset(tick, currentLost);
                    lost = currentLost;
                }
                if (warmup && rawRecords >= WarmupRecordLimit)
                {
                    stopReason = "warmup-record-limit";
                    source.StopProcessing();
                }
            };

            var started = Stopwatch.GetTimestamp();
            var threadStarted = ReadCurrentThreadCpuTime();
            source.Process();
            var threadEnded = ReadCurrentThreadCpuTime();
            var wall = Stopwatch.GetElapsedTime(started);
            var captureSeconds = firstQpc is { } first && lastQpc > first
                ? (lastQpc - first) / (double)frequency : 0;
            return new ManagedCombinedRun(rawRecords, switches, images, interruptCallbacks, validInterrupts,
                checked((uint)source.EventsLost), frequency, checked((uint)source.NumberOfProcessors), sessionStartQpc,
                wall.TotalMilliseconds, (threadEnded - threadStarted) / 10_000.0, residency.GetDiagnostics(),
                captureSeconds, stopReason);
        });

    private static unsafe NativeCombinedRun RunNativeWithInterrupts(string path, InputMetadata metadata,
        bool warmup, double captureSeconds)
        => OnDedicatedThread(() =>
        {
            var residencyConfiguration = new ResidencyConfiguration
            {
                Size = (uint)Marshal.SizeOf<ResidencyConfiguration>(), Version = 0x10000,
                Frequency = metadata.QpcFrequency, WindowMilliseconds = 5000, BucketMilliseconds = 50,
                MaximumPairs = 65536, MaximumCells = 2_000_000, MaximumProcesses = 32768,
                MaximumThreads = 131072, MaximumProcessors = 4096, MaximumNameBytes = 8 * 1024 * 1024,
                ByteCap = 256UL * 1024 * 1024
            };
            var interruptConfiguration = new NativeInterruptConfiguration
            {
                Size = (uint)Marshal.SizeOf<NativeInterruptConfiguration>(), Version = 0x10000,
                MaximumModules = 16384, MaximumPathUnits = 4096, MaximumIdentities = 32768,
                MaximumSlices = 128, MaximumSliceDrivers = 65536, ByteCap = 64UL * 1024 * 1024,
                Generation = 7, StartedTick = 1000, EventsLost = metadata.InitialEventsLost
            };
            Assert.Equal(0, CreateResidency(ref residencyConfiguration, out var residencyOwner));
            Assert.NotEqual(nint.Zero, residencyOwner);
            nint interruptOwner = 0;
            nint trace = 0;
            try
            {
                Assert.Equal(0, CreateInterruptOwner(ref interruptConfiguration, out interruptOwner));
                Assert.NotEqual(nint.Zero, interruptOwner);
                fixed (char* pathPointer = path)
                    Assert.Equal(0, OpenTrace(residencyOwner, pathPointer, (uint)path.Length, 0, out trace));
                Assert.NotEqual(nint.Zero, trace);
                Assert.Equal(0, BindInterrupts(trace, interruptOwner, metadata.SessionStartQpc, 1000, 1));
                Assert.Equal(0, QueryNativeTraceClock(trace, out var clock, (uint)Marshal.SizeOf<NativeTraceClock>()));
                Assert.Equal(metadata.QpcFrequency, clock.Frequency);
                Assert.Equal(0u, clock.Flags & 2u); // No raw record has been delivered before ProcessTrace.

                var started = Stopwatch.GetTimestamp();
                var threadStarted = ReadCurrentThreadCpuTime();
                var processStatus = ProcessTrace(trace, warmup ? (ulong)WarmupSwitchLimit : 0UL);
                var threadEnded = ReadCurrentThreadCpuTime();
                var wall = Stopwatch.GetElapsedTime(started);
                Assert.Equal(0, QueryNativeTraceClock(trace, out clock, (uint)Marshal.SizeOf<NativeTraceClock>()));
                Assert.Equal(2u, clock.Flags & 2u);
                Assert.Equal(metadata.SessionStartQpc, clock.HeaderQpc);
                Assert.Equal(0, QueryTraceStatus(trace, out var status, (uint)Marshal.SizeOf<NativeTraceStatus>()));
                Assert.Equal(0, QueryUsage(residencyOwner, out var residencyUsage, (uint)Marshal.SizeOf<NativeUsage>()));
                Assert.Equal(0, QueryInterruptUsage(interruptOwner, out var interruptUsage,
                    (uint)Marshal.SizeOf<NativeInterruptUsage>()));
                Assert.Equal(0, CloseTrace(trace));
                trace = 0;
                DestroyInterruptOwner(interruptOwner);
                interruptOwner = 0;
                Assert.Equal(0, DestroyResidency(residencyOwner));
                residencyOwner = 0;
                return new NativeCombinedRun(processStatus, status, clock, residencyUsage, interruptUsage,
                    metadata.SessionStartQpc, (threadEnded - threadStarted) / 10_000.0,
                    wall.TotalMilliseconds, captureSeconds);
            }
            finally
            {
                if (trace != 0) _ = CloseTrace(trace);
                if (interruptOwner != 0) DestroyInterruptOwner(interruptOwner);
                if (residencyOwner != 0) _ = DestroyResidency(residencyOwner);
            }
        });

    private static ManagedRun RunManaged(string path, InputMetadata metadata, bool warmup)
        => OnDedicatedThread(() =>
        {
            using var source = new ETWTraceEventSource(path);
            var frequency = ReadQpcFrequency(source);
            Assert.Equal(metadata.QpcFrequency, frequency);
            var buffer = new OracleCpuResidencyAggregationBuffer(frequency, 5000, 65536, 50);
            if (metadata.InitialEventsLost != 0) buffer.ResetAfterEventLoss();

            var parser = new KernelTraceEventParser(source);
            var dispatcher = new OracleKernelEtwSubscriptionDispatcher();
            dispatcher.AttachParser(parser);
            long rawRecords = 0;
            long switches = 0;
            long? firstQpc = null;
            long lastQpc = 0;
            var stopReason = "eof";
            source.AllEvents += data =>
            {
                rawRecords++;
                firstQpc = Math.Min(firstQpc ?? data.TimeStampQPC, data.TimeStampQPC);
                lastQpc = Math.Max(lastQpc, data.TimeStampQPC);
                if (warmup && rawRecords >= WarmupRecordLimit)
                {
                    stopReason = "warmup-record-limit";
                    source.StopProcessing();
                }
            };

            dispatcher.InitializeSubscriptions(
                [new(1, "cpu-residency-replay-cost",
                    KernelTraceEventParser.Keywords.Process | KernelTraceEventParser.Keywords.Thread
                        | KernelTraceEventParser.Keywords.ContextSwitch,
                    (callbacks, _) =>
                    {
                        OracleCpuResidencyEventAdapter.BindKernelEventCallbacks(callbacks, buffer,
                            () => !warmup || switches < WarmupSwitchLimit, static _ => null);
                        callbacks.ThreadCSwitch += _ =>
                        {
                            switches++;
                            if (warmup && switches >= WarmupSwitchLimit)
                            {
                                stopReason = "warmup-switch-limit";
                                source.StopProcessing();
                            }
                        };
                    })], new(1, DateTimeOffset.UnixEpoch));

            var started = Stopwatch.GetTimestamp();
            var threadStarted = ReadCurrentThreadCpuTime();
            source.Process();
            var threadEnded = ReadCurrentThreadCpuTime();
            var wall = Stopwatch.GetElapsedTime(started);
            var eventsLost = checked((uint)source.EventsLost);
            var owner = buffer.GetDiagnostics();
            var captureSeconds = firstQpc is { } first && lastQpc > first
                ? (lastQpc - first) / (double)frequency : 0;
            return new ManagedRun(rawRecords, switches, eventsLost, wall.TotalMilliseconds,
                (threadEnded - threadStarted) / 10_000.0, owner, captureSeconds, stopReason);
        });

    private static unsafe NativeRun RunNative(string path, InputMetadata metadata, bool warmup, double captureSeconds)
        => OnDedicatedThread(() =>
        {
            var configuration = new ResidencyConfiguration
            {
                Size = (uint)Marshal.SizeOf<ResidencyConfiguration>(), Version = 0x10000,
                Frequency = metadata.QpcFrequency, WindowMilliseconds = 5000, BucketMilliseconds = 50,
                MaximumPairs = 65536, MaximumCells = 2_000_000, MaximumProcesses = 32768,
                MaximumThreads = 131072, MaximumProcessors = 4096, MaximumNameBytes = 8 * 1024 * 1024,
                ByteCap = 256UL * 1024 * 1024
            };
            Assert.Equal(0, CreateResidency(ref configuration, out var ownerHandle));
            Assert.NotEqual(nint.Zero, ownerHandle);
            nint traceHandle = 0;
            try
            {
                fixed (char* pathPointer = path)
                    Assert.Equal(0, OpenTrace(ownerHandle, pathPointer, (uint)path.Length, 0, out traceHandle));
                Assert.NotEqual(nint.Zero, traceHandle);

                var started = Stopwatch.GetTimestamp();
                var threadStarted = ReadCurrentThreadCpuTime();
                var processStatus = ProcessTrace(traceHandle, warmup ? (ulong)WarmupSwitchLimit : 0UL);
                var threadEnded = ReadCurrentThreadCpuTime();
                var wall = Stopwatch.GetElapsedTime(started);
                Assert.Equal(0, QueryTraceStatus(traceHandle, out var trace, (uint)Marshal.SizeOf<NativeTraceStatus>()));
                Assert.Equal(0, QueryUsage(ownerHandle, out var usage, (uint)Marshal.SizeOf<NativeUsage>()));
                Assert.Equal(0, CloseTrace(traceHandle));
                traceHandle = 0;
                Assert.Equal(0, DestroyResidency(ownerHandle));
                ownerHandle = 0;

                return new NativeRun(processStatus, trace,
                    (threadEnded - threadStarted) / 10_000.0, wall.TotalMilliseconds,
                    usage, captureSeconds);
            }
            finally
            {
                if (traceHandle != 0) _ = CloseTrace(traceHandle);
                if (ownerHandle != 0) _ = DestroyResidency(ownerHandle);
            }
        });

    private static InventoryResult RunInventory(string path)
        => OnDedicatedThread(() =>
        {
            using var source = new ETWTraceEventSource(path);
            var parser = new KernelTraceEventParser(source);
            var state = new InventoryAccumulator();
            var started = Stopwatch.GetTimestamp();
            var stopReason = "eof";

            source.AllEvents += data =>
            {
                state.ObserveRaw(data);
                if (state.TotalRecords >= MaximumInventoryRecords)
                {
                    stopReason = "record-limit";
                    source.StopProcessing();
                }
                else if ((state.TotalRecords & 4095) == 0
                    && Stopwatch.GetElapsedTime(started) >= MaximumInventoryDuration)
                {
                    stopReason = "wall-limit";
                    source.StopProcessing();
                }
            };

            parser.DiskIORead += data => state.RecordDisk(InputFamily.DiskRead, data, data.ProcessID,
                unchecked((uint)data.TransferSize), data.DiskNumber);
            parser.DiskIOWrite += data => state.RecordDisk(InputFamily.DiskWrite, data, data.ProcessID,
                unchecked((uint)data.TransferSize), data.DiskNumber);
            parser.TcpIpSend += data => state.RecordNetwork(InputFamily.TcpSendV4, data, data.ProcessID,
                unchecked((uint)data.size), data.daddr);
            parser.TcpIpRecv += data => state.RecordNetwork(InputFamily.TcpReceiveV4, data, data.ProcessID,
                unchecked((uint)data.size), data.saddr);
            parser.TcpIpSendIPV6 += data => state.RecordNetwork(InputFamily.TcpSendV6, data, data.ProcessID,
                unchecked((uint)data.size), data.daddr);
            parser.TcpIpRecvIPV6 += data => state.RecordNetwork(InputFamily.TcpReceiveV6, data, data.ProcessID,
                unchecked((uint)data.size), data.saddr);
            parser.UdpIpSend += data => state.RecordNetwork(InputFamily.UdpSendV4, data, data.ProcessID,
                unchecked((uint)data.size), data.daddr);
            parser.UdpIpRecv += data => state.RecordNetwork(InputFamily.UdpReceiveV4, data, data.ProcessID,
                unchecked((uint)data.size), data.saddr);
            parser.UdpIpSendIPV6 += data => state.RecordNetwork(InputFamily.UdpSendV6, data, data.ProcessID,
                unchecked((uint)data.size), data.daddr);
            parser.UdpIpRecvIPV6 += data => state.RecordNetwork(InputFamily.UdpReceiveV6, data, data.ProcessID,
                unchecked((uint)data.size), data.saddr);
            parser.FileIOCreate += data => state.Record(InputFamily.FileCreate, data, data.ProcessID, null);
            parser.FileIOFileCreate += data => state.Record(InputFamily.FileNameCreate, data, data.ProcessID, null);
            parser.FileIOFileDelete += data => state.Record(InputFamily.FileNameDelete, data, data.ProcessID, null);
            parser.FileIOWrite += data => state.Record(InputFamily.FileWrite, data, data.ProcessID, null);
            parser.FileIORename += data => state.Record(InputFamily.FileRename, data, data.ProcessID, null);
            parser.FileIODelete += data => state.Record(InputFamily.FileDelete, data, data.ProcessID, null);
            parser.SysConfigVolumeMapping += data => state.Record(InputFamily.VolumeMapping, data, data.ProcessID, null);
            parser.SysConfigSystemPaths += data => state.Record(InputFamily.SystemPaths, data, data.ProcessID, null);

            source.Process();
            var elapsed = Stopwatch.GetElapsedTime(started);
            return state.ToResult(checked((uint)source.EventsLost), elapsed.TotalMilliseconds, stopReason);
        });

    private void WriteSample(string name, InventoryResult result, int index)
    {
        var addressLength = result.SampleAddressLengths[index];
        var addressBytes = addressLength == 0 ? "none"
            : Convert.ToHexString(result.SampleAddresses.AsSpan(index * 16, addressLength));
        var shape = result.SampleShapes[index];
        var inputFields = index switch
        {
            (int)InputFamily.DiskRead or (int)InputFamily.DiskWrite =>
                $"qpc={result.SampleQpc[index]}; processId={result.SampleProcessIds[index]}; "
                    + $"bytes={result.SampleTransferBytes[index]}; diskNumber={result.SampleDiskNumbers[index]}",
            <= (int)InputFamily.UdpReceiveV6 =>
                $"qpc={result.SampleQpc[index]}; processId={result.SampleProcessIds[index]}; "
                    + $"bytes={result.SampleTransferBytes[index]}; remoteAddressFamilyMarker={addressLength}; "
                    + $"remoteAddressBytes={addressBytes} (network byte order)",
            _ => $"traceEventProcessId={result.SampleProcessIds[index]}"
        };
        output.WriteLine($"sample family={name}; taskGuid={shape.TaskGuid:D}; opcode={shape.Opcode}; "
            + $"version={shape.Version}; pointerSize={shape.PointerSize}; payloadBytes={result.SamplePayloadBytes[index]}; "
            + $"headerTid={result.SampleHeaderTids[index]}; {inputFields}.");
    }

    private static string FormatFamilies(uint mask)
    {
        var names = new List<string>(4);
        for (var index = 0; index < (int)InputFamily.Count; index++)
            if ((mask & (1u << index)) != 0) names.Add(FamilyNames[index]);
        return names.Count == 0 ? "none" : string.Join(",", names);
    }

    private static InputMetadata ReadInputMetadata(string path)
    {
        using var source = new ETWTraceEventSource(path);
        var raw = KernelEtwZigInterruptEquivalenceTests.ReadRawClock(path);
        var frequency = ReadQpcFrequency(source);
        Assert.Equal(frequency, raw.Frequency);
        Assert.True(raw.HeaderQpc > 0);
        return new InputMetadata(frequency, checked((uint)source.NumberOfProcessors),
            checked((uint)source.EventsLost), raw.HeaderQpc);
    }

    private static long ReadQpcFrequency(TraceEventSource source)
    {
        var property = typeof(TraceEventSource).GetProperty("QPCFreq",
            BindingFlags.Instance | BindingFlags.NonPublic);
        var frequency = Assert.IsType<long>(property?.GetValue(source));
        Assert.True(frequency > 0, "TraceEvent did not expose a positive ETL QPC frequency.");
        return frequency;
    }

    private static string ResolveReplayPath()
    {
        var configured = Environment.GetEnvironmentVariable("RESOURCE_MANAGER_ETW_REPLAY_PATH");
        if (!string.IsNullOrWhiteSpace(configured)) return Path.GetFullPath(configured);
        var root = FindRepositoryRoot(Directory.GetCurrentDirectory())
            ?? FindRepositoryRoot(AppContext.BaseDirectory)
            ?? throw new DirectoryNotFoundException("Could not find the Resource Manager worktree root.");
        return Path.Combine(root, ".local", "etw-capture", "kernel-cpu-interrupt.etl");
    }

    private static string? FindRepositoryRoot(string start)
    {
        var current = new DirectoryInfo(start);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, ".git"))
                || Directory.Exists(Path.Combine(current.FullName, ".git")))
                return current.FullName;
            current = current.Parent;
        }
        return null;
    }

    private static T OnDedicatedThread<T>(Func<T> action)
    {
        T? result = default;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { result = action(); }
            catch (Exception error) { failure = error; }
        })
        {
            IsBackground = true,
            Name = "ETW replay diagnostic"
        };
        thread.Start();
        thread.Join();
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
        return result!;
    }

    private static long ReadCurrentThreadCpuTime()
    {
        if (!GetThreadTimes(GetCurrentThread(), out _, out _, out var kernel, out var user))
            throw new InvalidOperationException($"GetThreadTimes failed: {Marshal.GetLastWin32Error()}.");
        return checked(ToFileTime(kernel) + ToFileTime(user));
    }

    private static long ToFileTime(FileTime value)
        => checked((long)(((ulong)value.High << 32) | value.Low));

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentThread();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetThreadTimes(nint thread, out FileTime creation, out FileTime exit,
        out FileTime kernel, out FileTime user);

    [DllImport(Library, EntryPoint = "rm_kernel_etw_create", CallingConvention = CallingConvention.Cdecl)]
    private static extern int CreateResidency(ref ResidencyConfiguration config, out nint owner);

    [DllImport(Library, EntryPoint = "rm_kernel_etw_destroy", CallingConvention = CallingConvention.Cdecl)]
    private static extern int DestroyResidency(nint owner);

    [DllImport(Library, EntryPoint = "rm_kernel_etw_query_usage", CallingConvention = CallingConvention.Cdecl)]
    private static extern int QueryUsage(nint owner, out NativeUsage usage, uint size);

    [DllImport(Library, EntryPoint = "rm_kernel_etw_trace_open", CallingConvention = CallingConvention.Cdecl)]
    private static extern unsafe int OpenTrace(nint owner, char* path, uint length, uint realtime, out nint trace);

    [DllImport(Library, EntryPoint = "rm_kernel_etw_trace_process", CallingConvention = CallingConvention.Cdecl)]
    private static extern int ProcessTrace(nint trace, ulong switchLimit);

    [DllImport(Library, EntryPoint = "rm_kernel_etw_trace_status", CallingConvention = CallingConvention.Cdecl)]
    private static extern int QueryTraceStatus(nint trace, out NativeTraceStatus status, uint size);

    [DllImport(Library, EntryPoint = "rm_kernel_etw_trace_close", CallingConvention = CallingConvention.Cdecl)]
    private static extern int CloseTrace(nint trace);

    [DllImport(Library, EntryPoint = "rm_kernel_etw_interrupt_create", CallingConvention = CallingConvention.Cdecl)]
    private static extern int CreateInterruptOwner(ref NativeInterruptConfiguration config, out nint owner);

    [DllImport(Library, EntryPoint = "rm_kernel_etw_interrupt_destroy", CallingConvention = CallingConvention.Cdecl)]
    private static extern void DestroyInterruptOwner(nint owner);

    [DllImport(Library, EntryPoint = "rm_kernel_etw_interrupt_query_usage", CallingConvention = CallingConvention.Cdecl)]
    private static extern int QueryInterruptUsage(nint owner, out NativeInterruptUsage usage, uint size);

    [DllImport(Library, EntryPoint = "rm_kernel_etw_trace_bind_interrupts", CallingConvention = CallingConvention.Cdecl)]
    private static extern int BindInterrupts(nint trace, nint interruptOwner, long qpcOrigin, long tickOrigin, uint replayClock);

    [DllImport(Library, EntryPoint = "rm_kernel_etw_trace_clock", CallingConvention = CallingConvention.Cdecl)]
    private static extern int QueryNativeTraceClock(nint trace, out NativeTraceClock clock, uint size);

    private sealed record InputMetadata(long QpcFrequency, uint ProcessorCount, uint InitialEventsLost,
        long SessionStartQpc);

    private sealed record ManagedRun(long RawRecords, long Switches, uint EventsLost,
        double WallMilliseconds, double ThreadCpuMilliseconds, CpuResidencyAggregationDiagnostics Owner,
        double CaptureSeconds, string StopReason)
    {
        public double CpuNanosecondsPerRawEvent => RawRecords == 0 ? 0
            : ThreadCpuMilliseconds * 1_000_000 / RawRecords;
        public double RecordsPerCaptureSecond => CaptureSeconds <= 0 ? 0 : RawRecords / CaptureSeconds;
        public double CpuPercentAtRecordedRate => CaptureSeconds <= 0 ? 0
            : ThreadCpuMilliseconds / 1000 / CaptureSeconds * 100;
    }

    private sealed record NativeRun(int ProcessStatus, NativeTraceStatus Trace,
        double ThreadCpuMilliseconds, double WallMilliseconds, NativeUsage Owner, double CaptureSeconds)
    {
        public double CpuNanosecondsPerRawEvent => Trace.Records == 0 ? 0
            : ThreadCpuMilliseconds * 1_000_000 / Trace.Records;
        public double RecordsPerCaptureSecond => CaptureSeconds <= 0 ? 0 : Trace.Records / CaptureSeconds;
        public double CpuPercentAtRecordedRate => CaptureSeconds <= 0 ? 0
            : ThreadCpuMilliseconds / 1000 / CaptureSeconds * 100;
    }

    private sealed record ManagedCombinedRun(long RawRecords, long Switches, long Images,
        long[] InterruptCallbacks, long ValidInterrupts, uint EventsLost, long QpcFrequency,
        uint ProcessorCount, long SessionStartQpc, double WallMilliseconds,
        double ThreadCpuMilliseconds, CpuResidencyAggregationDiagnostics Residency,
        double CaptureSeconds, string StopReason)
    {
        public double CpuNanosecondsPerRawEvent => RawRecords == 0 ? 0
            : ThreadCpuMilliseconds * 1_000_000 / RawRecords;
        public double RecordsPerCaptureSecond => CaptureSeconds <= 0 ? 0 : RawRecords / CaptureSeconds;
        public double CpuPercentAtRecordedRate => CaptureSeconds <= 0 ? 0
            : ThreadCpuMilliseconds / 1000 / CaptureSeconds * 100;
    }

    private sealed record NativeCombinedRun(int ProcessStatus, NativeTraceStatus Trace,
        NativeTraceClock Clock, NativeUsage ResidencyUsage, NativeInterruptUsage InterruptUsage,
        long EventTickOriginQpc, double ThreadCpuMilliseconds, double WallMilliseconds, double CaptureSeconds)
    {
        public double CpuNanosecondsPerRawEvent => Trace.Records == 0 ? 0
            : ThreadCpuMilliseconds * 1_000_000 / Trace.Records;
        public double RecordsPerCaptureSecond => CaptureSeconds <= 0 ? 0 : Trace.Records / CaptureSeconds;
        public double CpuPercentAtRecordedRate => CaptureSeconds <= 0 ? 0
            : ThreadCpuMilliseconds / 1000 / CaptureSeconds * 100;
    }

    private enum InputFamily
    {
        DiskRead, DiskWrite,
        TcpSendV4, TcpReceiveV4, TcpSendV6, TcpReceiveV6,
        UdpSendV4, UdpReceiveV4, UdpSendV6, UdpReceiveV6,
        FileCreate, FileNameCreate, FileNameDelete, FileWrite, FileRename, FileDelete,
        VolumeMapping, SystemPaths, Count
    }

    private static readonly string[] FamilyNames =
    [
        "DiskIORead", "DiskIOWrite",
        "TcpIpSend", "TcpIpRecv", "TcpIpSendIPV6", "TcpIpRecvIPV6",
        "UdpIpSend", "UdpIpRecv", "UdpIpSendIPV6", "UdpIpRecvIPV6",
        "FileIOCreate", "FileIOFileCreate", "FileIOFileDelete", "FileIOWrite", "FileIORename", "FileIODelete",
        "SysConfigVolumeMapping", "SystemPaths"
    ];

    private static readonly string[] DigestNames =
    [
        "diskRead", "diskWrite", "tcp4Send", "tcp4Recv", "tcp6Send", "tcp6Recv",
        "udp4Send", "udp4Recv", "udp6Send", "udp6Recv"
    ];

    private readonly record struct EventShape(Guid TaskGuid, byte Opcode, byte Version, byte PointerSize);

    /// <summary>Mirrors the existing managed interrupt map/window owners without publishing snapshots.</summary>
    private sealed class ManagedInterruptOwner(long startedTick, ulong eventsLost)
    {
        private readonly OracleKernelModuleAddressMap modules = new();
        private readonly OracleSystemInterruptWindowAggregator aggregator = new();
        private long currentStartedTick = startedTick;
        private ulong currentEventsLost = eventsLost;

        public void Image(ulong address, int size, string? path, bool unload, long tick)
        {
            if (tick < currentStartedTick || !OracleKernelModuleAddressMap.IsKernelAddress(address)) return;
            if (unload) modules.Remove(address);
            else modules.Upsert(address, size, path);
        }

        public bool Observe(uint kind, double duration, ulong routine, long tick)
        {
            if (!double.IsFinite(duration) || duration <= 0 || tick < currentStartedTick) return false;
            var module = modules.Resolve(routine);
            aggregator.Observe(new OracleSystemInterruptEventSample(tick - currentStartedTick,
                Kind(kind), duration, routine, module.Name, module.Path));
            return true;
        }

        public void Reset(long tick, ulong lost)
        {
            if (lost == currentEventsLost) return;
            currentStartedTick = tick;
            currentEventsLost = lost;
            modules.Clear();
            aggregator.Reset();
        }

        private static string Kind(uint kind) => kind switch
        {
            1 => SystemInterruptEventKinds.Isr,
            2 => SystemInterruptEventKinds.Dpc,
            3 => SystemInterruptEventKinds.ThreadedDpc,
            4 => SystemInterruptEventKinds.TimerDpc,
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
    }

    private struct ShapeStats
    {
        public long Records;
        public int MinimumPayloadBytes;
        public int MaximumPayloadBytes;
        public uint FamilyMask;
    }

    private sealed class InventoryAccumulator
    {
        private readonly Dictionary<EventShape, ShapeStats> shapes = new(MaximumInventoryShapes);
        private readonly long[] familyCounts = new long[(int)InputFamily.Count];
        private readonly ulong[] digests = Enumerable.Repeat(FnvOffsetBasis, (int)InputFamily.Count).ToArray();
        private readonly bool[] hasSample = new bool[(int)InputFamily.Count];
        private readonly EventShape[] sampleShapes = new EventShape[(int)InputFamily.Count];
        private readonly int[] samplePayloadBytes = new int[(int)InputFamily.Count];
        private readonly int[] sampleHeaderTids = new int[(int)InputFamily.Count];
        private readonly int[] sampleProcessIds = new int[(int)InputFamily.Count];
        private readonly long[] sampleQpc = new long[(int)InputFamily.Count];
        private readonly uint[] sampleTransferBytes = new uint[(int)InputFamily.Count];
        private readonly int[] sampleDiskNumbers = new int[(int)InputFamily.Count];
        private readonly byte[] sampleAddressLengths = new byte[(int)InputFamily.Count];
        private readonly byte[] sampleAddresses = new byte[(int)InputFamily.Count * 16];

        public long TotalRecords { get; private set; }
        public long ShapeOverflowRecords { get; private set; }
        public long MissingNetworkAddressRecords { get; private set; }

        public bool Record(InputFamily family, TraceEvent data, int processId, IPAddress? remoteAddress)
        {
            var index = (int)family;
            familyCounts[index]++;
            var key = new EventShape(data.TaskGuid, (byte)data.Opcode, checked((byte)data.Version), checked((byte)data.PointerSize));
            RecordShape(family, data);

            var networkFamily = index >= (int)InputFamily.TcpSendV4 && index <= (int)InputFamily.UdpReceiveV6;
            var replaceSample = !hasSample[index]
                || (networkFamily && sampleAddressLengths[index] == 0 && remoteAddress is not null);
            if (!replaceSample) return false;
            hasSample[index] = true;
            sampleShapes[index] = key;
            samplePayloadBytes[index] = data.EventDataLength;
            sampleHeaderTids[index] = data.ThreadID;
            sampleProcessIds[index] = processId;
            if (remoteAddress is not null
                && remoteAddress.TryWriteBytes(sampleAddresses.AsSpan(index * 16, 16), out var addressLength))
                sampleAddressLengths[index] = checked((byte)addressLength);
            else
                sampleAddressLengths[index] = 0;
            return true;
        }

        public void RecordDisk(InputFamily family, TraceEvent data, int processId,
            uint transferBytes, int diskNumber)
        {
            var index = (int)family;
            if (Record(family, data, processId, null))
            {
                sampleQpc[index] = data.TimeStampQPC;
                sampleTransferBytes[index] = transferBytes;
                sampleDiskNumbers[index] = diskNumber;
            }
            var hash = AddUInt64(digests[(int)family], unchecked((ulong)data.TimeStampQPC));
            hash = AddUInt32(hash, processId == -1 ? uint.MaxValue : unchecked((uint)processId));
            hash = AddUInt32(hash, transferBytes);
            digests[(int)family] = AddUInt32(hash, diskNumber < 0 ? uint.MaxValue : (uint)diskNumber);
        }

        public void RecordNetwork(InputFamily family, TraceEvent data, int processId,
            uint transferBytes, IPAddress? remoteAddress)
        {
            var index = (int)family;
            if (Record(family, data, processId, remoteAddress))
            {
                sampleQpc[index] = data.TimeStampQPC;
                sampleTransferBytes[index] = transferBytes;
            }
            var hash = AddUInt64(digests[(int)family], unchecked((ulong)data.TimeStampQPC));
            hash = AddUInt32(hash, processId == -1 ? uint.MaxValue : unchecked((uint)processId));
            hash = AddUInt32(hash, transferBytes);
            Span<byte> address = stackalloc byte[16];
            if (remoteAddress is null || !remoteAddress.TryWriteBytes(address, out var length)
                || length is not (4 or 16))
            {
                MissingNetworkAddressRecords++;
                digests[(int)family] = AddUInt32(hash, 0);
                return;
            }
            hash = AddUInt32(hash, (uint)length);
            digests[(int)family] = AddBytes(hash, address[..length]);
        }

        private void RecordShape(InputFamily family, TraceEvent data)
        {
            var index = (int)family;
            var key = new EventShape(data.TaskGuid, (byte)data.Opcode, checked((byte)data.Version), checked((byte)data.PointerSize));
            if (!shapes.TryGetValue(key, out var stats))
            {
                if (shapes.Count >= MaximumInventoryShapes)
                {
                    ShapeOverflowRecords++;
                    return;
                }
                stats = new ShapeStats
                {
                    Records = 0,
                    MinimumPayloadBytes = data.EventDataLength,
                    MaximumPayloadBytes = data.EventDataLength,
                    FamilyMask = 0
                };
            }
            stats.Records++;
            stats.MinimumPayloadBytes = Math.Min(stats.MinimumPayloadBytes, data.EventDataLength);
            stats.MaximumPayloadBytes = Math.Max(stats.MaximumPayloadBytes, data.EventDataLength);
            stats.FamilyMask |= 1u << index;
            shapes[key] = stats;
        }

        public void ObserveRaw(TraceEvent data)
        {
            TotalRecords++;
        }

        public InventoryResult ToResult(uint eventsLost, double wallMilliseconds, string stopReason)
            => new(TotalRecords, eventsLost, wallMilliseconds, stopReason, stopReason == "eof",
                shapes, ShapeOverflowRecords, familyCounts, hasSample, sampleShapes, samplePayloadBytes,
                sampleHeaderTids, sampleProcessIds, sampleQpc, sampleTransferBytes, sampleDiskNumbers,
                sampleAddressLengths, sampleAddresses, digests,
                MissingNetworkAddressRecords);

        private static ulong AddUInt64(ulong hash, ulong value)
        {
            for (var index = 0; index < 8; index++) hash = AddByte(hash, (byte)(value >> (index * 8)));
            return hash;
        }

        private static ulong AddUInt32(ulong hash, uint value)
        {
            for (var index = 0; index < 4; index++) hash = AddByte(hash, (byte)(value >> (index * 8)));
            return hash;
        }

        private static ulong AddBytes(ulong hash, ReadOnlySpan<byte> bytes)
        {
            foreach (var value in bytes) hash = AddByte(hash, value);
            return hash;
        }

        private static ulong AddByte(ulong hash, byte value)
        {
            unchecked { return (hash ^ value) * FnvPrime; }
        }
    }

    private sealed record InventoryResult(long TotalRecords, uint EventsLost, double WallMilliseconds,
        string StopReason, bool Complete, IReadOnlyDictionary<EventShape, ShapeStats> Shapes,
        long ShapeOverflowRecords, long[] FamilyCounts, bool[] HasSample, EventShape[] SampleShapes,
        int[] SamplePayloadBytes, int[] SampleHeaderTids, int[] SampleProcessIds,
        long[] SampleQpc, uint[] SampleTransferBytes, int[] SampleDiskNumbers,
        byte[] SampleAddressLengths, byte[] SampleAddresses, ulong[] Digests, long MissingNetworkAddressRecords);

    [StructLayout(LayoutKind.Sequential)]
    private struct ResidencyConfiguration
    {
        public uint Size, Version;
        public long Frequency;
        public uint WindowMilliseconds, BucketMilliseconds, MaximumPairs, MaximumCells,
            MaximumProcesses, MaximumThreads, MaximumProcessors, MaximumNameBytes;
        public ulong ByteCap;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeTraceStatus
    {
        public uint WindowsStatus;
        public int OwnerStatus;
        public ulong Records, Decoded, Rejected, Unhandled, Switches;
        public uint EventsLost, BuffersLost;
        public long QpcFrequency;
        public uint ProcessorCount, OsBufferSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeUsage
    {
        public ulong CurrentBytes, PeakBytes, ByteCap, FailedAllocations;
        public int LastError;
        public uint TraceError;
        public ulong EventsLost;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeInterruptConfiguration
    {
        public uint Size, Version, MaximumModules, MaximumPathUnits, MaximumIdentities,
            MaximumSlices, MaximumSliceDrivers, Reserved;
        public ulong ByteCap, Generation;
        public long StartedTick;
        public ulong EventsLost;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeInterruptUsage
    {
        public ulong Current, Peak, Cap, FailedAllocations;
        public int LastError;
        public uint TraceError;
        public ulong EventsLost;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeTraceClock
    {
        public long Frequency, HeaderQpc, QpcBefore, QpcAfter, Filetime;
        public uint Flags, Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint Low;
        public uint High;
    }
}

#pragma warning restore CS0618
