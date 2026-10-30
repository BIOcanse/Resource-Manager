using Resource_Manager_APP.Tests.Oracles.KernelEtw;
using System.Globalization;
using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using ResourceManager.App.Infrastructure.ResourceBreakdown;
using ResourceManager.App.Infrastructure.Telemetry.Etw;
using ResourceManager.App.Application.ResourceBreakdown;

namespace Resource_Manager_APP.Tests;

/// <summary>The real managed owners are the oracle; no second aggregation implementation lives here.</summary>
public sealed class KernelEtwZigIoEquivalenceTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Fact]
    public void PublicDiskConfigurationUsesTheAuthoritativeNondefaultNativeBucketsAndRejectsConflicts()
    {
        using var fixture = new SharedFixture(bucketMilliseconds: 73);
        var broker = new HeldBroker(fixture.Session);
        using var capture = new EtwPhysicalDiskIoCaptureOwner(broker);
        capture.Start();
        Assert.Equal(new PhysicalDiskIoCaptureConfiguration(73, 32), capture.Configuration);
        var started = Environment.TickCount64;
        var aligned = (started + 1000 + 7299) / 7300 * 7300;
        fixture.Disk(aligned + 72, 2048);
        fixture.Disk(aligned + 75, 4096);
        fixture.Publish(aligned + 2073);
        var window = capture.Read(true).ObservedTraffic!;
        Assert.True(window.Complete);
        Assert.Equal("4096", Assert.Single(window.Processes).ReadBytes);
        using var matching = new EtwPhysicalDiskIoCaptureOwner(broker, configuration: new(73, 32));
        matching.Start();
        Assert.True(matching.IsRunning);
        using var conflicting = new EtwPhysicalDiskIoCaptureOwner(broker, configuration: new(100, 32));
        conflicting.Start();
        Assert.False(conflicting.IsRunning);
        Assert.Equal(PhysicalDiskIoProviderStates.Unavailable, conflicting.State);
        Assert.Contains("100", conflicting.Message); Assert.Contains("73", conflicting.Message);
        Assert.Empty(conflicting.Read(true).Processes);
    }
    [Theory]
    [InlineData("::ffff:127.0.0.0", true)]
    [InlineData("::ffff:127.0.0.1", false)]
    [InlineData("::ffff:127.2.3.4", true)]
    [InlineData("::ffff:127.255.255.255", true)]
    [InlineData("::ffff:126.255.255.255", true)]
    [InlineData("::ffff:128.0.0.0", true)]
    [InlineData("::ffff:0.0.0.0", true)]
    [InlineData("::ffff:255.255.255.255", true)]
    [InlineData("::127.0.0.1", true)]
    public void Ipv4MappedNetworkClassificationMatchesTheDotNetOracle(string text, bool external)
    {
        var address = IPAddress.Parse(text);
        Assert.Equal(!external, IPAddress.IsLoopback(address));
        foreach (var send in new[] { false, true })
            foreach (var udp in new[] { false, true })
            {
                using var replay = new Replay();
                replay.Identities[41] = new(100);
                replay.Network(1100, 41, 4096, send, udp, address, 100);
                replay.Compare(3001); // Compares native bytes/rates/counts and external fields to the actual owner.
                var oracle = replay.ReadNetwork();
                Assert.Single(oracle.RawProcessValues);
                Assert.Equal(external, oracle.Processes.ContainsKey(41));
                Assert.Equal(1, oracle.RawProcessValues[41].ActivityEventCount);
            }
    }

    [Fact]
    public void NativeDiskJoiningAndRejoiningAnExistingOwnerWaitsForItsOwnFreshWindow()
    {
        using var fixture = new SharedFixture();
        fixture.Disk(1100, 2048); fixture.Publish(3100);
        long tick = 10000;
        var broker = new HeldBroker(fixture.Session);
        using var capture = new EtwPhysicalDiskIoCaptureOwner(broker, new(), () => tick);
        capture.Start();
        Assert.False(capture.Read(true).ObservedTraffic!.Complete); // Reject the older complete bank before reset acknowledgment.
        fixture.Publish(10000);
        Assert.False(capture.Read(true).ObservedTraffic!.Complete);
        fixture.Disk(11000, 4096); fixture.Publish(11999);
        Assert.False(capture.Read(true).ObservedTraffic!.Complete);
        fixture.Publish(12000);
        var fresh = capture.Read(true);
        Assert.True(fresh.ObservedTraffic!.Complete);
        Assert.Equal("4096", Assert.Single(fresh.ObservedTraffic.Processes).ReadBytes);
        capture.Stop(); tick = 15000; capture.Start();
        Assert.False(capture.Read(true).ObservedTraffic!.Complete);
        fixture.Publish(17000);
        Assert.True(capture.Read(true).ObservedTraffic!.Complete);
        Assert.Empty(capture.Read(true).Processes);
        Assert.Empty(capture.Read(true).ObservedTraffic!.Disks);
    }

    [Fact]
    public void NativeFatalFailureCannotReuseACompleteCachedIoBankWhileBrokerStillRuns()
    {
        using var fixture = new SharedFixture();
        var broker = new HeldBroker(fixture.Session);
        long tick = 1000;
        using var disk = new EtwPhysicalDiskIoCaptureOwner(broker, new(), () => tick);
        using var network = new EtwNetworkTcpIpCaptureOwner(broker, new(), () => tick);
        disk.Start(); network.Start(); fixture.Disk(1100, 4096); fixture.Publish(3101);
        Assert.True(disk.Read(true).ObservedTraffic!.Complete);
        Assert.NotNull(network.Read(true).ObservedTraffic);
        fixture.Fail(3);
        Assert.True(broker.GetSnapshot().IsRunning);
        var failedDisk = disk.Read(true); var failedNetwork = network.Read(true);
        Assert.Equal(PhysicalDiskIoProviderStates.Unavailable, failedDisk.ProviderState.State);
        Assert.Equal(NetworkAttributionProviderStates.Unavailable, failedNetwork.ProviderState.State);
        Assert.Contains("(3)", failedDisk.ProviderState.Message); Assert.Contains("(3)", failedNetwork.ProviderState.Message);
        Assert.Null(failedDisk.ObservedTraffic); Assert.Null(failedNetwork.ObservedTraffic);
        Assert.Empty(failedDisk.Processes); Assert.Empty(failedNetwork.RawProcessValues);
    }
    [Fact]
    public void ExactPublicationsMatchForBoundariesUnsignedSizesIdentityReuseAndLoss()
    {
        using var r = new Replay();
        r.Compare(1000); // Both real owners have incomplete start windows.
        r.Disk(1100, 41, 0, 2048, false, 100);
        r.Disk(1199, 41, 0, 4096, false, 100);
        r.Disk(1200, 41, 1, uint.MaxValue, true, 100);
        r.Disk(1300, 41, -1, 512, true, 100);
        r.Disk(1400, -1, 2, 1024, false, 100);
        r.Disk(1500, 42, 3, 2048, false, null);
        r.Network(1100, 41, uint.MaxValue, true, false, IPAddress.Parse("8.8.8.8"), 100);
        r.Network(1200, 41, 1024, false, true, IPAddress.Loopback, 100);
        r.Network(1300, 41, 2048, false, true, IPAddress.Parse("::ffff:127.2.3.4"), 100);
        r.Network(1400, 42, 512, true, false, IPAddress.IPv6Loopback, null);
        r.Network(1500, 43, 256, false, false, IPAddress.Parse("2001:db8::1"), 99); // Before creation.
        r.Compare(3000); // Inclusive disk warm boundary, exclusive network loss boundary.
        r.Compare(3001); // A partial disk edge bucket is omitted whole.
        r.Compare(3100);
        r.Compare(3199);
        r.Identities[41] = new(200);
        r.ProcessChanged(41);
        r.Disk(3300, 41, 0, 8192, true, 201);
        r.Network(3300, 41, 4096, false, false, IPAddress.Parse("192.0.2.1"), 201);
        r.Compare(3400); // Old PID lifetime contributes zero; current lifetime retained.
        r.Identities[41] = new(200, ChangeOnVerification: true);
        r.Compare(3400); // Second read from the same handle fails verification.
        r.Identities[41] = new(200);
        r.Loss(3500);
        r.Compare(3500);
        r.Disk(3600, 41, 4, 16384, false, 201);
        r.Network(3600, 41, 8192, true, true, IPAddress.Parse("2001:db8::2"), 201);
        r.Compare(5500);
        r.Compare(5501);
        r.Compare(12001); // Retention removes historical disks and event rows.
        r.Compare(12001); // Read is stable and never mutates the previous publication.
    }

    [Fact]
    public void FailedIdentityAndRecoveredIntakeNeverBackfillUnknownDiskHistory()
    {
        using var r = new Replay();
        r.Identities[41] = new(0);
        r.Disk(1100, 41, 0, 4096, false, 100);
        r.Network(1100, 41, 4096, false, false, IPAddress.Any, 100);
        r.Compare(3100);
        r.Identities[41] = new(100);
        r.Compare(3100); // Verification repairs only the future intake cache.
        r.Disk(3200, 41, 0, 8192, true, 100);
        r.Network(3200, 41, 8192, true, false, IPAddress.None, 100);
        r.Compare(5200);
    }

    [EnvironmentVariableFact("RESOURCE_MANAGER_IO_ETL_REPLAY_PATH")]
    public void FiniteRecordedIoMatchesActualCSharpOwnerPublicationsAtIdenticalWindows()
    {
        var path = Environment.GetEnvironmentVariable("RESOURCE_MANAGER_IO_ETL_REPLAY_PATH");
        Assert.True(File.Exists(path), $"Owned IO ETL is absent: {path}");
        using var source = new ETWTraceEventSource(path);
        var frequency = Assert.IsType<long>(typeof(TraceEventSource).GetProperty("QPCFreq",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.GetValue(source));
        Assert.True(frequency > 0);
        using var r = new Replay();
        var parser = new KernelTraceEventParser(source);
        long? first = null;
        long through = 1000, nextPublication = 1500, records = 0, publications = 0;
        var counts = new long[10];
        var fileCounts = new Dictionary<string, long> { ["Create64"] = 0, ["NameCreate32"] = 0, ["NameDelete35"] = 0,
            ["Read67"] = 0, ["Write68"] = 0, ["Delete70"] = 0, ["Rename71"] = 0 };
        long Tick(long qpc)
        {
            first ??= qpc;
            return Math.Max(1000, checked(1000 + (long)(((Int128)qpc - first.Value) * 1000 / frequency)));
        }
        void Advance(long qpc)
        {
            var tick = Math.Max(through, Tick(qpc)); // The arrival clock is monotonic for backdated records.
            while (nextPublication < tick)
            {
                r.Compare(nextPublication); ++publications; nextPublication += 500;
            }
            through = tick;
        }
        void Disk(int index, int pid, int number, int size, bool write, TraceEvent data)
        {
#pragma warning disable CS0618
            Advance(data.TimeStampQPC);
#pragma warning restore CS0618
            r.Disk(through, pid, number, unchecked((uint)size), write, FileTime(data.TimeStamp)); ++counts[index];
        }
        void Net(int index, int pid, int size, bool send, bool udp, IPAddress address, TraceEvent data)
        {
#pragma warning disable CS0618
            Advance(data.TimeStampQPC);
#pragma warning restore CS0618
            r.Network(through, pid, unchecked((uint)size), send, udp, address, FileTime(data.TimeStamp)); ++counts[index];
        }
        var loss = source.EventsLost;
        if (loss != 0) r.Loss(1000, loss);
        parser.DiskIORead += d => Disk(0, d.ProcessID, d.DiskNumber, d.TransferSize, false, d);
        parser.DiskIOWrite += d => Disk(1, d.ProcessID, d.DiskNumber, d.TransferSize, true, d);
        parser.TcpIpSend += d => Net(2, d.ProcessID, d.size, true, false, d.daddr, d);
        parser.TcpIpRecv += d => Net(3, d.ProcessID, d.size, false, false, d.saddr, d);
        parser.TcpIpSendIPV6 += d => Net(4, d.ProcessID, d.size, true, false, d.daddr, d);
        parser.TcpIpRecvIPV6 += d => Net(5, d.ProcessID, d.size, false, false, d.saddr, d);
        parser.UdpIpSend += d => Net(6, d.ProcessID, d.size, true, true, d.daddr, d);
        parser.UdpIpRecv += d => Net(7, d.ProcessID, d.size, false, true, d.saddr, d);
        parser.UdpIpSendIPV6 += d => Net(8, d.ProcessID, d.size, true, true, d.daddr, d);
        parser.UdpIpRecvIPV6 += d => Net(9, d.ProcessID, d.size, false, true, d.saddr, d);
        parser.ProcessStartGroup += d => r.ProcessChanged(d.ProcessID);
        parser.ProcessEndGroup += d => r.ProcessChanged(d.ProcessID);
        parser.FileIOCreate += _ => ++fileCounts["Create64"];
        parser.FileIOFileCreate += _ => ++fileCounts["NameCreate32"];
        parser.FileIOFileDelete += _ => ++fileCounts["NameDelete35"];
        parser.FileIORead += _ => ++fileCounts["Read67"];
        parser.FileIOWrite += _ => ++fileCounts["Write68"];
        parser.FileIODelete += _ => ++fileCounts["Delete70"];
        parser.FileIORename += _ => ++fileCounts["Rename71"];
        source.AllEvents += d =>
        {
#pragma warning disable CS0618
            Advance(d.TimeStampQPC);
#pragma warning restore CS0618
            ++records;
            if (source.EventsLost != loss) { loss = source.EventsLost; r.Loss(through, loss); }
        };
        source.Process(); // EOF, no event-prefix or duration cap.
        r.Compare(through); ++publications;
        output.WriteLine($"EOF records={records}; elapsedMs={through - 1000}; publications={publications}; historicalEventsLost={loss}; "
            + $"diskRead/write,tcp4Send/Recv,tcp6Send/Recv,udp4Send/Recv,udp6Send/Recv=[{string.Join(',', counts)}]; "
            + $"verifiedDiskRows={r.VerifiedDiskRows}; verifiedNetworkRows={r.VerifiedNetworkRows}.");
        output.WriteLine("FileIO coverage only (not FileIO equivalence): " + string.Join(", ", fileCounts.Select(x => $"{x.Key}={x.Value}")));
        Assert.True(counts[0] > 0 && counts[1] > 0, "Recorded disk read and write coverage is required.");
        Assert.True(counts.Skip(2).Sum() > 0, "Recorded network coverage is required.");
        Assert.True(r.VerifiedDiskRows > 0 && r.VerifiedNetworkRows > 0, "Empty/unavailable-only equality is insufficient.");
        // Coverage gaps (for example absent TCPv6) stay explicit in the log and report.
    }

    private static ulong? FileTime(DateTime value)
    {
        try { return checked((ulong)value.ToUniversalTime().ToFileTimeUtc()); }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    [EnvironmentVariableFact("RESOURCE_MANAGER_IO_ETL_REPLAY_PATH")]
    public unsafe void IndependentRawSharedConsumerMatchesActualCSharpOwnersAtTheCommonEofBoundary()
    {
        var path = Environment.GetEnvironmentVariable("RESOURCE_MANAGER_IO_ETL_REPLAY_PATH");
        Assert.True(File.Exists(path));
        using var source = new ETWTraceEventSource(path);
        var frequency = Assert.IsType<long>(typeof(TraceEventSource).GetProperty("QPCFreq",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.GetValue(source));
        using var replay = new Replay(createNative: false);
        // An explicit common origin avoids deriving a clock from either parser's
        // dispatch order. QPC remains exact; this test accepts the EOF window.
        long Tick(long qpc) => checked(1000 + (long)((Int128)qpc * 1000 / frequency));
        long through = 1000, records = 0, diskEvents = 0, networkEvents = 0;
        var lost = source.EventsLost;
        if (lost != 0) replay.Loss(1000, lost);
        var parser = new KernelTraceEventParser(source);
        void Disk(int pid, int number, int size, bool write, TraceEvent data)
        {
#pragma warning disable CS0618
            replay.Disk(Tick(data.TimeStampQPC), pid, number, unchecked((uint)size), write, FileTime(data.TimeStamp));
#pragma warning restore CS0618
            ++diskEvents;
        }
        void Net(int pid, int size, bool send, bool udp, IPAddress address, TraceEvent data)
        {
#pragma warning disable CS0618
            replay.Network(Tick(data.TimeStampQPC), pid, unchecked((uint)size), send, udp, address, FileTime(data.TimeStamp));
#pragma warning restore CS0618
            ++networkEvents;
        }
        parser.DiskIORead += d => Disk(d.ProcessID, d.DiskNumber, d.TransferSize, false, d);
        parser.DiskIOWrite += d => Disk(d.ProcessID, d.DiskNumber, d.TransferSize, true, d);
        parser.TcpIpSend += d => Net(d.ProcessID, d.size, true, false, d.daddr, d);
        parser.TcpIpRecv += d => Net(d.ProcessID, d.size, false, false, d.saddr, d);
        parser.TcpIpSendIPV6 += d => Net(d.ProcessID, d.size, true, false, d.daddr, d);
        parser.TcpIpRecvIPV6 += d => Net(d.ProcessID, d.size, false, false, d.saddr, d);
        parser.UdpIpSend += d => Net(d.ProcessID, d.size, true, true, d.daddr, d);
        parser.UdpIpRecv += d => Net(d.ProcessID, d.size, false, true, d.saddr, d);
        parser.UdpIpSendIPV6 += d => Net(d.ProcessID, d.size, true, true, d.daddr, d);
        parser.UdpIpRecvIPV6 += d => Net(d.ProcessID, d.size, false, true, d.saddr, d);
        parser.ProcessStartGroup += d => replay.ProcessChanged(d.ProcessID);
        parser.ProcessEndGroup += d => replay.ProcessChanged(d.ProcessID);
        source.AllEvents += data =>
        {
#pragma warning disable CS0618
            through = Math.Max(through, Tick(data.TimeStampQPC));
#pragma warning restore CS0618
            ++records;
            Assert.Equal(lost, source.EventsLost); // Historical fixed loss is a common explicit input.
        };
        var managedBefore = ReadThreadCost();
        source.Process();
        var managedAfter = ReadThreadCost();
        WriteThreadCost("TraceEvent plus actual C# IO intake", managedBefore, managedAfter, checked((ulong)records));
        Assert.True(diskEvents > 0 && networkEvents > 0);
        var cfg = SharedConfiguration(frequency, checked((uint)source.NumberOfProcessors));
        var memory = NativeMemory.Alloc(checked((nuint)cfg.ByteCap));
        nint wrapper = 0, owner = 0;
        try
        {
            var identity = replay.Callbacks;
            Assert.Equal(0, SharedCreate(ref cfg, memory, checked((nuint)cfg.ByteCap), ref identity, out wrapper, out owner));
            Assert.Equal(0, SharedOutputs(owner, 12)); // This comparison consumes only disk/network outputs.
            fixed (char* file = path) Assert.Equal(0, SharedOpen(owner, file, checked((uint)path!.Length), 0));
            Assert.Equal(0, SharedClock(owner, 0, 1000));
            var nativeBefore = ReadThreadCost();
            var processed = SharedProcess(owner);
            var nativeAfter = ReadThreadCost();
            Assert.Equal(0, SharedStatus(owner, out var status, checked((uint)Marshal.SizeOf<TraceStatus>())));
            WriteThreadCost("native raw shared routing plus buffer publications", nativeBefore, nativeAfter, status.Records);
            output.WriteLine("Cost scope: one existing pair, managed first/native second (cache order differs); caller-thread CPU and managed allocations only. "
                + "Setup, final explicit EOF projection/comparison and cleanup are outside both intervals. Native buffer publications are inside its interval; managed intake has no periodic Read. "
                + "Native allocation usage is reported separately. This is route-cost diagnostic evidence, not a component speedup or isolated tray-cost estimate.");
            output.WriteLine($"Raw shared EOF: result={processed}; ownerStatus={status.OwnerStatus}; Windows={status.WindowsStatus}; records={status.Records}; "
                + $"rejected={status.Rejected}; lost={status.EventsLost}; bufferLoss={status.BuffersLost}; bufferBytes={status.BufferSize}; pendingPathUnitCap={cfg.MaximumPendingPathUnits}; boundary={through}; managedRecords={records}; diskEvents={diskEvents}; networkEvents={networkEvents}.");
            Assert.Equal(0, SharedFailureDetail(owner, out var detail, checked((uint)Marshal.SizeOf<FailureDetail>())));
            output.WriteLine($"Raw shared first cause: result={detail.Result}; error={(detail.NameLength == 0 ? string.Empty : Marshal.PtrToStringUTF8(detail.ErrorName, checked((int)detail.NameLength)))}; records={detail.Records}; qpc={detail.Qpc}.");
            OwnerUsage* usage = stackalloc OwnerUsage[7];
            Assert.Equal(0, SharedUsage(owner, usage, 7, out var usageCount));
            for (var i = 0; i < usageCount; i++)
                output.WriteLine($"Raw shared owner {usage[i].Kind}: current={usage[i].Current}; peak={usage[i].Peak}; cap={usage[i].Cap}; failedAllocations={usage[i].Failures}; capacityFailures={usage[i].CapacityFailures}.");
            Assert.Equal(0, SharedIrqCounts(wrapper, out var irqCounts));
            output.WriteLine($"Raw shared IRQ counts: modules={irqCounts.Modules}/{irqCounts.ModuleCap}; identities={irqCounts.Identities}/{irqCounts.IdentityCap}; slices={irqCounts.Slices}/{irqCounts.SliceCap}; cells={irqCounts.Cells}/{irqCounts.CellCap}.");
            output.WriteLine($"Raw shared FileIO counts: entries={irqCounts.FileEntries}/{irqCounts.FileEntryCap}; versions={irqCounts.FileVersions}/{irqCounts.FileVersionCap}; internedNames={irqCounts.FileNames}.");
            output.WriteLine($"Raw shared pending counts: events={irqCounts.PendingEvents}/{irqCounts.PendingEventCap}; pathUnits={irqCounts.PendingPaths}/{irqCounts.PendingPathCap}; namespaceEntries={irqCounts.NamespaceEntries}/{irqCounts.NamespaceCap}; maximumPathUnits={irqCounts.PathUnitsCap}.");
            output.WriteLine($"Raw shared history counts: starts={irqCounts.HistoryStarts}; endKeys={irqCounts.HistoryEndKeys}; endVersions={irqCounts.HistoryEnds}; combinedEntries={irqCounts.HistoryStarts + irqCounts.HistoryEnds}/{irqCounts.HistoryCap}.");
            Assert.Equal(0, detail.Result);
            Assert.Equal(0, processed); Assert.Equal(0UL, status.Rejected); Assert.Equal((ulong)records, status.Records);
            Assert.Equal((uint)lost, status.EventsLost);
            Assert.Equal(0, SharedSnapshot(wrapper, through, DateTimeOffset.UnixEpoch.AddMilliseconds(through).ToFileTime(), out var view));
            replay.CompareView(through, view);
            Assert.True(replay.VerifiedDiskRows > 0 && replay.VerifiedNetworkRows > 0, "Raw EOF equality must include verified nonempty IO rows.");
        }
        finally
        {
            if (owner != 0) Assert.Equal(0, SharedClose(owner));
            if (wrapper != 0) Assert.Equal(0, SharedDestroy(wrapper));
            NativeMemory.Free(memory);
        }
    }

    private static NativeKernelEtwSessionConfig SharedConfiguration(long frequency, uint processors)
    {
        var config = new NativeKernelEtwSessionConfig
        {
            ByteCap = 512 * 1024 * 1024, DiskByteCap = 64 * 1024 * 1024, NetworkByteCap = 64 * 1024 * 1024,
            // Full standalone replay measured 85,551,420 bytes for 126,091
            // recorded addresses / 1,134,299 versions. Match its proven finite
            // allowance; realtime cleanup and production defaults are unchanged.
            FileByteCap = 128 * 1024 * 1024, HistoryByteCap = 64 * 1024 * 1024,
            Residency = new() { QpcFrequency = frequency, WindowMilliseconds = 5000, BucketMilliseconds = 50,
                MaximumPairs = 65536, MaximumCells = 2_000_000, MaximumProcesses = 32768, MaximumThreads = 131072,
                MaximumProcessors = processors, MaximumNameBytes = 8 * 1024 * 1024, ByteCap = 128 * 1024 * 1024 },
            Interrupts = new() { MaximumModules = 8192, MaximumPathUnits = 32768, MaximumIdentities = 32768,
                MaximumSlices = 262144, MaximumSliceDrivers = 1048576, ByteCap = 128 * 1024 * 1024 },
            MaximumDiskPairs = 8192, MaximumDisks = 8192, MaximumCachedProcesses = 8192,
            // Finite recorded histories retain closed addresses: the 32,768-entry
            // limit was reached exactly at record 5,264,884. This is the same
            // 131,072-entry bound already proven by independent full FileIO replay.
            MaximumNetworkEvents = 262144, MaximumNetworkProcesses = 65536, MaximumFileEntries = 131072,
            MaximumFileVersions = 2097152, MaximumHistoryEntries = 262144, MaximumPathUnits = 32768,
            MaximumNamespaceEntries = 512, MaximumCommands = 8192, MaximumCommandNameBytes = 1024 * 1024,
            PublicationBanks = 3, WindowMilliseconds = 2000, RetentionMilliseconds = 8000, DiskBucketMilliseconds = 100,
            // Recorded buffers are 1 MiB, and mapped/history names in Write/Info
            // notifications can exceed their raw payload footprint. The observed
            // batch exhausted 1,048,576 units at 7,159 events. Keep a finite 4M-unit
            // replay staging allowance; all region/owner byte ceilings stay fixed.
            StartedTickMilliseconds = 1000, MaximumPendingEvents = 32768, MaximumPendingPathUnits = 4 * 1024 * 1024,
            MaximumRendererProcesses = 4096
        };
        config.StampAbi(new(1, DateTimeOffset.UnixEpoch.AddMilliseconds(1000)));
        return config;
    }

    private sealed record Identity(ulong Birth, bool ChangeOnVerification = false);
    private sealed class Lease(uint pid, Identity identity) : IWindowsNetworkEndpointProcessLease
    {
        private int reads;
        public uint ProcessId => pid;
        public bool TryReadCreationFileTime(out ulong value)
        {
            value = identity.Birth + (identity.ChangeOnVerification && ++reads > 1 ? 1UL : 0UL);
            return value != 0;
        }
        public void Dispose() { }
    }
    private sealed class HeldBroker(NativeKernelEtwSession native) : IKernelEtwSessionBroker
    {
        private readonly object gate = new();
        private readonly Dictionary<int, KernelEtwSubscriptionRequest> requests = [];
        private int next;
        private int acquisitions, releases;
        private readonly TaskCompletionSource firstAcquisition = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task FirstAcquisition => firstAcquisition.Task;
        internal int Acquisitions { get { lock (gate) return acquisitions; } }
        internal int Releases { get { lock (gate) return releases; } }
        internal KernelTraceEventParser.Keywords Keywords { get { lock (gate) return requests.Values.Aggregate(KernelTraceEventParser.Keywords.None, (all, request) => all | request.Keywords); } }
        internal KernelEtwNativeOutputs Outputs { get { lock (gate) return requests.Values.Aggregate(KernelEtwNativeOutputs.None, (all, request) => all | request.NativeOutputs); } }
        public IKernelEtwSubscription Acquire(KernelEtwSubscriptionRequest request)
        {
            lock (gate)
            {
                var id = ++next; ++acquisitions; requests.Add(id, request);
                var source = new KernelEtwEventSource(native, $"{request.Id}-{id}", request.NativeOutputs, 1,
                    static (name, read, supplement) => new(name, read, supplement));
                try { request.Bind(source, new(1, DateTimeOffset.UnixEpoch)); source.Activate(); }
                catch { requests.Remove(id); source.Cancel(); throw; }
                firstAcquisition.TrySetResult();
                return new Held(this, id, source);
            }
        }
        public KernelEtwSessionSnapshot GetSnapshot()
        {
            lock (gate) return new(requests.Count == 0 ? "Idle" : "Running", "held replay", 1,
                DateTimeOffset.UnixEpoch, Keywords, requests.Count, 0);
        }
        private sealed class Held(HeldBroker owner, int id, KernelEtwEventSource source) : IKernelEtwSubscription
        {
            public KernelEtwSessionSnapshot GetSnapshot() => owner.GetSnapshot();
            public void Dispose()
            {
                lock (owner.gate)
                    if (owner.requests.Remove(id)) { source.Cancel(); ++owner.releases; }
            }
        }
    }

    internal sealed unsafe class SharedFixture : IDisposable
    {
        private readonly Replay identity = new();
        private readonly void* memory;
        private readonly nint wrapper;
        private readonly PublicationCallback publication;
        private Exception? publicationFailure;
        internal NativeKernelEtwSession Session { get; }
        internal SharedFixture(int bucketMilliseconds = 100)
        {
            var config = SharedConfiguration(1000, 1);
            config.ByteCap = 32 * 1024 * 1024;
            config.DiskBucketMilliseconds = bucketMilliseconds;
            config.DiskByteCap = config.NetworkByteCap = config.FileByteCap = config.HistoryByteCap = 4 * 1024 * 1024;
            config.MaximumDiskPairs = config.MaximumCachedProcesses = 32; config.MaximumDisks = 16;
            config.MaximumNetworkEvents = 256; config.MaximumNetworkProcesses = 64;
            config.MaximumFileEntries = 64; config.MaximumFileVersions = 256; config.MaximumHistoryEntries = 128;
            config.MaximumCommands = 32; config.MaximumCommandNameBytes = 4096; config.MaximumPathUnits = 1024;
            config.MaximumNamespaceEntries = 32; config.MaximumPendingEvents = 128; config.MaximumPendingPathUnits = 4096;
            config.MaximumRendererProcesses = 64;
            config.Residency.MaximumPairs = 32; config.Residency.MaximumCells = 1024;
            config.Residency.MaximumProcesses = 64; config.Residency.MaximumThreads = 128;
            config.Residency.MaximumNameBytes = 4096; config.Residency.ByteCap = 4 * 1024 * 1024;
            config.Interrupts.MaximumModules = 16; config.Interrupts.MaximumPathUnits = 1024; config.Interrupts.MaximumIdentities = 64;
            config.Interrupts.MaximumSlices = 128; config.Interrupts.MaximumSliceDrivers = 256; config.Interrupts.ByteCap = 4 * 1024 * 1024;
            memory = NativeMemory.Alloc(checked((nuint)config.ByteCap));
            try
            {
                var callbacks = identity.Callbacks;
                Assert.Equal(0, SharedCreate(ref config, memory, checked((nuint)config.ByteCap), ref callbacks, out wrapper, out var owner));
                // This fixture owns the real native owner. Borrow its handle without
                // creating an ETW provider/session or duplicating lifetime ownership.
                Session = new(new BorrowedRegion((nint)memory, checked((nuint)config.ByteCap)));
                Session.InitializeCounterContract(config.MaximumCommands);
                typeof(NativeKernelEtwSession).GetField("handle", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(Session, owner);
                publication = _ =>
                {
                    try
                    {
                        var signals = (Action[])typeof(NativeKernelEtwSession).GetField("publicationSignals",
                            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(Session)!;
                        foreach (var signal in signals)
                            try { signal(); }
                            catch (Exception error) { Interlocked.CompareExchange(ref publicationFailure, error, null); }
                    }
                    catch (Exception error) { Interlocked.CompareExchange(ref publicationFailure, error, null); }
                };
                Assert.Equal(0, SharedPublicationCallback(owner, Marshal.GetFunctionPointerForDelegate(publication), 0));
            }
            catch
            {
                if (wrapper != 0) Assert.Equal(0, SharedDestroy(wrapper));
                NativeMemory.Free(memory); identity.Dispose(); throw;
            }
        }
        internal void Disk(long tick, uint bytes) => Assert.Equal(0, SharedDisk(wrapper, tick, 41, bytes));
        internal void Network(long tick, uint bytes)
        {
            byte[] address = [8, 8, 8, 8];
            fixed (byte* value = address) Assert.Equal(0, SharedNetwork(wrapper, tick, 41, bytes, 1, 0, value, 4, 100));
        }
        internal void Publish(long tick)
        {
            Assert.Equal(0, SharedSnapshot(wrapper, tick, DateTimeOffset.UnixEpoch.AddMilliseconds(tick).ToFileTime(), out _));
            if (publicationFailure is { } failure) throw new InvalidOperationException("Controlled native publication callback failed.", failure);
        }
        internal void Fail(int code) => Assert.Equal(0, SharedFail(wrapper, code));
        public void Dispose()
        {
            using (var owner = Session.AcquireReadLease()) Assert.Equal(0, SharedPublicationCallback(owner.OwnerHandle, 0, 0));
            Assert.Equal(0, SharedDestroy(wrapper));
            GC.KeepAlive(publication);
            NativeMemory.Free(memory); identity.Dispose();
        }
        private sealed class BorrowedRegion(nint pointer, nuint length) : IKernelEtwRegionLease
        {
            public nint Pointer => pointer;
            public nuint Length => length;
            public void Complete() { }
            public void Dispose() { }
        }
    }

    private sealed unsafe class Replay : IDisposable
    {
        private readonly OracleReplayBroker broker = new();
        private readonly OracleKernelEtwEventSource events = new();
        private readonly KernelEtwSessionContext context = new(1, DateTimeOffset.UnixEpoch);
        private readonly OraclePhysicalDiskIoCaptureOwner disk;
        private readonly OracleNetworkTcpIpCaptureOwner network;
        private readonly OpenCallback open;
        private readonly PidCallback pid;
        private readonly BirthCallback birth;
        private readonly CloseCallback close;
        private nint native;
        private int activeNativeHandles;
        internal NativeIdentity Callbacks;
        private long tick = 1000;
        internal readonly Dictionary<uint, Identity> Identities = [];
        internal long VerifiedDiskRows, VerifiedNetworkRows;
        private DateTimeOffset UtcNow() => DateTimeOffset.UnixEpoch.AddMilliseconds(tick);
        private IWindowsNetworkEndpointProcessLease NewLease(uint process) => new Lease(process, Identities.GetValueOrDefault(process, new Identity(100)));
        internal Replay(bool createNative = true)
        {
            disk = new(broker, new(), () => tick, NewLease, utcNow: UtcNow);
            network = new(broker, new(), () => tick, NewLease, UtcNow);
            disk.Start(); network.Start();
            foreach (var request in broker.Requests) request.Bind(events, context);
            events.Activate();
            open = (_, process) => { ++activeNativeHandles; return GCHandle.ToIntPtr(GCHandle.Alloc(NewLease(process))); };
            pid = (_, handle) => ((IWindowsNetworkEndpointProcessLease)GCHandle.FromIntPtr(handle).Target!).ProcessId;
            birth = (_, handle) => ((IWindowsNetworkEndpointProcessLease)GCHandle.FromIntPtr(handle).Target!).TryReadCreationFileTime(out var value) ? value : 0;
            close = (_, handle) => { var lease = GCHandle.FromIntPtr(handle); ((IDisposable)lease.Target!).Dispose(); lease.Free(); --activeNativeHandles; };
            Callbacks = new NativeIdentity { Open = Marshal.GetFunctionPointerForDelegate(open), Pid = Marshal.GetFunctionPointerForDelegate(pid), Birth = Marshal.GetFunctionPointerForDelegate(birth), Close = Marshal.GetFunctionPointerForDelegate(close) };
            if (createNative) Assert.Equal(0, Create(ref Callbacks, tick, out native));
        }
        internal void ProcessChanged(int process)
        {
            disk.ObserveProcessIdentityChange(process);
            if (native != 0) Assert.Equal(0, ProcessChangedNative(native, unchecked((uint)process)));
        }
        internal void Loss(long at, int? cumulative = null)
        {
            tick = at; broker.Lost = cumulative ?? broker.Lost + 1;
            disk.Read(true); network.Read(true); // Actual owner loss-refresh path.
            if (native != 0) Assert.Equal(0, Invalidate(native, at));
        }
        internal void Disk(long at, int process, int number, uint bytes, bool write, ulong? eventTime)
        {
            tick = at;
            disk.ObserveBoundDiskTransfer(events, context, process, bytes, write, at, number < 0 ? null : (uint)number, eventTime);
            if (native != 0) Assert.Equal(0, DiskNative(native, at, unchecked((uint)process), number, bytes, write ? 1u : 0u, eventTime ?? 0));
        }
        internal void Network(long at, int process, uint bytes, bool send, bool udp, IPAddress address, ulong? eventTime)
        {
            tick = at;
            network.ObserveBoundNetworkTransfer(events, context, process, unchecked((int)bytes), send, udp, address, at, eventTime);
            if (native != 0)
            {
                var addr = address.GetAddressBytes();
                fixed (byte* p = addr) Assert.Equal(0, NetworkNative(native, at, unchecked((uint)process), bytes, send ? 1u : 0u, udp ? 1u : 0u, p, (uint)addr.Length, eventTime ?? 0));
            }
        }
        internal void Compare(long at)
        {
            tick = at;
            Assert.Equal(0, Snapshot(native, at, UtcNow().ToFileTime(), out var view));
            CompareView(at, view);
        }
        internal NetworkAttributionSnapshot ReadNetwork() => network.Read(true);
        internal void CompareView(long at, NativeView view)
        {
            tick = at;
            var d = disk.Read(true);
            var n = network.Read(true);
            Assert.Equal(0, activeNativeHandles); // Snapshot verification leases never outlive publication.
            Assert.Equal(UtcNow().ToFileTime(), view.CapturedAt);
            Assert.Equal(at - 2000, view.StartTick); Assert.Equal(at, view.ThroughTick); Assert.Equal(2000, view.Window);
            Assert.Equal(UtcNow(), d.CapturedAt); Assert.Equal(UtcNow(), n.CapturedAt);
            Assert.Equal("etw-fileio-disk", d.ProviderState.Id);
            Assert.Equal(view.DiskComplete != 0 ? PhysicalDiskIoProviderStates.Running : PhysicalDiskIoProviderStates.Warming, d.ProviderState.State);
            Assert.Equal("etw-network-tcpip", n.ProviderState.Id);
            Assert.Equal(view.NetworkComplete != 0 ? NetworkAttributionProviderStates.Running : NetworkAttributionProviderStates.Warming, n.ProviderState.State);
            var observedDisk = Assert.IsType<PhysicalDiskProcessIoWindow>(d.ObservedTraffic);
            Assert.Equal(d.CapturedAt, observedDisk.CapturedAt);
            Assert.Equal(view.Window, observedDisk.WindowMilliseconds);
            Assert.Equal(view.DiskComplete != 0, observedDisk.Complete);
            var scopes = new ReadOnlySpan<uint>(view.Disks, checked((int)view.DiskCount)).ToArray();
            Assert.Equal(scopes, observedDisk.Disks.Select(x => x.DiskNumber));
            Assert.All(observedDisk.Disks, x => Assert.Equal(1, x.SessionGeneration));
            var diskRows = new ReadOnlySpan<NativeDiskRow>(view.DiskRows, checked((int)view.DiskRowCount)).ToArray();
            var actualDisk = observedDisk.Processes.OrderBy(x => x.ProcessId).ThenBy(x => x.Disk.DiskNumber).ToArray();
            Assert.Equal(diskRows.Length, actualDisk.Length);
            for (var i = 0; i < diskRows.Length; i++)
            {
                var expected = diskRows[i]; var actual = actualDisk[i];
                Assert.Equal(expected.Pid, (uint)actual.ProcessId); Assert.Equal(expected.Number, actual.Disk.DiskNumber); Assert.Equal(1, actual.Disk.SessionGeneration);
                Assert.Equal(Text(expected.Available, expected.Birth), actual.CreationWindowsFileTime100Ns);
                Assert.Equal(Text(expected.Available, expected.Read), actual.ReadBytes); Assert.Equal(Text(expected.Available, expected.Write), actual.WriteBytes);
                if (expected.Available != 0) ++VerifiedDiskRows;
            }
            var processes = new ReadOnlySpan<NativeDiskRow>(view.Processes, checked((int)view.ProcessCount)).ToArray().Where(x => x.Available != 0).ToArray();
            Assert.Equal(processes.Select(x => (int)x.Pid), d.Processes.Keys.Order());
            foreach (var row in processes)
            {
                var actual = d.Processes[(int)row.Pid]; Assert.Equal((int)row.Pid, actual.ProcessId); Assert.Equal(row.Birth, actual.ProcessStartKey); Assert.Equal(row.ReadRate, actual.ReadBytesPerSecond); Assert.Equal(row.WriteRate, actual.WriteBytesPerSecond);
                Assert.Equal(row.ReadCount, (uint)actual.ReadEventCount); Assert.Equal(row.WriteCount, (uint)actual.WriteEventCount);
            }
            Assert.Equal(view.NetworkComplete != 0, n.ObservedTraffic is not null);
            var networks = new ReadOnlySpan<NativeNetworkRow>(view.Networks, checked((int)view.NetworkCount)).ToArray();
            if (n.ObservedTraffic is { } observed)
            {
                Assert.Equal(n.CapturedAt, observed.CapturedAt); Assert.Equal(view.Window, observed.WindowMilliseconds);
                Assert.Equal(networks.Length, observed.Processes.Count);
                for (var i = 0; i < networks.Length; i++)
                {
                    var expected = networks[i]; var actual = observed.Processes[i];
                    Assert.Equal(expected.Pid, (uint)actual.ProcessId); Assert.Equal(Text(expected.Available, expected.Birth), actual.CreationWindowsFileTime100Ns);
                    Assert.Equal(Text(expected.Available, expected.Receive), actual.ReceiveBytes); Assert.Equal(Text(expected.Available, expected.Send), actual.SendBytes);
                    Assert.Equal(expected.Available != 0 ? expected.ReceiveRate : (double?)null, actual.ReceiveBytesPerSecond);
                    Assert.Equal(expected.Available != 0 ? expected.SendRate : (double?)null, actual.SendBytesPerSecond);
                    if (expected.Available != 0) ++VerifiedNetworkRows;
                }
            }
            Assert.Equal(networks.Where(x => x.Available != 0).Select(x => (int)x.Pid), n.RawProcessValues.Keys.Order());
            Assert.Equal(networks.Where(x => x.Available != 0 && x.ExternalCount > 0).Select(x => (int)x.Pid), n.Processes.Keys.Order());
            foreach (var row in networks.Where(x => x.Available != 0))
            {
                var actual = n.RawProcessValues[(int)row.Pid]; Assert.Equal((int)row.Pid, actual.ProcessId); Assert.Equal(row.Birth, actual.ProcessStartKey); Assert.Equal(row.ReceiveRate, actual.ReceiveBytesPerSecond); Assert.Equal(row.SendRate, actual.SendBytesPerSecond); Assert.Equal(row.Count, (uint)actual.ActivityEventCount);
                Assert.False(actual.IsEstimated); Assert.False(actual.IsKnownProxy); Assert.Equal(0, actual.TcpConnectionCount); Assert.Equal(0, actual.UdpSocketCount);
                if (row.ExternalCount == 0) continue;
                var external = n.Processes[(int)row.Pid]; Assert.Equal((int)row.Pid, external.ProcessId); Assert.Equal(row.Birth, external.ProcessStartKey); Assert.Equal(row.ExternalReceiveRate, external.ReceiveBytesPerSecond); Assert.Equal(row.ExternalSendRate, external.SendBytesPerSecond); Assert.Equal(row.ExternalCount, (uint)external.ActivityEventCount);
                Assert.False(external.IsEstimated); Assert.False(external.IsKnownProxy); Assert.Equal(0, external.TcpConnectionCount); Assert.Equal(0, external.UdpSocketCount);
            }
        }
        private static string? Text(uint available, ulong value) => available != 0 ? value.ToString(CultureInfo.InvariantCulture) : null;
        public void Dispose()
        {
            disk.Dispose(); network.Dispose();
            if (native != 0) { Assert.Equal(0, Destroy(native)); native = 0; }
            GC.KeepAlive(open); GC.KeepAlive(pid); GC.KeepAlive(birth); GC.KeepAlive(close);
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nint OpenCallback(nint context, uint pid);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate uint PidCallback(nint context, nint handle);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate ulong BirthCallback(nint context, nint handle);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void CloseCallback(nint context, nint handle);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void PublicationCallback(nint context);
    [StructLayout(LayoutKind.Sequential)] private struct NativeIdentity { public nint Context, Open, Pid, Birth, Close; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeDiskRow
    {
        public uint Pid, Number; public ulong Birth; public uint Available, ReadCount, WriteCount, Reserved;
        public ulong Read, Write; public double ReadRate, WriteRate;
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativeNetworkRow
    {
        public uint Pid, Available; public ulong Birth, Receive, Send; public double ReceiveRate, SendRate;
        public uint Count, ExternalCount; public ulong ExternalReceive, ExternalSend; public double ExternalReceiveRate, ExternalSendRate;
    }
    [StructLayout(LayoutKind.Sequential)] private unsafe struct NativeView
    {
        public long CapturedAt, StartTick, ThroughTick, Window;
        public uint DiskComplete, NetworkComplete, ProcessCount, DiskRowCount, DiskCount, NetworkCount;
        public NativeDiskRow* Processes; public NativeDiskRow* DiskRows; public uint* Disks; public NativeNetworkRow* Networks;
    }
    [StructLayout(LayoutKind.Sequential)] private struct TraceStatus
    {
        public uint WindowsStatus; public int OwnerStatus;
        public ulong Records, Decoded, Rejected, Unhandled, Switches;
        public uint EventsLost, BuffersLost; public long Frequency; public uint ProcessorCount, BufferSize;
    }
    [StructLayout(LayoutKind.Sequential)] private struct FailureDetail
    {
        public int Result; public uint Reserved; public nint ErrorName; public uint NameLength, Reserved2; public ulong Records; public long Qpc;
    }
    [StructLayout(LayoutKind.Sequential)] private struct OwnerUsage
    {
        public uint Kind, Flags; public ulong Current, Peak, Cap, Failures, CapacityFailures;
    }
    [StructLayout(LayoutKind.Sequential)] private struct IrqCounts
    {
        public uint Modules, ModuleCap, Identities, IdentityCap, Slices, SliceCap, Cells, CellCap;
        public uint FileEntries, FileEntryCap, FileVersions, FileVersionCap, FileNames;
        public uint PendingEvents, PendingEventCap, PendingPaths, PendingPathCap;
        public uint HistoryStarts, HistoryEndKeys, HistoryEnds, HistoryCap;
        public uint NamespaceEntries, NamespaceCap, PathUnitsCap;
    }
    private readonly record struct ThreadCost(long CpuTicks, long AllocatedBytes, long Timestamp);
    private static ThreadCost ReadThreadCost()
    {
        if (!GetThreadTimes(GetCurrentThread(), out _, out _, out var kernel, out var user))
            throw new InvalidOperationException($"GetThreadTimes failed: {Marshal.GetLastWin32Error()}.");
        return new(checked(kernel + user), GC.GetAllocatedBytesForCurrentThread(), Stopwatch.GetTimestamp());
    }
    private void WriteThreadCost(string route, ThreadCost before, ThreadCost after, ulong records)
        => output.WriteLine(FormattableString.Invariant($"IO replay caller-thread cost: route={route}; records={records}; cpuMilliseconds={(after.CpuTicks - before.CpuTicks) / 10000d:F3}; wallMilliseconds={Stopwatch.GetElapsedTime(before.Timestamp, after.Timestamp).TotalMilliseconds:F3}; managedAllocatedBytes={after.AllocatedBytes - before.AllocatedBytes}."));
    [DllImport("kernel32.dll")] private static extern nint GetCurrentThread();
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetThreadTimes(nint thread, out long creation, out long exit, out long kernel, out long user);

    private const string Library = "ResourceManager.NativeCore";
    [DllImport(Library, EntryPoint = "rm_kernel_etw_io_replay_create", CallingConvention = CallingConvention.Cdecl)] private static extern int Create(ref NativeIdentity callbacks, long started, out nint handle);
    [DllImport(Library, EntryPoint = "rm_kernel_etw_io_replay_destroy", CallingConvention = CallingConvention.Cdecl)] private static extern int Destroy(nint handle);
    [DllImport(Library, EntryPoint = "rm_kernel_etw_io_replay_disk", CallingConvention = CallingConvention.Cdecl)] private static extern int DiskNative(nint handle, long tick, uint pid, int disk, uint bytes, uint write, ulong filetime);
    [DllImport(Library, EntryPoint = "rm_kernel_etw_io_replay_network", CallingConvention = CallingConvention.Cdecl)] private static extern unsafe int NetworkNative(nint handle, long tick, uint pid, uint bytes, uint send, uint udp, byte* address, uint length, ulong filetime);
    [DllImport(Library, EntryPoint = "rm_kernel_etw_io_replay_process_changed", CallingConvention = CallingConvention.Cdecl)] private static extern int ProcessChangedNative(nint handle, uint pid);
    [DllImport(Library, EntryPoint = "rm_kernel_etw_io_replay_invalidate", CallingConvention = CallingConvention.Cdecl)] private static extern int Invalidate(nint handle, long tick);
    [DllImport(Library, EntryPoint = "rm_kernel_etw_io_replay_snapshot", CallingConvention = CallingConvention.Cdecl)] private static extern int Snapshot(nint handle, long tick, long captured, out NativeView view);
    [DllImport(Library, EntryPoint = "rm_kernel_etw_io_replay_shared_create", CallingConvention = CallingConvention.Cdecl)] private static extern unsafe int SharedCreate(ref NativeKernelEtwSessionConfig config, void* memory, nuint length, ref NativeIdentity callbacks, out nint wrapper, out nint owner);
    [DllImport(Library, EntryPoint = "rm_kernel_etw_io_replay_shared_destroy", CallingConvention = CallingConvention.Cdecl)] private static extern int SharedDestroy(nint wrapper);
    [DllImport(Library, EntryPoint = "rm_kernel_etw_shared_set_publication_callback", CallingConvention = CallingConvention.Cdecl)] private static extern int SharedPublicationCallback(nint owner, nint callback, nint context);
    [DllImport(Library, EntryPoint = "rm_kernel_etw_io_replay_shared_snapshot", CallingConvention = CallingConvention.Cdecl)] private static extern int SharedSnapshot(nint wrapper, long tick, long captured, out NativeView view);
    [DllImport(Library, EntryPoint = "rm_kernel_etw_shared_open", CallingConvention = CallingConvention.Cdecl)] private static extern unsafe int SharedOpen(nint owner, char* path, uint length, uint realtime);
    [DllImport(Library, EntryPoint = "rm_kernel_etw_shared_set_replay_clock", CallingConvention = CallingConvention.Cdecl)] private static extern int SharedClock(nint owner, long qpc, long tick);
    [DllImport(Library, EntryPoint = "rm_kernel_etw_shared_set_outputs", CallingConvention = CallingConvention.Cdecl)] private static extern int SharedOutputs(nint owner, uint outputs);
    [DllImport(Library, EntryPoint = "rm_kernel_etw_shared_process", CallingConvention = CallingConvention.Cdecl)] private static extern int SharedProcess(nint owner);
    [DllImport(Library, EntryPoint = "rm_kernel_etw_shared_status", CallingConvention = CallingConvention.Cdecl)] private static extern int SharedStatus(nint owner, out TraceStatus status, uint size);
    [DllImport(Library, EntryPoint = "rm_kernel_etw_shared_close_trace", CallingConvention = CallingConvention.Cdecl)] private static extern int SharedClose(nint owner);
    [DllImport(Library, EntryPoint = "rm_kernel_etw_io_replay_shared_disk", CallingConvention = CallingConvention.Cdecl)] private static extern int SharedDisk(nint wrapper, long tick, uint pid, uint bytes);
    [DllImport(Library, EntryPoint = "rm_kernel_etw_test_shared_network", CallingConvention = CallingConvention.Cdecl)] private static extern unsafe int SharedNetwork(nint wrapper, long tick, uint pid, uint bytes, uint send, uint udp, byte* address, uint length, ulong filetime);
    [DllImport(Library, EntryPoint = "rm_kernel_etw_io_replay_shared_fail", CallingConvention = CallingConvention.Cdecl)] private static extern int SharedFail(nint wrapper, int code);
    [DllImport(Library, EntryPoint = "rm_kernel_etw_shared_query_failure_detail", CallingConvention = CallingConvention.Cdecl)] private static extern int SharedFailureDetail(nint owner, out FailureDetail detail, uint size);
    [DllImport(Library, EntryPoint = "rm_kernel_etw_shared_query_owner_usage", CallingConvention = CallingConvention.Cdecl)] private static extern unsafe int SharedUsage(nint owner, OwnerUsage* rows, uint capacity, out uint count);
    [DllImport(Library, EntryPoint = "rm_kernel_etw_io_replay_shared_irq_counts", CallingConvention = CallingConvention.Cdecl)] private static extern int SharedIrqCounts(nint wrapper, out IrqCounts counts);

    private sealed class OracleReplayBroker : IOracleKernelEtwSessionBroker
    {
        internal readonly List<OracleKernelEtwSubscriptionRequest> Requests = [];
        internal int Lost;
        public IOracleKernelEtwSubscription Acquire(OracleKernelEtwSubscriptionRequest request)
        { Requests.Add(request); return new Subscription(this); }
        public KernelEtwSessionSnapshot GetSnapshot() => new("Running", "replay", 1, DateTimeOffset.UnixEpoch, 0, Requests.Count, Lost);
        private sealed class Subscription(OracleReplayBroker owner) : IOracleKernelEtwSubscription
        {
            public KernelEtwSessionSnapshot GetSnapshot() => owner.GetSnapshot();
            public void Dispose() { }
        }
    }
}
