using Resource_Manager_APP.Tests.Oracles.KernelEtw;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using ResourceManager.App.Infrastructure.CpuTopology;
using ResourceManager.App.Infrastructure.Telemetry.Etw;

namespace Resource_Manager_APP.Tests;

/// <summary>Feeds identical normalized events to the retained C# oracle and the Zig C ABI.</summary>
public sealed class KernelEtwZigEquivalenceTests
{
    private static readonly string[] ReplayScenarios =
    [
        "duration-distribution", "unknown-and-idle", "known-thread-unknown-pid", "identity-mismatch",
        "pid-reuse", "tid-reuse", "lifecycle-before-boundary", "identity-metadata", "event-loss",
        "reset-and-clear", "capacity-rewarm", "expired-pair", "partial-buckets", "shared-boundary",
        "backward-time", "bucket-wrap", "maximum-qpc", "rounded-qpc-window"
    ];

    // Every scenario runs as a finite window (n = 0) and as a continuous one with n = 4.
    public static IEnumerable<object[]> ReplayCases()
    {
        foreach (var scenario in ReplayScenarios)
            foreach (var slots in new[] { 0, 4 }) yield return [scenario, slots];
    }

    [Theory]
    [MemberData(nameof(ReplayCases))]
    public void NormalizedReplayMatchesEverySnapshotField(string scenario, int slots)
    {
        var window = scenario switch { "tid-reuse" or "pid-reuse" => 3000, "bucket-wrap" => 8,
            "maximum-qpc" => 1, "rounded-qpc-window" => 7, _ => 1000 };
        var frequency = scenario == "rounded-qpc-window" ? 1001 : 1000;
        var capacity = scenario == "capacity-rewarm" ? 1 : scenario == "expired-pair" ? 2 : 1024;
        int[] processors = scenario == "shared-boundary" ? [0, 9] : [0];
        using var replay = new Replay(frequency, window, capacity, processors, slots, compareEveryEvent: true);
        replay.Compare();
        // A fresh owner is a finite window with floor 0; a continuous one is begun explicitly.
        if (slots != 0) replay.Begin(0);

        switch (scenario)
        {
            case "duration-distribution":
                replay.Start(0, 100, 1000, "Long", 10);
                replay.Start(0, 200, 2000, "Short", 20);
                replay.Switch(0, 0, 0, 0, 100, 10);
                replay.Switch(900, 0, 100, 10, 200, 20);
                replay.Switch(1000, 0, 200, 20, 0, 0);
                break;
            case "unknown-and-idle":
                replay.Start(0, 100, 1000, "Known", 10);
                replay.Switch(0, 0, 0, 0, 100, 10);
                replay.Switch(200, 0, 100, 10, -1, 999);
                replay.Switch(400, 0, -1, 999, 100, 10);
                replay.Switch(600, 0, 100, 10, -1, 0);
                replay.Switch(800, 0, -1, 0, 100, 10);
                replay.Switch(1000, 0, 100, 10, 0, 0);
                break;
            case "known-thread-unknown-pid":
                replay.Start(0, 100, 1000, "Known", 10);
                replay.Switch(0, 0, 0, 0, -1, 10);
                replay.Switch(1000, 0, -1, 10, 100, 10);
                replay.Switch(2000, 0, -1, 10, 0, 0);
                break;
            case "identity-mismatch":
                replay.Start(0, 100, 1000, "First", 10);
                replay.Start(0, 200, 2000, "Second", 20);
                replay.Switch(0, 0, 0, 0, 100, 10);
                replay.Switch(1000, 0, 200, 20, 200, 20);
                replay.Switch(2000, 0, 200, 20, 0, 0);
                break;
            case "pid-reuse":
                replay.Start(0, 100, 1000, "Old", 10, 11);
                replay.Switch(0, 0, 0, 0, 100, 10);
                replay.Switch(1000, 0, 100, 10, 0, 0);
                replay.Apply(new(Kind.ProcessEnd, 1000, 100, Unique: 11));
                replay.Start(1500, 100, 2000, "New", 10, 22);
                replay.Switch(2000, 0, 0, 0, 100, 10);
                replay.Switch(3000, 0, 100, 10, 0, 0);
                break;
            case "tid-reuse":
                replay.Start(0, 100, 1000, "Game", 10);
                replay.Switch(0, 0, 0, 0, 100, 10);
                replay.Switch(1000, 0, 100, 10, 0, 0);
                replay.Apply(new(Kind.ThreadEnd, 1000, 100, 10));
                replay.Apply(new(Kind.ThreadStart, 1500, 100, 10));
                replay.Switch(2000, 0, 0, 0, 100, 10);
                replay.Switch(3000, 0, 100, 10, 0, 0);
                break;
            case "lifecycle-before-boundary":
                replay.Start(0, 100, 1000, "Ended", 10, 11);
                replay.Start(0, 200, 2000, "Thread ended", 20, 22);
                replay.Switch(0, 0, 0, 0, 100, 10);
                replay.Switch(500, 0, 100, 10, 200, 20);
                replay.Apply(new(Kind.ProcessEnd, 700, 100, Unique: 11));
                replay.Apply(new(Kind.ThreadEnd, 900, 200, 20));
                replay.Switch(1000, 0, 200, 20, 0, 0);
                break;
            case "identity-metadata":
                replay.Apply(new(Kind.SeedProcess, Pid: 100, StartKey: 1000, Name: " Seed 中文 "));
                replay.Apply(new(Kind.SeedThread, Pid: 100, Tid: 10));
                replay.Apply(new(Kind.ProcessStart, 0, 100, Unique: 11, StartKey: 1000, Name: "  Renamed 中文  "));
                replay.Apply(new(Kind.ProcessStart, 0, 100, Unique: 11, StartKey: 9999, Name: "Final"));
                replay.Switch(0, 0, 0, 0, 100, 10);
                replay.Switch(1000, 0, 100, 10, 0, 0);
                replay.Start(1000, 100, 1000, "Different unique key", 10, 22);
                replay.Switch(1000, 0, 0, 0, 100, 10);
                replay.Switch(2000, 0, 100, 10, 0, 0);
                break;
            case "event-loss":
                replay.Start(0, 100, 1000, "Old", 10);
                replay.Switch(0, 0, 0, 0, 100, 10);
                replay.Switch(1000, 0, 100, 10, 0, 0);
                replay.Apply(new(Kind.Loss));
                replay.Start(900, 100, 1000, "Delayed", 10);
                replay.Apply(new(Kind.SeedProcess, Pid: 100, StartKey: 2000, Name: "Recovered"));
                replay.Apply(new(Kind.SeedThread, Pid: 100, Tid: 10));
                replay.Switch(1000, 0, 0, 0, 100, 10);
                replay.Switch(2000, 0, 100, 10, 0, 0);
                break;
            case "reset-and-clear":
                replay.Start(0, 100, 1000, "Game", 10);
                replay.Switch(0, 0, 0, 0, 100, 10);
                replay.Switch(1000, 0, 100, 10, 0, 0);
                replay.Begin(1000);
                replay.Switch(1000, 0, 0, 0, 100, 10);
                replay.Switch(2000, 0, 100, 10, 0, 0);
                replay.Apply(new(Kind.Clear));
                // A cleared owner is a fresh one; begin the theory's window shape again.
                if (slots != 0) replay.Begin(0);
                replay.Start(0, 100, 3000, "Fresh", 10);
                replay.Switch(0, 0, 0, 0, 100, 10);
                replay.Switch(1000, 0, 100, 10, 0, 0);
                break;
            case "capacity-rewarm":
                replay.Start(0, 100, 1000, "A", 10);
                replay.Start(0, 200, 2000, "B", 20);
                replay.Switch(0, 0, 0, 0, 100, 10);
                replay.Switch(500, 0, 100, 10, 200, 20);
                replay.Switch(1000, 0, 200, 20, 100, 10);
                replay.Switch(2000, 0, 100, 10, 0, 0);
                break;
            case "expired-pair":
                replay.Start(0, 100, 1000, "A", 10);
                replay.Start(0, 200, 2000, "B", 20);
                replay.Switch(0, 0, 0, 0, 100, 10);
                replay.Switch(100, 0, 100, 10, 200, 20);
                replay.Switch(200, 0, 200, 20, 100, 10);
                replay.Switch(1200, 0, 100, 10, 0, 0);
                break;
            case "partial-buckets":
                // Run edges that fall inside slots of either window grid.
                replay.Start(0, 100, 1000, "A", 10);
                replay.Start(0, 200, 2000, "B", 20);
                replay.Switch(0, 0, 0, 0, 100, 10);
                replay.Switch(123, 0, 100, 10, 200, 20);
                replay.Switch(777, 0, 200, 20, 100, 10);
                replay.Switch(1123, 0, 100, 10, 0, 0);
                replay.Switch(1291, 0, 0, 0, 0, 0);
                replay.Switch(2300, 0, 0, 0, 0, 0);
                break;
            case "shared-boundary":
                replay.Start(0, 100, 1000, "A", 10);
                replay.Start(0, 200, 2000, "B", 20);
                replay.Switch(0, 0, 0, 0, 100, 10);
                replay.Switch(0, 9, 0, 0, 200, 20);
                replay.Switch(1000, 9, 200, 20, 0, 0);
                replay.Switch(1700, 0, 100, 10, 0, 0);
                break;
            case "backward-time":
                replay.Start(0, 100, 1000, "Game", 10);
                replay.Switch(1000, 0, 0, 0, 100, 10);
                replay.Switch(900, 0, 100, 10, 0, 0);
                replay.Switch(1000, 0, 0, 0, 100, 10);
                replay.Switch(2000, 0, 100, 10, 0, 0);
                break;
            case "bucket-wrap":
                // An 8 QPC window: the continuous ring wraps many times.
                replay.Start(0, 100, 1000, "A", 10);
                replay.Start(0, 200, 2000, "B", 20);
                replay.Switch(0, 0, 0, 0, 100, 10);
                for (var qpc = 1; qpc <= 512; qpc++)
                    replay.Switch(qpc, 0, qpc % 2 == 1 ? 100 : 200, qpc % 2 == 1 ? 10 : 20,
                        qpc % 2 == 1 ? 200 : 100, qpc % 2 == 1 ? 20 : 10);
                break;
            case "maximum-qpc":
                replay.Apply(new(Kind.ProcessStart, long.MaxValue - 1, 100, Unique: 11, StartKey: 1000, Name: "Last"));
                replay.Apply(new(Kind.ThreadStart, long.MaxValue - 1, 100, 10));
                replay.Switch(long.MaxValue - 1, 0, 0, 0, 100, 10);
                replay.Switch(long.MaxValue, 0, 100, 10, 0, 0);
                break;
            case "rounded-qpc-window":
                replay.Start(0, 100, 1000, "Rounded", 10);
                replay.Switch(0, 0, 0, 0, 100, 10);
                replay.Switch(7, 0, 100, 10, 100, 10);
                replay.Switch(8, 0, 100, 10, 0, 0);
                replay.Switch(13, 0, 0, 0, 0, 0);
                break;
            default: throw new ArgumentOutOfRangeException(nameof(scenario));
        }

        // Acquiring twice cannot settle an open slice or mutate the previous result.
        replay.Compare();
        replay.Compare();
    }

