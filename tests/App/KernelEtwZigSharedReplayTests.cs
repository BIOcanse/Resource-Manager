using Resource_Manager_APP.Tests.Oracles.KernelEtw;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using ResourceManager.App.Domain.SystemHealth;
using ResourceManager.App.Infrastructure.Adaptation;
using ResourceManager.App.Infrastructure.CpuTopology;
using ResourceManager.App.Infrastructure.SystemHealth.Interrupts;
using ResourceManager.App.Infrastructure.Telemetry.Etw;

#pragma warning disable CS0618 // Recorded raw QPC is the independent replay clock.

namespace Resource_Manager_APP.Tests;

/// <summary>Opt-in, finite-file evidence for the composed production owner. Timing is diagnostic only.</summary>
public sealed class KernelEtwZigSharedReplayTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private const string Library = "ResourceManager.NativeCore";
    private const long StartedTick = 1000;
    private const int Rounds = 2;
    private static readonly TimeSpan ReplayTimeout = TimeSpan.FromSeconds(120);
    private static readonly DateTimeOffset SnapshotTime = new(2026, 10, 27, 0, 0, 0, TimeSpan.Zero);

    [EnvironmentVariableFact("RESOURCE_MANAGER_ETW_SHARED_REPLAY", "1")]
    public void ComposedProductionOwnerMatchesRecordedCpuAndInterruptViewsAndReportsReplayCost()
    {
        var path = Environment.GetEnvironmentVariable("RESOURCE_MANAGER_ETW_REPLAY_PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            var root = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (root is not null && !File.Exists(Path.Combine(root.FullName, ".git"))
                && !Directory.Exists(Path.Combine(root.FullName, ".git"))) root = root.Parent;
            Assert.NotNull(root);
            path = Path.Combine(root.FullName, ".local", "etw-capture", "kernel-cpu-interrupt.etl");
        }
        Assert.True(File.Exists(path), $"Finite replay input is absent: {path}");
        // ProcessTrace streams this finite file; the limit is input scope, not a loaded byte array.
        Assert.InRange(new FileInfo(path).Length, 1, 2L * 1024 * 1024 * 1024);
        var raw = KernelEtwZigInterruptEquivalenceTests.ReadRawClock(path);
        using var metadata = new ETWTraceEventSource(path);
        var frequency = Assert.IsType<long>(typeof(TraceEventSource).GetProperty("QPCFreq",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(metadata));
        Assert.Equal(raw.Frequency, frequency);
        Assert.True(raw.HeaderQpc > 0);
        var processors = Enumerable.Range(0, metadata.NumberOfProcessors).ToArray();
        var initialLost = checked((uint)metadata.EventsLost);
        var plan = HostManagerTestPlanFactory.CreatePlan();
        var config = KernelEtwNativeSettings.CreateConfiguration(plan.KernelEtwLimits!, plan.CpuCoreResidency,
            checked((uint)processors.Length));
        config.Residency.QpcFrequency = frequency;
        config.StartedTickMilliseconds = StartedTick;
        config.Interrupts.EventsLost = initialLost;
        output.WriteLine($"input={path}; bytes={new FileInfo(path).Length}; rawRecords={raw.Records}; "
            + $"rawHeaderQpc={raw.HeaderQpc}; frequency={frequency}; processors={processors.Length}; initialLost={initialLost}.");
        output.WriteLine("scope=shared_owner + metadata/history/namespace + CPU/IRQ + disk/network/FileIO routing + buffer publication; "
            + "nativeRuntime=production CreateRecorded + ledger region + consumer observer; managed=retained CPU/IRQ event owners; "
            + "managed publication=excluded; final managed view copy=excluded; timing=diagnostic-only; no timing pass/fail threshold.");
        output.WriteLine("CPU sampling=1 Hz recorded IO ThroughTick, coalesced at actual buffer publications; first complete and EOF views included; "
            + "no wall-clock refresh. Native consumer timing includes callback status/IO/CPU reads, sample requests and domain row projection; "
            + "projection wall time is descriptive and is not subtracted from thread CPU. Exact managed raw-prefix oracle scans are untimed.");
        output.WriteLine("identity=recorded PID/TID/unique-key/name stream; historical creation FILETIME unavailable on this capture, "
            + "so both CPU oracles use null birth keys; live OpenProcess identity and seeded-birth behavior are outside this replay evidence.");

        var warm = RunManaged(path, frequency, raw.HeaderQpc, processors, initialLost);
        var prefixOracles = new Dictionary<ulong, ManagedPrefix>();
        _ = RunNative(path, config, processors, raw, warm, prefixOracles);
        output.WriteLine($"warmup=complete finite file for both implementations; records={warm.Records}.");
        var managed = new ManagedRun[Rounds];
        var native = new NativeRun[Rounds];
        for (var round = 0; round < Rounds; ++round)
        {
            if ((round & 1) == 0)
            {
                managed[round] = RunManaged(path, frequency, raw.HeaderQpc, processors, initialLost);
                native[round] = RunNative(path, config, processors, raw, managed[round], prefixOracles);
            }
            else
            {
                native[round] = RunNative(path, config, processors, raw, managed[0], prefixOracles);
                managed[round] = RunManaged(path, frequency, raw.HeaderQpc, processors, initialLost);
                AssertResidency(managed[0].Residency, managed[round].Residency);
            }
            var m = managed[round];
            var n = native[round];
            output.WriteLine($"round={round + 1}; order={((round & 1) == 0 ? "managed,native" : "native,managed")}; "
                + $"managedRecords={m.Records}; nativeRecords={n.Status.Records}; switches={n.Status.Switches}; "
                + $"decoded={n.Status.Decoded}; rejected={n.Status.Rejected}; unhandled={n.Status.Unhandled}; "
                + $"lost={n.Status.EventsLost}; buffersLost={n.Status.BuffersLost}; "
                + $"managedThreadCpuMs={m.CpuMilliseconds:F3}; nativeThreadCpuMs={n.CpuMilliseconds:F3}; "
                + $"managedWallMs={m.WallMilliseconds:F3}; nativeWallMs={n.WallMilliseconds:F3}; "
                + $"managedCpuNsPerRawRecord={m.CpuMilliseconds * 1_000_000 / m.Records:F1}; "
                + $"nativeCpuNsPerRawRecord={n.CpuMilliseconds * 1_000_000 / m.Records:F1}; "
                + $"captureSeconds={m.CaptureSeconds:F3}; recordedRawRate={m.Records / m.CaptureSeconds:F1}; "
                + $"nativeOneCorePercentAtRecordedRate={n.CpuMilliseconds / 10 / m.CaptureSeconds:F3}; "
                + $"regionCurrent={n.Usage.Occupied}; regionPeak={n.Usage.PeakOccupied}; regionBackingBytes={n.Usage.Reserved}; "
                + $"failedAllocations={n.Usage.Failed}; publications={n.Publications}; cpuRows={m.Residency.Records.Count}; "
                + $"cpuWindows={n.CpuWindows}; sampleCalls={n.SampleCalls}; distinctWantedTickets={n.WantedTickets}; coalescedSeconds={n.CoalescedSeconds}; "
                + $"cpuWarmingWindows={n.WarmingWindows}; cpuCompleteWindows={n.CompleteWindows}; "
                + $"callbackProjectionWallMs={n.ProjectionWallMilliseconds:F3}; callbackProjectedRows={n.ProjectedRows}; "
                + $"ioCallbacks={m.IoRecords}; finalDiskRows={n.DiskRows}; finalNetworkRows={n.NetworkRows}.");
        }
        var managedMean = managed.Average(m => m.CpuMilliseconds * 1_000_000 / m.Records);
        var nativeMean = native.Average(n => n.CpuMilliseconds * 1_000_000 / n.Status.Records);
        output.WriteLine($"meanCpuNsPerRawRecord managed={managedMean:F1}; sharedNative={nativeMean:F1}; ratio={nativeMean / managedMean:F3}.");
        if (Environment.GetEnvironmentVariable("RESOURCE_MANAGER_ETW_SHARED_PROFILE") == "1")
        {
            output.WriteLine("publication profile=one additional native pass; excluded from measured round means; phase QPC is wall time, including preemption.");
            var profiled = RunNative(path, config, processors, raw, managed[0], prefixOracles, profile: true);
            output.WriteLine($"publication profile pass totals: threadCpuMs={profiled.CpuMilliseconds:F3}; wallMs={profiled.WallMilliseconds:F3}; "
                + $"records={profiled.Status.Records}; publications={profiled.Publications}; includedInMeasuredMean=false.");
        }
        output.WriteLine("Uncertainty: two rounds; Windows GetThreadTimes granularity, scheduler/background load, file cache and JIT affect timings. "
            + "Recorded-rate and prior-thread projections are estimates, not installed tray measurements. "
            + "This managed baseline does less work: native includes shared metadata, IO routing and publication. "
            + "IO callback count zero means no recorded IO equivalence claim; nonzero IO is inventoried here but requires the separate IO oracle.");
    }

    private static ManagedRun RunManaged(string path, long frequency, long headerQpc, int[] processors, uint initialLost)
        => OnDedicatedThread(() =>
        {
            using var source = new ETWTraceEventSource(path);
            var residency = new OracleCpuResidencyAggregationBuffer(frequency, 5000, 65536, 50);
            var interrupts = new OracleSystemInterruptWindowAggregator();
            var modules = new OracleKernelModuleAddressMap();
            var parser = new KernelTraceEventParser(source);
            var dispatcher = new OracleKernelEtwSubscriptionDispatcher();
            dispatcher.AttachParser(parser);
            long records = 0, switches = 0, first = long.MaxValue, last = 0, ioRecords = 0;
            long irqStarted = StartedTick;
            var lost = initialLost;
            if (lost != 0) residency.ResetAfterEventLoss();
            long Tick(long qpc) => checked(StartedTick + (long)(((Int128)qpc - headerQpc) * 1000 / frequency));
            void Image(ulong address, int size, string? path, bool unload, long qpc)
            {
                if (Tick(qpc) < irqStarted) return;
                if (unload) modules.Remove(address); else modules.Upsert(address, size, path);
            }
            void Interrupt(string kind, double duration, ulong routine, long qpc)
            {
                var tick = Tick(qpc);
                if (!double.IsFinite(duration) || duration <= 0 || tick < irqStarted) return;
                var module = modules.Resolve(routine);
                interrupts.Observe(new(tick - irqStarted, kind, duration, routine, module.Name, module.Path));
            }
            dispatcher.InitializeSubscriptions([new(1, "shared-replay-managed-oracle", KernelTraceEventParser.Keywords.All,
                (events, _) =>
                {
                    OracleCpuResidencyEventAdapter.BindKernelEventCallbacks(events, residency, static () => true, static _ => null);
                    events.ThreadCSwitch += _ => switches++;
                    events.ImageDCStart += d => Image(d.ImageBase, d.ImageSize, d.FileName, false, d.TimeStampQPC);
                    events.ImageLoad += d => Image(d.ImageBase, d.ImageSize, d.FileName, false, d.TimeStampQPC);
                    events.ImageUnload += d => Image(d.ImageBase, d.ImageSize, d.FileName, true, d.TimeStampQPC);
                    events.PerfInfoISR += d => Interrupt(SystemInterruptEventKinds.Isr, d.ElapsedTimeMSec, d.Routine, d.TimeStampQPC);
                    events.PerfInfoDPC += d => Interrupt(SystemInterruptEventKinds.Dpc, d.ElapsedTimeMSec, d.Routine, d.TimeStampQPC);
                    events.PerfInfoThreadedDPC += d => Interrupt(SystemInterruptEventKinds.ThreadedDpc, d.ElapsedTimeMSec, d.Routine, d.TimeStampQPC);
                    events.PerfInfoTimerDPC += d => Interrupt(SystemInterruptEventKinds.TimerDpc, d.ElapsedTimeMSec, d.Routine, d.TimeStampQPC);
                })], new(7, DateTimeOffset.UnixEpoch));
            parser.DiskIORead += _ => ioRecords++;
            parser.DiskIOWrite += _ => ioRecords++;
            parser.TcpIpSend += _ => ioRecords++;
            parser.TcpIpRecv += _ => ioRecords++;
            parser.TcpIpSendIPV6 += _ => ioRecords++;
            parser.TcpIpRecvIPV6 += _ => ioRecords++;
            parser.UdpIpSend += _ => ioRecords++;
            parser.UdpIpRecv += _ => ioRecords++;
            parser.UdpIpSendIPV6 += _ => ioRecords++;
            parser.UdpIpRecvIPV6 += _ => ioRecords++;
            parser.FileIOCreate += _ => ioRecords++;
            parser.FileIOFileCreate += _ => ioRecords++;
            parser.FileIOFileDelete += _ => ioRecords++;
            parser.FileIOWrite += _ => ioRecords++;
            parser.FileIORename += _ => ioRecords++;
            parser.FileIODelete += _ => ioRecords++;
            source.AllEvents += d =>
            {
                records++;
                first = Math.Min(first, d.TimeStampQPC);
                last = Math.Max(last, d.TimeStampQPC);
                var current = checked((uint)source.EventsLost);
                if (current == lost) return;
                lost = current;
                irqStarted = Tick(d.TimeStampQPC);
                residency.ResetAfterEventLoss(); modules.Clear(); interrupts.Reset();
            };
            var start = Stopwatch.GetTimestamp();
            var cpuStart = ThreadCpu();
            source.Process();
            var cpu = (ThreadCpu() - cpuStart) / 10_000.0;
            var wall = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            return new ManagedRun(records, switches, lost, cpu, wall, (last - first) / (double)frequency,
                residency.Snapshot(processors), interrupts, irqStarted, ioRecords, Tick(last), processors.Length);
        });

    private static Dictionary<ulong, ManagedPrefix> ReadManagedPrefixes(string path, long frequency, int[] processors,
        ManagedRun eofOracle, HashSet<ulong> requestedCuts)
        => OnDedicatedThread(() =>
        {
            Assert.InRange(requestedCuts.Count, 1, 1024);
            using var source = new ETWTraceEventSource(path);
            var residency = new OracleCpuResidencyAggregationBuffer(frequency, 5000, 65536, 50);
            var parser = new KernelTraceEventParser(source);
            var dispatcher = new OracleKernelEtwSubscriptionDispatcher();
            dispatcher.AttachParser(parser);
            ulong records = 0, switches = 0;
            var lost = checked((uint)source.EventsLost);
            Assert.Equal(eofOracle.Lost, lost);
            if (lost != 0) residency.ResetAfterEventLoss();
            dispatcher.InitializeSubscriptions([new(1, "shared-replay-bulk-prefix-oracle", KernelTraceEventParser.Keywords.All,
                (events, _) =>
                {
                    OracleCpuResidencyEventAdapter.BindKernelEventCallbacks(events, residency, static () => true, static _ => null);
                    events.ThreadCSwitch += _ => switches++;
                })], new(7, DateTimeOffset.UnixEpoch));
            var prefixes = new Dictionary<ulong, ManagedPrefix>();
            long projectedRows = 0, nameCharacters = 0;
            void Capture()
            {
                var snapshot = residency.Snapshot(processors);
                projectedRows = checked(projectedRows + snapshot.Records.Count);
                nameCharacters = checked(nameCharacters + snapshot.Records.Sum(static row => (long)row.ProcessName.Length));
                Assert.True(projectedRows <= 2_000_000, "Bulk managed prefix rows exceeded their explicit bound.");
                Assert.True(nameCharacters <= 64_000_000, "Bulk managed prefix names exceeded their explicit bound.");
                prefixes.Add(records, new(records, switches, lost, snapshot));
            }
            if (requestedCuts.Contains(0)) Capture();
            // This retained parser delivers the registered typed CPU callbacks for
            // the current record before AllEvents. Exact raw/switch counts and the
            // full snapshot comparisons verify every requested boundary; no tolerance.
            source.AllEvents += _ =>
            {
                records++;
                var current = checked((uint)source.EventsLost);
                if (current != lost) { lost = current; residency.ResetAfterEventLoss(); }
                if (requestedCuts.Contains(records)) Capture();
            };
            source.Process();
            Assert.Equal(checked((ulong)eofOracle.Records), records);
            Assert.Equal(checked((ulong)eofOracle.Switches), switches);
            Assert.Equal(eofOracle.Lost, lost);
            Assert.Equal(requestedCuts.Count, prefixes.Count);
            if (prefixes.TryGetValue(records, out var final)) AssertResidency(eofOracle.Residency, final.Residency);
            return prefixes;
        });

    private NativeRun RunNative(string path, NativeKernelEtwSessionConfig config, int[] processors,
        KernelEtwZigInterruptEquivalenceTests.RawClock raw, ManagedRun oracle, Dictionary<ulong, ManagedPrefix> prefixOracles, bool profile = false)
    {
        using var ledger = new ResourceManagerLedgerOwner();
        var observer = new TimingObserver();
        var factory = new NativeKernelEtwSessionRuntimeFactory(new NativeKernelEtwLedgerRegionProvider(ledger), () => config, observer: observer);
        NativeKernelEtwSession? session = null;
        using var runtime = factory.CreateRecorded(path, new(7, DateTimeOffset.UnixEpoch),
            [new(1, "shared-replay-native", KernelTraceEventParser.Keywords.All, (source, _) => session = source.NativeSession)
                { NativeOutputs = KernelEtwNativeOutputs.Residency | KernelEtwNativeOutputs.Interrupts | KernelEtwNativeOutputs.Disk
                    | KernelEtwNativeOutputs.Network | KernelEtwNativeOutputs.File | KernelEtwNativeOutputs.Renderer }]);
        Assert.NotNull(session);
        if (profile)
            using (var profilingOwner = session.AcquireReadLease()) Assert.Equal(0, EnableProfile(profilingOwner.OwnerHandle, 1));
        KernelEtwNativeReaders.ConfigureProcessors(session, processors);
        using (var lease = session.AcquireReadLease()) Assert.Equal(0, SetReplayClock(lease.OwnerHandle, raw.HeaderQpc, StartedTick));
        Assert.Equal(96, Marshal.SizeOf<PublishedProgress>());
        var publications = 0;
        var cadence = new RecordedCpuCadence(session, raw.Records, initialLoss: oracle.Lost);
        using var publicationSignal = session.ObservePublications(() =>
        {
            Interlocked.Increment(ref publications);
            cadence.OnPublication();
        });
        using var complete = new ManualResetEventSlim();
        Exception? failure = null;
        runtime.StartProcessing(error => { failure = error; complete.Set(); });
        Assert.True(complete.Wait(ReplayTimeout), "The finite production replay did not finish within its bound.");
        Assert.True(observer.ConsumerThread?.Join(ReplayTimeout) == true, "The observed native flow did not join after completion.");
        if (failure is not null)
        {
            using var failedOwner = session.AcquireReadLease();
            WriteFailureDiagnostics(failedOwner.OwnerHandle);
            output.WriteLine("shared failure allocator=" + JsonSerializer.Serialize(session.AllocationUsage));
            output.WriteLine($"shared failure Ledger workspace count={ledger.ReadWorkspaceCapacities().Count}; table={ledger.ReadTable()}.");
        }
        Assert.Null(failure);
        Assert.Null(observer.Failure);
        Assert.Null(session.PublicationCallbackFailure);
        cadence.AssertFinished();
        NativeRun result;
        using (var owner = session.AcquireReadLease())
        {
            Assert.Equal(0, Status(owner.OwnerHandle, out var status, (uint)Marshal.SizeOf<TraceStatus>()));
            output.WriteLine($"native shared status windows={status.WindowsStatus}; owner={status.OwnerStatus}; records={status.Records}; "
                + $"decoded={status.Decoded}; rejected={status.Rejected}; unhandled={status.Unhandled}; switches={status.Switches}; lost={status.EventsLost}.");
            Assert.Equal(0u, status.WindowsStatus); Assert.Equal(0, status.OwnerStatus);
            Assert.Equal(raw.Records, status.Records); Assert.Equal((ulong)oracle.Records, status.Records);
            Assert.Equal((ulong)oracle.Switches, status.Switches); Assert.Equal(0UL, status.Rejected);
            Assert.Equal(oracle.Lost, status.EventsLost); Assert.Equal(0u, status.BuffersLost);
            Assert.Equal(raw.Frequency, status.Frequency); Assert.Equal((uint)processors.Length, status.ProcessorCount);
            Assert.Equal(0, Clock(owner.OwnerHandle, out var clock, (uint)Marshal.SizeOf<TraceClock>()));
            Assert.Equal(raw.HeaderQpc, clock.HeaderQpc); Assert.Equal(raw.Frequency, clock.Frequency); Assert.Equal(2u, clock.Flags & 2u);
            ulong sequence = 0;
            var cpu = KernelEtwNativeReaders.ReadResidency(session, ref sequence, checked((int)oracle.Lost), 0, null,
                view => new(view.Frequency, view.StartQpc, view.ThroughQpc,
                    (view.Flags & 2) != 0 ? new DateTimeOffset(view.TimeTicks, TimeSpan.Zero) : null,
                    (view.Flags & 1) != 0, new KernelEtwNativeReaders.BorrowedResidencyRecords(view.Rows, view.Count).ToArray()));
            Assert.NotNull(cpu); AssertResidency(oracle.Residency, cpu);
            sequence = 0;
            Assert.True(KernelEtwNativeReaders.ReadInterrupts(session, ref sequence, view => CompareInterrupts(oracle, view), out var irqAvailable, out _));
            Assert.True(irqAvailable, "The finite input must yield a complete IRQ window.");
            sequence = 0;
            Assert.True(KernelEtwNativeIoReaders.Read(session, ref sequence, v => (v.ProcessDiskCount, v.NetworkCount), out var io, out var ioFailure));
            Assert.Equal(0, ioFailure);
            if (oracle.IoRecords == 0) { Assert.Equal(0u, io.ProcessDiskCount); Assert.Equal(0u, io.NetworkCount); }
            Assert.Equal(0, Usage(owner.OwnerHandle, out var usage, (uint)Marshal.SizeOf<RegionUsage>()));
            Assert.Equal(0UL, usage.Failed); Assert.InRange(usage.Occupied, 1UL, config.ByteCap);
            Assert.InRange(usage.PeakOccupied, usage.Occupied, config.ByteCap);
            Assert.True(publications > 0);
            // One bulk prefix scan and comparisons happen after the observed consumer has joined;
            // none of this oracle work contributes to either timed implementation.
            var missing = cadence.Cuts.Select(static cut => cut.Status.Records).Where(cut => !prefixOracles.ContainsKey(cut)).ToHashSet();
            if (missing.Count != 0)
            {
                Assert.True(prefixOracles.Count + missing.Count <= 1024, "Managed prefix cache exceeded its explicit evidence bound.");
                var prefixes = ReadManagedPrefixes(path, raw.Frequency, processors, oracle, missing);
                foreach (var pair in prefixes) prefixOracles.Add(pair.Key, pair.Value);
                Assert.True(prefixOracles.Values.Sum(static prefix => (long)prefix.Residency.Records.Count) <= 2_000_000,
                    "Managed prefix row evidence exceeded its explicit bound.");
                Assert.True(prefixOracles.Values.Sum(static prefix => prefix.Residency.Records.Sum(static row => (long)row.ProcessName.Length)) <= 64_000_000,
                    "Managed prefix name evidence exceeded its explicit bound.");
                output.WriteLine($"managed CPU prefix oracle=one untimed full scan; newCuts={missing.Count}; cachedCuts={prefixOracles.Count}; includedInMeasuredMean=false.");
            }
            foreach (var cut in cadence.Cuts)
            {
                var prefix = prefixOracles[cut.Status.Records];
                Assert.Equal(cut.Status.Records, checked((ulong)prefix.Records));
                Assert.Equal(cut.Status.Switches, checked((ulong)prefix.Switches));
                Assert.Equal(prefix.Lost, cut.Status.EventsLost);
                Assert.Equal(0UL, cut.Status.Rejected); Assert.Equal(0u, cut.Status.BuffersLost);
                AssertResidency(prefix.Residency, cut.Snapshot, requireComplete: false);
                output.WriteLine($"cadenced CPU cut sequence={cut.Sequence}; bankSequence={cut.BankSequence}; rawRecords={cut.Status.Records}; switches={cut.Status.Switches}; "
                    + $"throughTick={cut.ThroughTick}; appliedTicket={cut.AppliedTicket}; complete={cut.Snapshot.IsComplete}; "
                    + $"windowFromQpc={cut.Snapshot.WindowStartQpc}; windowThroughQpc={cut.Snapshot.MeasuredThroughQpc}; rows={cut.Snapshot.Records.Count}; exactOracle=true.");
            }
            if (profile)
            {
                Assert.Equal(344, Marshal.SizeOf<PublicationProfile>());
                Assert.Equal(0, QueryProfile(owner.OwnerHandle, out var publicationProfile, (uint)Marshal.SizeOf<PublicationProfile>()));
                WritePublicationProfile(publicationProfile);
                Assert.Equal(192, Marshal.SizeOf<CpuPublicationDetail>());
                Assert.Equal(0, QueryCpuPublicationDetail(owner.OwnerHandle, out var detail, (uint)Marshal.SizeOf<CpuPublicationDetail>()));
                WriteCpuPublicationDetail(detail);
                var ownerUsage = session.ReadOwnerUsage();
                Assert.NotNull(ownerUsage); Assert.Equal(7, ownerUsage.Count);
                output.WriteLine("profile successful owner usage: Shared includes child owners; these rows must not be summed.");
                foreach (var row in ownerUsage)
                    output.WriteLine($"profile owner={row.Kind}; current={row.CurrentBytes}; peak={row.PeakBytes}; cap={row.ByteCap}; "
                        + $"failedAllocations={row.FailedAllocations}; capacityFailures={row.CapacityFailures}.");
                output.WriteLine($"profile region reserved={usage.Reserved}; usable={usage.Usable}; occupied={usage.Occupied}; peakOccupied={usage.PeakOccupied}; "
                    + $"requested={usage.Requested}; peakRequested={usage.PeakRequested}; liveAllocations={usage.Live}; failedAllocations={usage.Failed}.");
            }
            // An actual closed shared bank must not satisfy a later consumer reset
            // before that consumer's exact staged command has been acknowledged.
            var pendingTicket = KernelEtwNativeReaders.ResetContinuity(session);
            var previousSequence = sequence;
            var projectedOldBank = false;
            Assert.Null(KernelEtwNativeReaders.ReadResidency(session, ref sequence, checked((int)oracle.Lost), pendingTicket, cpu,
                _ => { projectedOldBank = true; return cpu!; }));
            Assert.False(projectedOldBank);
            Assert.Equal(previousSequence, sequence);
            result = new(status, observer.CpuMilliseconds, observer.WallMilliseconds, usage, publications, io.ProcessDiskCount, io.NetworkCount,
                cadence.Cuts.Count, cadence.SampleCalls, cadence.WantedTickets.Count, cadence.CoalescedSeconds, cadence.ProjectionWallMilliseconds, cadence.ProjectedRows,
                cadence.Cuts.Count(static cut => !cut.Snapshot.IsComplete), cadence.Cuts.Count(static cut => cut.Snapshot.IsComplete));
        }
        publicationSignal.Dispose();
        Assert.True(runtime.Stop(TimeSpan.FromSeconds(5)).IsReleased);
        Assert.Equal(0UL, ledger.ReadTable().RegisteredBytes);
        return result;
    }

    private void WritePublicationProfile(PublicationProfile value)
    {
        string[] phases = ["bank_select", "bank_reset", "io", "notifications", "residency", "interrupts", "diagnostics", "renderer", "scratch_reset", "install", "signal"];
        Assert.Equal(1u, value.Enabled); Assert.Equal(11u, value.PhaseCount);
        Assert.True(value.QpcFrequency > 0);
        output.WriteLine($"profile enabled={value.Enabled}; frequency={value.QpcFrequency}; publications={value.Publications}; clockFailures={value.ClockFailures}; "
            + $"cpuRows={value.CpuRows}; cpuPairsBound={value.CpuPairsBound}; cpuRetainedCells={value.CpuRetainedCells}; "
            + $"irqIdentities={value.IrqIdentities}; irqCells={value.IrqCells}; notifications={value.Notifications}; pathUnits={value.PathUnits}.");
        for (var i = 0; i < phases.Length; ++i)
            output.WriteLine($"profile phase={phases[i]}; calls={value.Phases[i].Calls}; wallQpcTicks={value.Phases[i].WallQpcTicks}; "
                + $"wallMs={value.Phases[i].WallQpcTicks * 1000.0 / value.QpcFrequency:F3}.");
        var r = value.Region;
        output.WriteLine($"profile region lowAllocCalls={r.LowAllocCalls}; highAllocCalls={r.HighAllocCalls}; lowSearchSteps={r.LowSearchSteps}; "
            + $"highSearchSteps={r.HighSearchSteps}; freeCalls={r.FreeCalls}; freeInsertSteps={r.FreeInsertSteps}; resizeCalls={r.ResizeCalls}; "
            + $"resizeSuccesses={r.ResizeSuccesses}; lowRequestedBytes={r.LowRequestedBytes}; highRequestedBytes={r.HighRequestedBytes}.");
    }

    private void WriteCpuPublicationDetail(CpuPublicationDetail value)
    {
        Assert.Equal(1u, value.Enabled); Assert.True(value.QpcFrequency > 0);
        output.WriteLine($"profile CPU detail enabled={value.Enabled}; frequency={value.QpcFrequency}; clockReads={value.ClockReads}; clockFailures={value.ClockFailures}.");
        output.WriteLine($"profile CPU native_snapshot calls={value.NativeSnapshot.Calls}; wallQpcTicks={value.NativeSnapshot.WallQpcTicks}; wallMs={value.NativeSnapshot.WallQpcTicks * 1000.0 / value.QpcFrequency:F3}.");
        output.WriteLine($"profile CPU ABI_conversion calls={value.AbiConversion.Calls}; wallQpcTicks={value.AbiConversion.WallQpcTicks}; wallMs={value.AbiConversion.WallQpcTicks * 1000.0 / value.QpcFrequency:F3}.");
        WriteRetention("event", value.EventRetention);
        WriteRetention("snapshot", value.SnapshotRetention);
        void WriteRetention(string lane, RetentionProfile r)
            => output.WriteLine($"profile CPU retention={lane}; calls={r.Calls}; wallQpcTicks={r.WallQpcTicks}; wallMs={r.WallQpcTicks * 1000.0 / value.QpcFrequency:F3}; "
                + $"pairEntryVisits={r.PairEntryVisits}; cellEntryVisits={r.CellEntryVisits}; pairsRemoved={r.PairsRemoved}; cellsRemoved={r.CellsRemoved}; "
                + $"pairRestarts={r.PairRestarts}; cellRestarts={r.CellRestarts}.");
    }

    private unsafe void WriteFailureDiagnostics(nint owner)
    {
        var statusResult = Status(owner, out var status, (uint)Marshal.SizeOf<TraceStatus>());
        output.WriteLine($"shared failure trace query={statusResult}; windows={status.WindowsStatus}; owner={status.OwnerStatus}; "
            + $"records={status.Records}; decoded={status.Decoded}; rejected={status.Rejected}; unhandled={status.Unhandled}; "
            + $"switches={status.Switches}; eventsLost={status.EventsLost}; buffersLost={status.BuffersLost}.");
        var detailResult = FailureDetailQuery(owner, out var detail, (uint)Marshal.SizeOf<FailureDetail>());
        output.WriteLine($"shared first cause query={detailResult}; result={detail.Result}; "
            + $"error={Marshal.PtrToStringUTF8(detail.ErrorName, checked((int)detail.NameLength))}; records={detail.Records}; qpc={detail.Qpc}.");
        OwnerUsage* rows = stackalloc OwnerUsage[7];
        var ownersResult = OwnerUsageQuery(owner, rows, 7, out var count);
        output.WriteLine($"shared failure owner usage query={ownersResult}; count={count}.");
        if (ownersResult == 0)
            for (var i = 0; i < Math.Min(count, 7u); ++i)
                output.WriteLine($"shared failure owner kind={rows[i].Kind}; flags={rows[i].Flags}; current={rows[i].Current}; "
                    + $"peak={rows[i].Peak}; cap={rows[i].Cap}; failedAllocations={rows[i].Failures}; capacityFailures={rows[i].CapacityFailures}.");
        var regionResult = Usage(owner, out var region, (uint)Marshal.SizeOf<RegionUsage>());
        output.WriteLine($"shared failure region query={regionResult}; reserved={region.Reserved}; usable={region.Usable}; "
            + $"occupied={region.Occupied}; peakOccupied={region.PeakOccupied}; requested={region.Requested}; "
            + $"peakRequested={region.PeakRequested}; liveAllocations={region.Live}; failedAllocations={region.Failed}.");
        var allocationResult = FailureAllocationQuery(owner, out var allocation, (uint)Marshal.SizeOf<FailureAllocation>());
        output.WriteLine($"shared failed allocation query={allocationResult}; stage={allocation.PublicationStage}; reserved={allocation.Reserved}; "
            + $"sequence={allocation.Sequence}; requestBytes={allocation.RequestBytes}; alignment={allocation.Alignment}; "
            + $"largestFreePayload={allocation.LargestFreePayload}; totalFreeSpanBytes={allocation.TotalFreeSpanBytes}; "
            + $"occupiedBytes={allocation.OccupiedBytes}; requestedBytes={allocation.RequestedBytes}.");
    }

    private static void AssertResidency(CpuResidencyAggregationSnapshot expected, CpuResidencyAggregationSnapshot actual, bool requireComplete = true)
    {
        Assert.Equal(expected.QpcFrequency, actual.QpcFrequency);
        Assert.Equal(expected.WindowStartQpc, actual.WindowStartQpc);
        Assert.Equal(expected.MeasuredThroughQpc, actual.MeasuredThroughQpc);
        Assert.Equal(expected.MeasuredThrough, actual.MeasuredThrough);
        Assert.Equal(expected.IsComplete, actual.IsComplete);
        if (requireComplete) { Assert.True(actual.IsComplete); Assert.NotEmpty(actual.Records); }
        static CpuExecutionTimeAggregate[] Sort(IReadOnlyList<CpuExecutionTimeAggregate> rows) => rows
            .OrderBy(r => r.LogicalProcessorId).ThenBy(r => r.ProcessInstanceId).ThenBy(r => r.ThreadInstanceId).ToArray();
        Assert.Equal(Sort(expected.Records), Sort(actual.Records));
    }

    private static bool CompareInterrupts(ManagedRun oracle, KernelEtwNativeReaders.InterruptView actual)
    {
        var now = SnapshotTime.AddMilliseconds(oracle.ThroughTick);
        var expected = oracle.Interrupts.CreateSnapshot(now, oracle.ThroughTick - oracle.InterruptStarted, 7, 0,
            oracle.ProcessorCount, new("etw-system-interrupts", SystemInterruptProviderStates.Running, "", checked((int)oracle.Lost)));
        Assert.Equal(expected.Available, (actual.Flags & 1) != 0);
        Assert.Equal(oracle.ThroughTick, actual.EndedTick);
        Assert.Equal(oracle.ThroughTick - checked((long)expected.Window.TotalMilliseconds), actual.StartedTick);
        Assert.Equal((ulong)expected.SourceGeneration, actual.Generation); Assert.Equal((ulong)oracle.Lost, actual.EventsLost);
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
            var row = Marshal.PtrToStructure<KernelEtwNativeReaders.InterruptBucket>(actual.Buckets + i * Marshal.SizeOf<KernelEtwNativeReaders.InterruptBucket>());
            var e = expected.Buckets[i];
            Assert.Equal(e.StartedAt, SnapshotTime.AddMilliseconds(row.StartedTick));
            Assert.Equal(e.EndedAt, SnapshotTime.AddMilliseconds(row.EndedTick));
            Assert.Equal(e.TotalDurationMilliseconds, row.TotalDuration); Assert.Equal(e.CpuCapacityPercent, row.CapacityPercent);
            Assert.Equal((ulong)e.EventCount, row.EventCount);
        }
        Assert.Equal(expected.Drivers.Count, (int)actual.DriverCount);
        for (var i = 0; i < expected.Drivers.Count; ++i)
        {
            var row = Marshal.PtrToStructure<KernelEtwNativeReaders.InterruptDriver>(actual.Drivers + i * Marshal.SizeOf<KernelEtwNativeReaders.InterruptDriver>());
            var e = expected.Drivers[i];
            Assert.Equal(e.ModuleName, Name(row.Module)); Assert.Equal(e.ModulePath, KernelEtwNativeReaders.Utf16(row.Module.Path, row.Module.PathLength));
            Assert.Equal(e.TotalDurationMilliseconds, row.TotalDuration); Assert.Equal(e.MaximumSingleDurationMilliseconds, row.MaximumDuration);
            Assert.Equal(e.CpuCapacityPercent, row.CapacityPercent); Assert.Equal((ulong)e.EventCount, row.EventCount);
            Assert.Equal((ulong)e.EventsAtOrAboveOneMillisecond, row.LongCount);
        }
        return expected.Available;
    }
    private static string Name(KernelEtwNativeReaders.ModuleIdentity module)
        => KernelEtwNativeReaders.Utf16(module.Name, module.NameLength) ?? OracleKernelModuleAddressMap.UnknownModuleName;
    private static string? Kind(uint kind) => kind switch { 1 => SystemInterruptEventKinds.Isr, 2 => SystemInterruptEventKinds.Dpc,
        3 => SystemInterruptEventKinds.ThreadedDpc, 4 => SystemInterruptEventKinds.TimerDpc, _ => null };
    private static T OnDedicatedThread<T>(Func<T> action)
    {
        T? result = default; Exception? failure = null;
        var thread = new Thread(() => { try { result = action(); } catch (Exception e) { failure = e; } }) { IsBackground = true, Name = "ETW managed replay oracle" };
        thread.Start(); Assert.True(thread.Join(ReplayTimeout), "Managed finite replay exceeded its bound.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
        return result!;
    }
    private sealed class TimingObserver : IKernelEtwConsumerThreadObserver
    {
        internal double CpuMilliseconds, WallMilliseconds;
        internal Exception? Failure;
        internal Thread? ConsumerThread;
        public IDisposable RegisterCurrentThread(string name)
        {
            ConsumerThread = Thread.CurrentThread;
            var wall = Stopwatch.GetTimestamp(); var cpu = ThreadCpu();
            return new End(() => { CpuMilliseconds = (ThreadCpu() - cpu) / 10_000.0; WallMilliseconds = Stopwatch.GetElapsedTime(wall).TotalMilliseconds; });
        }
        public void ReportFailure(Exception error) => Failure = error;
        private sealed class End(Action action) : IDisposable { public void Dispose() => action(); }
    }
    private static long ThreadCpu()
    {
        if (!GetThreadTimes(GetCurrentThread(), out _, out _, out var kernel, out var user))
            throw new InvalidOperationException($"GetThreadTimes failed: {Marshal.GetLastWin32Error()}.");
        return checked((long)kernel + (long)user);
    }
    private sealed record ManagedRun(long Records, long Switches, uint Lost, double CpuMilliseconds, double WallMilliseconds,
        double CaptureSeconds, CpuResidencyAggregationSnapshot Residency, OracleSystemInterruptWindowAggregator Interrupts,
        long InterruptStarted, long IoRecords, long ThroughTick, int ProcessorCount);
    // This test callback is deliberately included in the observed consumer cost.
    // Only a changed CPU component sequence copies rows; ordinary publications read
    // scalar metadata, never project the same CPU rows again.
    private sealed class RecordedCpuCadence(NativeKernelEtwSession session, ulong totalRecords, uint initialLoss)
    {
        // The accepted same input has 859 buffer publications. Warming emits cheap
        // metadata versions per buffer; retain every one, with a finite 1024 bound.
        private const int MaximumWindows = 1024;
        private const long MaximumRows = 2_000_000, MaximumNameCharacters = 64_000_000;
        private ulong ioSequence, cpuSequence, lastWanted;
        private long throughTick = StartedTick, nextDue = StartedTick + 1000, nameCharacters;
        private bool failed;
        internal readonly List<CpuCut> Cuts = [];
        internal readonly HashSet<ulong> WantedTickets = [];
        internal long SampleCalls, CoalescedSeconds, ProjectedRows;
        internal double ProjectionWallMilliseconds;

        internal void OnPublication()
        {
            if (failed) return;
            try { CaptureAndRequest(); }
            catch { failed = true; throw; }
        }

        private void CaptureAndRequest()
        {
            using var owner = session.AcquireReadLease();
            // Working trace_status intentionally refuses concurrent processing.
            // These counters were copied on the native flow into this immutable
            // bank before signaling; later headers cannot relabel an older CPU view.
            Assert.Equal(0, QueryPublishedProgress(owner.OwnerHandle, out var progress, (uint)Marshal.SizeOf<PublishedProgress>()));
            var status = progress.Status;
            Assert.True(status.Records <= totalRecords);
            if (KernelEtwNativeIoReaders.Read(session, ref ioSequence, static view => view.ThroughTick, out var tick, out var ioFailure))
                throughTick = tick;
            Assert.Equal(0, ioFailure);
            Assert.Equal(progress.BankSequence, ioSequence);
            var result = AcquireResidency(owner.OwnerHandle, out var view, out var next, out var lost, out var applied, out var token);
            if (result != 3)
            {
                Assert.Equal(0, result);
                try
                {
                    Assert.Equal(progress.CpuSequence, next);
                    Assert.Equal(progress.CpuAppliedTicket, applied);
                    Assert.Equal((ulong)initialLoss, lost);
                    if (next != cpuSequence)
                    {
                        Assert.True(Cuts.Count < MaximumWindows, "Recorded CPU view schedule exceeded its explicit bound.");
                        Assert.True(ProjectedRows + view.Count <= MaximumRows, "Recorded CPU row evidence exceeded its explicit bound.");
                        var started = Stopwatch.GetTimestamp();
                        var rows = new KernelEtwNativeReaders.BorrowedResidencyRecords(view.Rows, view.Count).ToArray();
                        nameCharacters = checked(nameCharacters + rows.Sum(static row => (long)row.ProcessName.Length));
                        Assert.True(nameCharacters <= MaximumNameCharacters, "Recorded CPU name evidence exceeded its explicit bound.");
                        var snapshot = new CpuResidencyAggregationSnapshot(view.Frequency, view.StartQpc, view.ThroughQpc,
                            (view.Flags & 2) != 0 ? new DateTimeOffset(view.TimeTicks, TimeSpan.Zero) : null, (view.Flags & 1) != 0, rows);
                        ProjectionWallMilliseconds += Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                        ProjectedRows += rows.Length;
                        Cuts.Add(new(next, progress.BankSequence, applied, throughTick, status, snapshot));
                        cpuSequence = next;
                    }
                }
                finally { Assert.Equal(0, ReleaseView(owner.OwnerHandle, token)); }
            }
            // At EOF the owner already forced the latest CPU component. Staging a
            // new demand here would have no next buffer and invalidate this test.
            if (status.Records == totalRecords || throughTick < nextDue) return;
            var elapsedSlots = checked((throughTick - nextDue) / 1000 + 1);
            CoalescedSeconds += elapsedSlots - 1;
            nextDue = checked(nextDue + elapsedSlots * 1000);
            Assert.Equal(0, RequestResidency(owner.OwnerHandle, out var wanted, out var pending));
            Assert.InRange(pending, 0u, 1u);
            SampleCalls++;
            if (pending != 0) { lastWanted = Math.Max(lastWanted, wanted); WantedTickets.Add(wanted); }
        }

        internal void AssertFinished()
        {
            Assert.False(failed); Assert.NotEmpty(Cuts);
            Assert.Equal(totalRecords, Cuts[^1].Status.Records);
            Assert.True(Cuts[^1].AppliedTicket >= lastWanted, "EOF did not acknowledge the last wanted CPU sample.");
            // The API is a read when the final forced CPU revision is clean. If it
            // unexpectedly stages work, fail; never drain/refresh it to hide this.
            using var owner = session.AcquireReadLease();
            Assert.Equal(0, RequestResidency(owner.OwnerHandle, out var finalTicket, out var pending));
            Assert.Equal(0u, pending);
            Assert.True(finalTicket >= lastWanted);
            Assert.Equal(Cuts[^1].AppliedTicket, finalTicket);
        }
    }
    private sealed record CpuCut(ulong Sequence, ulong BankSequence, ulong AppliedTicket, long ThroughTick, TraceStatus Status, CpuResidencyAggregationSnapshot Snapshot);
    private sealed record NativeRun(TraceStatus Status, double CpuMilliseconds, double WallMilliseconds, RegionUsage Usage, int Publications,
        uint DiskRows, uint NetworkRows, int CpuWindows, long SampleCalls, int WantedTickets, long CoalescedSeconds,
        double ProjectionWallMilliseconds, long ProjectedRows, int WarmingWindows, int CompleteWindows);
    private sealed record ManagedPrefix(ulong Records, ulong Switches, uint Lost, CpuResidencyAggregationSnapshot Residency);
    [StructLayout(LayoutKind.Sequential)] private struct TraceStatus
    {
        public uint WindowsStatus; public int OwnerStatus;
        public ulong Records, Decoded, Rejected, Unhandled, Switches;
        public uint EventsLost, BuffersLost; public long Frequency; public uint ProcessorCount, BufferSize;
    }
    [StructLayout(LayoutKind.Sequential)] private struct PublishedProgress
    { public ulong BankSequence, CpuSequence, CpuAppliedTicket; public TraceStatus Status; }
    [StructLayout(LayoutKind.Sequential)] private struct TraceClock
    { public long Frequency, HeaderQpc, Before, After, Filetime; public uint Flags, Reserved; }
    [StructLayout(LayoutKind.Sequential)] private struct RegionUsage
    { public ulong Reserved, Usable, Occupied, PeakOccupied, Requested, PeakRequested, Live, Failed; }
    [StructLayout(LayoutKind.Sequential)] private struct OwnerUsage
    { public uint Kind, Flags; public ulong Current, Peak, Cap, Failures, CapacityFailures; }
    [StructLayout(LayoutKind.Sequential)] private struct FailureDetail
    { public int Result; public uint Reserved; public nint ErrorName; public uint NameLength, Reserved2; public ulong Records; public long Qpc; }
    [StructLayout(LayoutKind.Sequential)] private struct FailureAllocation
    {
        public uint PublicationStage, Reserved;
        public ulong Sequence, RequestBytes, Alignment, LargestFreePayload, TotalFreeSpanBytes, OccupiedBytes, RequestedBytes;
    }
    [StructLayout(LayoutKind.Sequential)] private struct ProfilePhase { public ulong Calls, WallQpcTicks; }
    [StructLayout(LayoutKind.Sequential)] private struct ProfileRegion
    {
        public ulong LowAllocCalls, HighAllocCalls, LowSearchSteps, HighSearchSteps, FreeCalls, FreeInsertSteps,
            ResizeCalls, ResizeSuccesses, LowRequestedBytes, HighRequestedBytes;
    }
    [StructLayout(LayoutKind.Sequential)] private struct PublicationProfile
    {
        public uint Enabled, PhaseCount;
        public long QpcFrequency;
        public ulong Publications, ClockFailures, CpuRows, CpuPairsBound, CpuRetainedCells, IrqIdentities, IrqCells, Notifications, PathUnits;
        public ProfileRegion Region;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 11)] public ProfilePhase[] Phases;
    }
    [StructLayout(LayoutKind.Sequential)] private struct RetentionProfile
    {
        public ulong Calls, WallQpcTicks, PairEntryVisits, CellEntryVisits, PairsRemoved, CellsRemoved, PairRestarts, CellRestarts;
    }
    [StructLayout(LayoutKind.Sequential)] private struct CpuPublicationDetail
    {
        public uint Enabled, Reserved;
        public long QpcFrequency;
        public ulong ClockReads, ClockFailures;
        public ProfilePhase NativeSnapshot, AbiConversion;
        public RetentionProfile EventRetention, SnapshotRetention;
    }
    [DllImport("kernel32.dll")] private static extern nint GetCurrentThread();
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetThreadTimes(nint thread, out ulong creation, out ulong exit, out ulong kernel, out ulong user);
    [DllImport(Library, EntryPoint = "rm_kernel_etw_shared_set_replay_clock", CallingConvention = CallingConvention.Cdecl)]
    private static extern int SetReplayClock(nint owner, long qpc, long tick);
    [DllImport(Library, EntryPoint = "rm_kernel_etw_shared_status", CallingConvention = CallingConvention.Cdecl)]
    private static extern int Status(nint owner, out TraceStatus status, uint size);
    [DllImport(Library, EntryPoint = "rm_kernel_etw_shared_query_published_progress", CallingConvention = CallingConvention.Cdecl)]
    private static extern int QueryPublishedProgress(nint owner, out PublishedProgress progress, uint size);
    [DllImport(Library, EntryPoint = "rm_kernel_etw_shared_clock", CallingConvention = CallingConvention.Cdecl)]
    private static extern int Clock(nint owner, out TraceClock clock, uint size);
    [DllImport(Library, EntryPoint = "rm_kernel_etw_shared_query_region_usage", CallingConvention = CallingConvention.Cdecl)]
    private static extern int Usage(nint owner, out RegionUsage usage, uint size);
    [DllImport(Library, EntryPoint = "rm_kernel_etw_shared_query_owner_usage", CallingConvention = CallingConvention.Cdecl)]
    private static extern unsafe int OwnerUsageQuery(nint owner, OwnerUsage* rows, uint capacity, out uint count);
    [DllImport(Library, EntryPoint = "rm_kernel_etw_shared_query_failure_detail", CallingConvention = CallingConvention.Cdecl)]
    private static extern int FailureDetailQuery(nint owner, out FailureDetail detail, uint size);
    [DllImport(Library, EntryPoint = "rm_kernel_etw_shared_query_failure_allocation", CallingConvention = CallingConvention.Cdecl)]
    private static extern int FailureAllocationQuery(nint owner, out FailureAllocation allocation, uint size);
    [DllImport(Library, EntryPoint = "rm_kernel_etw_shared_profile_enable", CallingConvention = CallingConvention.Cdecl)]
    private static extern int EnableProfile(nint owner, uint enabled);
    [DllImport(Library, EntryPoint = "rm_kernel_etw_shared_profile_query", CallingConvention = CallingConvention.Cdecl)]
    private static extern int QueryProfile(nint owner, out PublicationProfile profile, uint size);
    [DllImport(Library, EntryPoint = "rm_kernel_etw_shared_profile_query_cpu_detail", CallingConvention = CallingConvention.Cdecl)]
    private static extern int QueryCpuPublicationDetail(nint owner, out CpuPublicationDetail detail, uint size);
    [DllImport(Library, EntryPoint = "rm_kernel_etw_shared_request_residency", CallingConvention = CallingConvention.Cdecl)]
    private static extern int RequestResidency(nint owner, out ulong ticket, out uint pending);
    [DllImport(Library, EntryPoint = "rm_kernel_etw_shared_acquire_residency", CallingConvention = CallingConvention.Cdecl)]
    private static extern int AcquireResidency(nint owner, out KernelEtwNativeReaders.ResidencyView view, out ulong sequence,
        out ulong eventsLost, out ulong appliedTicket, out ulong token);
    [DllImport(Library, EntryPoint = "rm_kernel_etw_shared_release", CallingConvention = CallingConvention.Cdecl)]
    private static extern int ReleaseView(nint owner, ulong token);
}

#pragma warning restore CS0618