    [Theory]
    [InlineData(new int[] { })]
    [InlineData(new[] { 0, 0 })]
    [InlineData(new[] { -1 })]
    [InlineData(new[] { 0, 1, -2 })]
    public void InvalidExpectedProcessorSetIsRejectedAtCreateByBothOwners(int[] processors)
    {
        Assert.Throws<ArgumentException>(() => new OracleCpuResidencyAggregationBuffer(1000, 1000, 1024, processors));
        var status = NativeReplay.Create(1000, 1000, 1024, processors, out var handle);
        if (handle != 0) NativeReplay.DestroyHandle(handle);
        // invalid_argument
        Assert.Equal(1, status);
    }

    [EnvironmentVariableFact("RESOURCE_MANAGER_ETL_REPLAY_PATH")]
    public void RecordedKernelEtlMatchesCSharpSnapshotsFromTheSameParserStream()
    {
        var path = Environment.GetEnvironmentVariable("RESOURCE_MANAGER_ETL_REPLAY_PATH");
        Assert.True(File.Exists(path), $"Kernel ETL does not exist: {path}");
        using var source = new ETWTraceEventSource(path);
        var qpcProperty = typeof(TraceEventSource).GetProperty("QPCFreq",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        var frequency = Assert.IsType<long>(qpcProperty?.GetValue(source));
        Assert.True(frequency > 0);
        var processors = Enumerable.Range(0, source.NumberOfProcessors).ToArray();
        using var finite = new Replay(frequency, 5000, 65536, processors, slots: 0, compareEveryEvent: false);
        using var continuous = new Replay(frequency, 5000, 65536, processors, slots: 4, compareEveryEvent: false);
        continuous.Begin(0);
        Replay[] replays = [finite, continuous];
        var parser = new KernelTraceEventParser(source);
        var dispatcher = new OracleKernelEtwSubscriptionDispatcher();
        dispatcher.AttachParser(parser);
        const int maximumSwitches = 2_000_000;
        const int maximumEvents = 4_000_000;
        var switches = 0;
        var events = 0;
        var snapshots = 0;
        var completeSnapshots = 0;
        long attributedQpc = 0;

        void Apply(Event input)
        {
            foreach (var replay in replays) replay.Apply(input);
            if (++events >= maximumEvents) source.StopProcessing();
        }

        dispatcher.InitializeSubscriptions(
            [new(1, "zig-csharp-equivalence", KernelTraceEventParser.Keywords.Process
                | KernelTraceEventParser.Keywords.Thread | KernelTraceEventParser.Keywords.ContextSwitch,
                (callbacks, _) =>
                {
#pragma warning disable CS0618 // Exact raw QPC values are the subject of this equivalence test.
                    callbacks.ProcessStartGroup += data => Apply(new(Kind.ProcessStart,
                        data.TimeStampQPC, data.ProcessID, Unique: unchecked((ulong)data.UniqueProcessKey),
                        Name: !string.IsNullOrWhiteSpace(data.ProcessName) ? data.ProcessName : data.ImageFileName,
                        Timestamp: Timestamp(data.TimeStamp)));
                    callbacks.ProcessEndGroup += data => Apply(new(Kind.ProcessEnd,
                        data.TimeStampQPC, data.ProcessID, Unique: unchecked((ulong)data.UniqueProcessKey),
                        Timestamp: Timestamp(data.TimeStamp)));
                    callbacks.ThreadStartGroup += data => Apply(new(Kind.ThreadStart,
                        data.TimeStampQPC, data.ProcessID, data.ThreadID, Timestamp: Timestamp(data.TimeStamp)));
                    callbacks.ThreadEndGroup += data => Apply(new(Kind.ThreadEnd,
                        data.TimeStampQPC, data.ProcessID, data.ThreadID, Timestamp: Timestamp(data.TimeStamp)));
                    callbacks.ThreadCSwitch += data =>
                    {
                        Apply(new(Kind.Switch, data.TimeStampQPC, data.NewProcessID, data.NewThreadID,
                            data.OldProcessID, data.OldThreadID, data.ProcessorNumber,
                            Name: data.NewProcessName, Timestamp: Timestamp(data.TimeStamp)));
                        switches++;
                        if (switches % 4096 == 0 || switches == maximumSwitches)
                        {
                            foreach (var replay in replays)
                            {
                                var snapshot = replay.Compare();
                                snapshots++;
                                if (snapshot.IsComplete)
                                {
                                    completeSnapshots++;
                                    attributedQpc += snapshot.Records.Sum(static row => row.ExecutionTimeQpc);
                                }
                            }
                        }
                        if (switches >= maximumSwitches) source.StopProcessing();
                    };
#pragma warning restore CS0618
                })], new(1, DateTimeOffset.UnixEpoch));

        source.Process();
        foreach (var replay in replays)
        {
            var final = replay.Compare();
            if (final.IsComplete)
            {
                completeSnapshots++;
                attributedQpc += final.Records.Sum(static row => row.ExecutionTimeQpc);
            }
        }
        Assert.InRange(switches, 4096, maximumSwitches);
        Assert.InRange(events, switches, maximumEvents);
        Assert.True(snapshots > 0);
        Assert.True(completeSnapshots > 0, $"No complete windows compared after {switches} switches / {events} events.");
        Assert.True(attributedQpc > 0, "The ETL comparison must include nonempty attributed execution.");
    }

    private static DateTimeOffset Timestamp(DateTime timestamp)
        => new(timestamp.Kind == DateTimeKind.Utc ? timestamp : timestamp.ToUniversalTime());

    [EnvironmentVariableFact("RESOURCE_MANAGER_ETL_REPLAY_PATH")]
    public void NativeConsumerDirectlyReplaysTheSameEtlPrefixAsTheManagedParser()
    {
        var path = Environment.GetEnvironmentVariable("RESOURCE_MANAGER_ETL_REPLAY_PATH");
        Assert.True(File.Exists(path), $"Kernel ETL does not exist: {path}");
        const int switchLimit = 2_000_000;
        using var source = new ETWTraceEventSource(path);
        var frequencyProperty = typeof(TraceEventSource).GetProperty("QPCFreq",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        var frequency = Assert.IsType<long>(frequencyProperty?.GetValue(source));
        Assert.True(frequency > 0);
        var processors = Enumerable.Range(0, source.NumberOfProcessors).ToArray();
        var managed = new OracleCpuResidencyAggregationBuffer(frequency, 5000, 65536, processors);
        var initialEventsLost = checked((uint)source.EventsLost);
        // OpenTrace reports historical session loss before delivering any records.
        // Match that empty-state reset; a later loss must not be absorbed into this baseline.
        if (initialEventsLost != 0) managed.ResetAfterEventLoss();
        var parser = new KernelTraceEventParser(source);
        var dispatcher = new OracleKernelEtwSubscriptionDispatcher();
        dispatcher.AttachParser(parser);
        var switches = 0;
        dispatcher.InitializeSubscriptions(
            [new(1, "direct-native-etl-oracle", KernelTraceEventParser.Keywords.Process
                | KernelTraceEventParser.Keywords.Thread | KernelTraceEventParser.Keywords.ContextSwitch,
                (callbacks, _) =>
                {
                    // The production binding runs first, including the exact final switch.
                    // No events after that boundary may enter the managed owner.
                    OracleCpuResidencyEventAdapter.BindKernelEventCallbacks(callbacks, managed,
                        () => switches < switchLimit, static _ => null);
                    callbacks.ThreadCSwitch += _ =>
                    {
                        if (switches >= switchLimit) return;
                        if (++switches == switchLimit) source.StopProcessing();
                    };
                })], new(1, DateTimeOffset.UnixEpoch));
        source.Process();
        Assert.InRange(switches, 4096, switchLimit);
        Assert.Equal(initialEventsLost, checked((uint)source.EventsLost));
        var expected = managed.Snapshot();
        Assert.True(expected.IsComplete, "The chosen ETL prefix must contain a complete managed window.");
        Assert.NotEmpty(expected.Records);

        using var native = new NativeReplay(frequency, 5000, 65536, processors);
        var status = native.ReplayEtl(path!, (ulong)switches);
        Assert.Equal((ulong)switches, status.Switches);
        Assert.Equal(frequency, status.QpcFrequency);
        Assert.Equal((uint)source.NumberOfProcessors, status.ProcessorCount);
        Assert.Equal(0UL, status.Rejected);
        Assert.Equal(initialEventsLost, status.EventsLost);
        Assert.Equal(0u, status.BuffersLost);
        AssertSnapshotsEqual(expected, native.Snapshot());
    }

    [Fact]
    public void NativeReferencePublicationSurvivesEventLossAndReleasesBudgetedStorage()
    {
        using var native = new NativeReplay(1000, 1000, 64, [0]);
        native.Apply(new(Kind.ProcessStart, Pid: 100, Unique: 11, StartKey: 1000, Name: "Stable 中文"),
            DateTimeOffset.UnixEpoch);
        native.Apply(new(Kind.ThreadStart, Pid: 100, Tid: 10), DateTimeOffset.UnixEpoch);
        native.Apply(new(Kind.Switch, Pid: 100, Tid: 10), DateTimeOffset.UnixEpoch);
        native.Apply(new(Kind.Switch, 1000, OldPid: 100, OldTid: 10), DateTimeOffset.UnixEpoch.AddSeconds(1));
        native.AssertPublicationSurvivesLoss();
    }

    private static void AssertSnapshotsEqual(CpuResidencyAggregationSnapshot expected,
        CpuResidencyAggregationSnapshot actual)
    {
        Assert.Equal(expected.QpcFrequency, actual.QpcFrequency);
        Assert.Equal(expected.WindowStartQpc, actual.WindowStartQpc);
        Assert.Equal(expected.MeasuredThroughQpc, actual.MeasuredThroughQpc);
        Assert.Equal(expected.MeasuredThrough, actual.MeasuredThrough);
        Assert.Equal(expected.IsComplete, actual.IsComplete);
        Assert.Equal(Sorted(expected.Records), Sorted(actual.Records));
    }

    private static CpuExecutionTimeAggregate[] Sorted(IReadOnlyList<CpuExecutionTimeAggregate> rows)
        => rows.OrderBy(static row => row.LogicalProcessorId).ThenBy(static row => row.ProcessInstanceId)
            .ThenBy(static row => row.ThreadInstanceId).ToArray();

    // Begin: Qpc = floor, Unique = slots per window (0 = finite).
    private enum Kind : uint { SeedProcess = 1, SeedThread, ProcessStart, ProcessEnd, ThreadStart,
        ThreadEnd, Switch, Loss, Begin, Clear }

    private readonly record struct Event(Kind Kind, long Qpc = 0, int Pid = 0, int Tid = 0,
        int OldPid = 0, int OldTid = 0, int Processor = 0, ulong Unique = 0,
        long? StartKey = null, string? Name = null, DateTimeOffset? Timestamp = null);

    private sealed class Replay : IDisposable
    {
        private readonly OracleCpuResidencyAggregationBuffer managed;
        private readonly NativeReplay native;
        private readonly bool compareEveryEvent;
        private readonly int slots;

        public Replay(long frequency, int window, int pairs, int[] processors, int slots, bool compareEveryEvent)
        {
            managed = new(frequency, window, pairs, processors);
            native = new(frequency, window, pairs, processors);
            this.slots = slots;
            this.compareEveryEvent = compareEveryEvent;
        }

        public void Begin(long floor) => Apply(new(Kind.Begin, floor, Unique: (ulong)slots));

        public void Start(long qpc, int pid, long startKey, string name, int tid, ulong unique = 1)
        {
            Apply(new(Kind.ProcessStart, qpc, pid, Unique: unique, StartKey: startKey, Name: name));
            Apply(new(Kind.ThreadStart, qpc, pid, tid));
        }

        public void Switch(long qpc, int lp, int oldPid, int oldTid, int newPid, int newTid)
            => Apply(new(Kind.Switch, qpc, newPid, newTid, oldPid, oldTid, lp));

        public void Apply(Event input)
        {
            // QPC and civil time are independent. Keep synthetic civil time bounded, including MaxValue QPC.
            var time = input.Timestamp ?? DateTimeOffset.UnixEpoch.AddTicks(input.Qpc % TimeSpan.TicksPerDay);
            switch (input.Kind)
            {
                case Kind.SeedProcess: managed.SeedProcess(input.Pid, input.StartKey, input.Name); break;
                case Kind.SeedThread: managed.SeedThread(input.Pid, input.Tid); break;
                case Kind.ProcessStart: managed.ObserveProcessStart(input.Qpc, time, input.Pid,
                    input.Unique, input.StartKey, input.Name); break;
                case Kind.ProcessEnd: managed.ObserveProcessEnd(input.Qpc, time, input.Pid, input.Unique); break;
                case Kind.ThreadStart: managed.ObserveThreadStart(input.Qpc, time, input.Pid, input.Tid); break;
                case Kind.ThreadEnd: managed.ObserveThreadEnd(input.Qpc, time, input.Pid, input.Tid); break;
                case Kind.Switch: managed.ObserveContextSwitch(input.Qpc, time, input.Processor,
                    input.OldPid, input.OldTid, input.Pid, input.Name, input.Tid); break;
                case Kind.Loss: managed.ResetAfterEventLoss(); break;
                case Kind.Begin: managed.BeginWindow(input.Qpc, checked((int)input.Unique)); break;
                case Kind.Clear: managed.Clear(); break;
                default: throw new ArgumentOutOfRangeException(nameof(input));
            }
            // Capacity overflow is a declared fail-closed reset; native may report it only where the oracle reset.
            native.Apply(input, time, managed.CapacityResetCount != capacityResets);
            capacityResets = managed.CapacityResetCount;
            if (compareEveryEvent) Compare();
        }

        private long capacityResets;

        public CpuResidencyAggregationSnapshot Compare()
        {
            var expected = managed.Snapshot();
            var actual = native.Snapshot();
            AssertSnapshotsEqual(expected, actual);
            return expected;
        }

        public void Dispose() => native.Dispose();
    }

    // Deliberately test-local: the retained C# runtime has no dependency on the new Zig owner.
    private sealed class NativeReplay : IDisposable
    {
        private nint handle;

        public NativeReplay(long frequency, int window, int pairs, int[] processors)
        {
            Assert.Equal(0, Create(frequency, window, pairs, processors, out handle));
            Assert.NotEqual(nint.Zero, handle);
        }

        // The owner copies the expected processors; the pin only spans the call.
        public static unsafe int Create(long frequency, int window, int pairs, int[] processors, out nint handle)
        {
            fixed (int* processorPointer = processors)
            {
                var config = new Configuration
                {
                    Size = (uint)Marshal.SizeOf<Configuration>(), Version = 0x10000,
                    QpcFrequency = frequency, WindowMilliseconds = (uint)window,
                    MaximumPairs = (uint)pairs, MaximumProcesses = 32768, MaximumThreads = 131072,
                    MaximumProcessors = 4096, MaximumNameBytes = 8 * 1024 * 1024,
                    ByteCap = 256 * 1024 * 1024,
                    ProcessorCount = (uint)processors.Length, Processors = (nint)processorPointer
                };
                return CreateNative(ref config, out handle);
            }
        }

        public static void DestroyHandle(nint value) => Destroy(value);

        public unsafe void Apply(Event input, DateTimeOffset time, bool capacityReset = false)
        {
            var name = input.Name is null ? null : Encoding.UTF8.GetBytes(input.Name);
            fixed (byte* namePointer = name)
            {
                var value = new Input
                {
                    Kind = (uint)input.Kind, Flags = input.StartKey.HasValue ? 1u : 0u,
                    Qpc = input.Qpc, TimeTicks = time.UtcTicks, ProcessId = input.Pid,
                    ThreadId = input.Tid, OldProcessId = input.OldPid, OldThreadId = input.OldTid,
                    Processor = input.Processor, UniqueKey = input.Unique, StartKey = input.StartKey ?? 0,
                    Name = (nint)namePointer, NameLength = (uint)(name?.Length ?? 0)
                };
                var status = ApplyNative(handle, ref value);
                var capacity = status != 0 ? DescribeCapacityFailure() : string.Empty;
                // Capacity overflow is a declared fail-closed reset, also exercised by the C# oracle.
                Assert.True(status == 0 || status == 5 && capacityReset,
                    $"Native event {input.Kind} at QPC {input.Qpc} returned {status}. {capacity}");
            }
        }

        public CpuResidencyAggregationSnapshot Snapshot()
        {
            Assert.Equal(0, Acquire(handle, out var view));
            try
            {
                return Copy(view);
            }
            finally { Release(handle); }
        }

        private static unsafe CpuResidencyAggregationSnapshot Copy(View view)
        {
            Assert.InRange(view.Count, 0u, 65536u);
            Assert.True(view.CurrentBytes <= 256UL * 1024 * 1024);
            Assert.True(view.PeakBytes <= 256UL * 1024 * 1024);
            var rows = new CpuExecutionTimeAggregate[checked((int)view.Count)];
            for (var index = 0; index < rows.Length; index++)
            {
                var row = ((Row*)view.Records)[index];
                rows[index] = new(row.ProcessInstance, row.ProcessId,
                    (row.Flags & 1) != 0 ? row.StartKey : null,
                    Marshal.PtrToStringUTF8(row.Name, checked((int)row.NameLength)) ?? string.Empty,
                    row.ThreadInstance, row.ThreadId, row.Processor, row.Duration, row.Switches);
            }
            return new(view.Frequency, view.StartQpc, view.ThroughQpc,
                (view.Flags & 2) != 0 ? new DateTimeOffset(view.TimeTicks, TimeSpan.Zero) : null,
                (view.Flags & 1) != 0, rows);
        }

        public unsafe TraceStatus ReplayEtl(string path, ulong switchLimit)
        {
            fixed (char* pathPointer = path)
            {
                Assert.Equal(0, TraceOpen(handle, pathPointer, (uint)path.Length, 0, out var trace));
                Assert.NotEqual(nint.Zero, trace);
                try
                {
                    var result = TraceProcess(trace, switchLimit);
                    Assert.Equal(0, GetTraceStatus(trace, out var status, (uint)Marshal.SizeOf<TraceStatus>()));
                    var capacity = result != 0 || status.OwnerStatus != 0
                        ? DescribeCapacityFailure() : string.Empty;
                    Assert.True(result == 0,
                        $"Native ETL result={result}; Windows={status.WindowsStatus}; owner={status.OwnerStatus}; "
                        + $"records={status.Records}; decoded={status.Decoded}; rejected={status.Rejected}; "
                        + $"unhandled={status.Unhandled}; switches={status.Switches}. {capacity}");
                    Assert.True(status.OwnerStatus == 0, $"Native owner status={status.OwnerStatus}. {capacity}");
                    Assert.True(status.WindowsStatus is 0 or 1223);
                    return status;
                }
                finally { Assert.Equal(0, TraceClose(trace)); }
            }
        }

        private string DescribeCapacityFailure()
        {
            var result = QueryCapacityFailure(handle, out var failure, (uint)Marshal.SizeOf<CapacityFailure>());
            return result != 0 ? $"Capacity diagnostic query returned {result}."
                : $"capacity reason={failure.Reason}; observed={failure.Observed}; limit={failure.Limit}; "
                    + $"processor={failure.Processor}; eventQpc={failure.EventQpc}; "
                    + $"oldestProcessor={failure.OldestProcessor}; oldestBoundary={failure.OldestBoundaryQpc}; "
                    + $"windowStart={failure.WindowStartQpc}; pairs={failure.Pairs}; "
                    + $"processors={failure.Processors}; "
                    + $"processes={failure.Processes}; threads={failure.Threads}; nameBytes={failure.NameBytes}.";
        }

        public void AssertPublicationSurvivesLoss()
        {
            ulong heldPublicationBytes = 0;
            Assert.Equal(0, Acquire(handle, out var view));
            try
            {
                var original = Copy(view);
                Assert.True(original.IsComplete);
                Assert.Single(original.Records);
                Apply(new(Kind.Loss), DateTimeOffset.UnixEpoch);
                AssertSnapshotsEqual(original, Copy(view));
                Assert.Equal(3, Acquire(handle, out _));
                Assert.Equal(0, QueryUsage(handle, out var held, (uint)Marshal.SizeOf<Usage>()));
                Assert.True(held.CurrentBytes <= held.ByteCap);
                Assert.True(held.PeakBytes <= held.ByteCap);
                Assert.Equal(0UL, held.FailedAllocations);
                heldPublicationBytes = held.CurrentBytes;
            }
            finally { Release(handle); }
            Assert.Equal(0, QueryUsage(handle, out var released, (uint)Marshal.SizeOf<Usage>()));
            Assert.True(released.CurrentBytes < heldPublicationBytes,
                $"Releasing the publication did not reduce native allocations: held={heldPublicationBytes}, released={released.CurrentBytes}.");
            Assert.False(Snapshot().IsComplete);
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct TraceStatus
        {
            public uint WindowsStatus;
            public int OwnerStatus;
            public ulong Records, Decoded, Rejected, Unhandled, Switches;
            public uint EventsLost, BuffersLost;
            public long QpcFrequency;
            public uint ProcessorCount, OsBufferSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Usage
        {
            public ulong CurrentBytes, PeakBytes, ByteCap, FailedAllocations;
            public int LastError;
            public uint TraceError;
            public ulong EventsLost;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct CapacityFailure
        {
            public uint Reason;
            public int Processor;
            public long EventQpc;
            public ulong Observed, Limit, Pairs;
            // -1 = no window start.
            public long WindowStartQpc;
            public int OldestProcessor;
            public uint Reserved;
            public long OldestBoundaryQpc;
            public ulong Processors, Processes, Threads, NameBytes;
        }

        public void Dispose()
        {
            if (handle == 0) return;
            Destroy(handle);
            handle = 0;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Configuration
        {
            public uint Size, Version;
            public long QpcFrequency;
            public uint WindowMilliseconds, MaximumPairs,
                MaximumProcesses, MaximumThreads, MaximumProcessors, MaximumNameBytes;
            public ulong ByteCap;
            public uint ProcessorCount, Reserved;
            public nint Processors;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Input
        {
            public uint Kind, Flags;
            public long Qpc, TimeTicks;
            public int ProcessId, ThreadId, OldProcessId, OldThreadId, Processor;
            public uint Reserved;
            public ulong UniqueKey;
            public long StartKey;
            public nint Name;
            public uint NameLength, Reserved2;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct View
        {
            public long Frequency, StartQpc, ThroughQpc, TimeTicks;
            public uint Flags, Count;
            public nint Records;
            public ulong CurrentBytes, PeakBytes;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Row
        {
            public long ProcessInstance;
            public int ProcessId;
            public uint Flags;
            public long StartKey, ThreadInstance;
            public int ThreadId, Processor;
            public long Duration;
            public int Switches;
            public uint NameLength;
            public nint Name;
        }

        private const string Library = "ResourceManager.NativeCore";
        [DllImport(Library, EntryPoint = "rm_kernel_etw_create", CallingConvention = CallingConvention.Cdecl)]
        private static extern int CreateNative(ref Configuration config, out nint handle);
        [DllImport(Library, EntryPoint = "rm_kernel_etw_destroy", CallingConvention = CallingConvention.Cdecl)]
        private static extern void Destroy(nint handle);
        [DllImport(Library, EntryPoint = "rm_kernel_etw_apply", CallingConvention = CallingConvention.Cdecl)]
        private static extern int ApplyNative(nint handle, ref Input input);
        [DllImport(Library, EntryPoint = "rm_kernel_etw_acquire_residency", CallingConvention = CallingConvention.Cdecl)]
        private static extern int Acquire(nint handle, out View view);
        [DllImport(Library, EntryPoint = "rm_kernel_etw_release_residency", CallingConvention = CallingConvention.Cdecl)]
        private static extern void Release(nint handle);
        [DllImport(Library, EntryPoint = "rm_kernel_etw_query_usage", CallingConvention = CallingConvention.Cdecl)]
        private static extern int QueryUsage(nint handle, out Usage usage, uint size);
        [DllImport(Library, EntryPoint = "rm_kernel_etw_query_capacity_failure", CallingConvention = CallingConvention.Cdecl)]
        private static extern int QueryCapacityFailure(nint handle, out CapacityFailure failure, uint size);
        [DllImport(Library, EntryPoint = "rm_kernel_etw_trace_open", CallingConvention = CallingConvention.Cdecl)]
        private static extern unsafe int TraceOpen(nint owner, char* name, uint length, uint realtime, out nint trace);
        [DllImport(Library, EntryPoint = "rm_kernel_etw_trace_process", CallingConvention = CallingConvention.Cdecl)]
        private static extern int TraceProcess(nint trace, ulong switchLimit);
        [DllImport(Library, EntryPoint = "rm_kernel_etw_trace_status", CallingConvention = CallingConvention.Cdecl)]
        private static extern int GetTraceStatus(nint trace, out TraceStatus status, uint size);
        [DllImport(Library, EntryPoint = "rm_kernel_etw_trace_close", CallingConvention = CallingConvention.Cdecl)]
        private static extern int TraceClose(nint trace);
    }
}
