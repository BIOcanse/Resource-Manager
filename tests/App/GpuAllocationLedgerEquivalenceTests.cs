using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text.Json;
using ResourceManager.App.Infrastructure.Telemetry.Etw;
using Xunit.Abstractions;

namespace Resource_Manager_APP.Tests;

// The compact ledger must read exactly what the previous representation read for the same event sequence.
// A call that throws ends a sequence: the collector discards a ledger that threw, so later state is never read.
public sealed class GpuAllocationLedgerEquivalenceTests(ITestOutputHelper output)
{
    [Fact]
    public void RecordedRundownAndLiveEventsReadTheSameAsThePreviousLedger()
    {
        var loaded = LoadRecorded();
        // The collector applies the rundown and then the live events, as GpuAllocationRecordedDecodingTests does.
        var records = loaded.Where(item => item.Kind == "rundown").Concat(loaded.Where(item => item.Kind == "live")).ToArray();
        var opens = records.Where(item => item.Id is 36 or 38).ToArray();
        Assert.NotEmpty(opens);
        var pids = opens.Select(item => Convert.ToInt32(item.Fields["hProcessId"])).Distinct().Order().ToArray();
        var times = opens.Select(item => item.At.ToFileTimeUtc()).Order().ToArray();
        // Every process visible; then half born after the median reference, so older references are filtered.
        var allVisible = pids.ToDictionary(pid => pid, _ => 1L);
        var laterBirths = pids.Select((pid, i) => (pid, i)).ToDictionary(item => item.pid,
            item => item.i % 2 == 0 ? times[times.Length / 2] : 1L);
        var expected = new GpuAllocationLedgerReference();
        var actual = new GpuAllocationLedger();
        var reads = 0;
        var step = 0;
        foreach (var record in records)
        {
            var both = ApplyBoth(expected, actual, ledger =>
                GpuAllocationEventDecoder.Apply(ledger, record.Id, record.Payload, record.At));
            Assert.True(both, $"Recorded event {step} (id {record.Id}) threw in both ledgers.");
            step++;
            if (record.Kind == "live" || step % 997 == 0 || step == records.Length)
            {
                AssertSameReading(expected, actual, allVisible, $"after event {step}");
                AssertSameReading(expected, actual, laterBirths, $"after event {step}, later births");
                reads++;
            }
        }
        var final = actual.Read(allVisible);
        Assert.NotEmpty(final.Adapters);
        Assert.Contains(final.Adapters.Values, adapter => adapter.Count > 0);
        output.WriteLine($"{records.Length} recorded events, {reads} compared reads, {final.Adapters.Values.Sum(a => a.Count)} process amounts.");
    }

    [Theory]
    [InlineData(1, 262144, 1048576)]
    [InlineData(2, 262144, 1048576)]
    [InlineData(3, 262144, 1048576)]
    [InlineData(4, 5, 7)]
    [InlineData(5, 3, 4)]
    public void RandomEventSequencesReadAndFailTheSameAsThePreviousLedger(int seed, int allocations, int references)
    {
        var random = new Random(seed);
        var compared = 0;
        var failed = 0;
        for (var sequence = 0; sequence < 160; sequence++)
        {
            var expected = new GpuAllocationLedgerReference(allocations, references);
            var actual = new GpuAllocationLedger(allocations, references);
            var generator = new SequenceGenerator(random);
            for (var operation = 0; operation < 140; operation++)
            {
                var (name, apply) = generator.Next();
                if (!ApplyBoth(expected, actual, apply, $"sequence {sequence} op {operation} {name}"))
                {
                    failed++;
                    break;
                }
                foreach (var births in SequenceGenerator.Births)
                    AssertSameReading(expected, actual, births, $"sequence {sequence} op {operation} {name}");
                compared++;
            }
        }
        output.WriteLine($"seed {seed}: {compared} compared states, {failed} sequences ended by a matching exception.");
        Assert.True(compared > 160 * 3, "Generated sequences should mostly stay valid.");
    }

    [Fact]
    public void SegmentReclassifiedWhileHoldingRangesReadsTheSameAsThePreviousLedger()
    {
        var expected = new GpuAllocationLedgerReference();
        var actual = new GpuAllocationLedger();
        foreach (IGpuAllocationLedgerUpdates ledger in new IGpuAllocationLedgerUpdates[] { expected, actual })
        {
            ledger.SetAdapter(1, 23);
            ledger.SetAllocation(1, 2, 4096);
            ledger.Open(1, 2, 11, 41, 200);
            ledger.Open(1, 2, 12, 42, 200);
            ledger.AddResidentRange(1, 2, 2, 0, 4096);
        }
        Assert.Throws<InvalidDataException>(() => expected.Read(Births));
        Assert.Throws<InvalidDataException>(() => actual.Read(Births));
        foreach (var flags in new uint[] { 0, 0x1000, 0, 0x1 })
        {
            expected.SetSegment(1, 2, flags);
            actual.SetSegment(1, 2, flags);
            expected.AddResidentRange(1, 2, 2, 2048, 4096);
            actual.AddResidentRange(1, 2, 2, 2048, 4096);
            AssertSameReading(expected, actual, Births, $"flags {flags:x}");
        }
        expected.SetSegment(1, 2, 0);
        actual.SetSegment(1, 2, 0);
        Assert.Equal(3072, actual.Read(Births).Adapters[23][41].SharedBytes);
        AssertSameReading(expected, actual, Births, "dedicated again");
    }

    [Fact]
    public void ReusedPidCountsOnlyAllocationsReferencedSinceTheCurrentBirth()
    {
        var expected = new GpuAllocationLedgerReference();
        var actual = new GpuAllocationLedger();
        foreach (IGpuAllocationLedgerUpdates ledger in new IGpuAllocationLedgerUpdates[] { expected, actual })
        {
            ledger.SetAdapter(1, 23);
            ledger.SetSegment(1, 2, 0);
            ledger.SetAllocation(1, 2, 4096);
            ledger.Open(1, 2, 11, 41, 150);
            ledger.SetCommittedGlobal(2, 2);
            ledger.SetAllocation(1, 3, 1024);
            ledger.Open(1, 3, 12, 41, 320);
            ledger.Open(1, 3, 13, 42, 320);
            ledger.SetCommittedGlobal(3, 2);
        }
        var reused = new Dictionary<int, long> { [41] = 300, [42] = 100 };
        AssertSameReading(expected, actual, reused, "reused 41");
        var amount = actual.Read(reused).Adapters[23][41];
        Assert.Equal((0D, 512D), (amount.PrivateBytes, amount.SharedBytes));
        // After the exact pass, the same read stays exact; the old instance's allocation going away restores 41's total.
        AssertSameReading(expected, actual, reused, "reused 41 again");
        expected.Delete(1, 2);
        actual.Delete(1, 2);
        AssertSameReading(expected, actual, reused, "old allocation deleted");
        AssertSameReading(expected, actual, Births, "original births");
    }

    private static readonly Dictionary<int, long> Births = new() { [41] = 100, [42] = 120 };

    // True when both applied; false when both threw the same exception type; fails when only one threw.
    private static bool ApplyBoth(GpuAllocationLedgerReference expected, GpuAllocationLedger actual,
        Action<IGpuAllocationLedgerUpdates> apply, string context = "")
    {
        var expectedError = Record.Exception(() => apply(expected));
        var actualError = Record.Exception(() => apply(actual));
        Assert.True(expectedError?.GetType() == actualError?.GetType(),
            $"{context}: previous ledger {Describe(expectedError)}, compact ledger {Describe(actualError)}");
        return expectedError is null;
    }

    private static void AssertSameReading(GpuAllocationLedgerReference expectedLedger, GpuAllocationLedger actualLedger,
        IReadOnlyDictionary<int, long> births, string context)
    {
        GpuAllocationReading? expected = null, actual = null;
        var expectedError = Record.Exception(() => expected = expectedLedger.Read(births));
        var actualError = Record.Exception(() => actual = actualLedger.Read(births));
        Assert.True(expectedError?.GetType() == actualError?.GetType(),
            $"{context}: previous read {Describe(expectedError)}, compact read {Describe(actualError)}");
        if (expectedError is not null) return;
        Assert.Equal(expected!.Adapters.Keys.Order(), actual!.Adapters.Keys.Order());
        foreach (var (luid, processes) in expected.Adapters)
        {
            var other = actual.Adapters[luid];
            Assert.True(processes.Keys.Order().SequenceEqual(other.Keys.Order()),
                $"{context}: adapter {luid} processes {string.Join(",", processes.Keys.Order())} vs {string.Join(",", other.Keys.Order())}");
            foreach (var (pid, amount) in processes)
            {
                Assert.True(amount.PrivateBytes == other[pid].PrivateBytes,
                    $"{context}: adapter {luid} pid {pid} private {amount.PrivateBytes} vs {other[pid].PrivateBytes}");
                // Shares of several owners are summed in a different order; doubles may differ in the last bits.
                Assert.True(Math.Abs(amount.SharedBytes - other[pid].SharedBytes) <= 1e-9 * Math.Max(1, Math.Abs(amount.SharedBytes)),
                    $"{context}: adapter {luid} pid {pid} shared {amount.SharedBytes} vs {other[pid].SharedBytes}");
            }
        }
        Assert.Equal(expected.KernelMetadataBytes.OrderBy(pair => pair.Key), actual.KernelMetadataBytes.OrderBy(pair => pair.Key));
    }

    private static string Describe(Exception? error) => error is null ? "succeeded" : $"threw {error.GetType().Name}: {error.Message}";

    private sealed class SequenceGenerator(Random random)
    {
        internal static readonly IReadOnlyDictionary<int, long>[] Births =
        [
            new Dictionary<int, long> { [41] = 100, [42] = 100, [43] = 100, [4] = 100 },
            new Dictionary<int, long> { [41] = 100, [42] = 300, [43] = 250 },
            new Dictionary<int, long> { [41] = 260, [42] = 0 }
        ];
        private static readonly ulong[] Adapters = [1, 2];
        private static readonly uint[] Segments = [0, 1, 2, 3];
        private static readonly uint[] Flags = [0, 0, 0x1000, 0x1, 0x2];
        private static readonly int[] Pids = [41, 42, 43, 4];
        private static readonly long[] Times = [150, 200, 260, 320, 400];
        private static readonly ulong[] Placements = [0, 4096, 8192];
        private static readonly ulong[] Offsets = [0, 0, 512, 1024, 4096];
        private static readonly ulong[] Lengths = [0, 512, 1024, 2048, 4096];
        private readonly Dictionary<(ulong, ulong), ulong> sizes = [];
        private readonly Dictionary<ulong, int> pendingOwners = [];

        private T Pick<T>(T[] values) => values[random.Next(values.Length)];
        private ulong Global() => (ulong)random.Next(1, 7);
        private ulong Reference() => (ulong)random.Next(1, 11);

        internal (string Name, Action<IGpuAllocationLedgerUpdates> Apply) Next()
        {
            var adapter = Pick(Adapters);
            var global = Global();
            var segment = Pick(Segments);
            var placement = Pick(Placements);
            switch (random.Next(100))
            {
                case < 6:
                {
                    var flags = random.Next(50) == 0 ? 0x20u : Pick(Flags);
                    return ($"SetSegment({adapter},{segment},{flags:x})", ledger => ledger.SetSegment(adapter, segment, flags));
                }
                case < 9:
                {
                    var luid = adapter * 10 + (ulong)random.Next(2);
                    return ($"SetAdapter({adapter},{luid})", ledger => ledger.SetAdapter(adapter, luid));
                }
                case < 22:
                {
                    var bytes = Pick(Lengths) + 4096;
                    var kernel = random.Next(8) == 0;
                    sizes[(adapter, global)] = bytes;
                    return ($"SetAllocation({adapter},{global},{bytes},{kernel})", ledger => ledger.SetAllocation(adapter, global, bytes, kernel));
                }
                case < 30:
                    return ($"SetCommittedGlobal({global},{segment},{placement})", ledger => ledger.SetCommittedGlobal(global, segment, placement));
                case < 35:
                {
                    var reference = Reference();
                    var pid = Pick(Pids);
                    pendingOwners[reference] = pid;
                    var bytes = Pick(Lengths);
                    return ($"SetCommittedReference({reference},{pid},{segment},{bytes},{placement})",
                        ledger => ledger.SetCommittedReference(reference, pid, segment, bytes, placement));
                }
                case < 50:
                {
                    var offset = Pick(Offsets);
                    var bytes = Pick(Lengths);
                    return ($"AddResidentRange({adapter},{global},{segment},{offset},{bytes},{placement})",
                        ledger => ledger.AddResidentRange(adapter, global, segment, offset, bytes, placement));
                }
                case < 56:
                {
                    ulong? only = random.Next(2) == 0 ? null : placement;
                    return ($"DiscardResident({adapter},{global},{segment},{only})", ledger => ledger.DiscardResident(adapter, global, segment, only));
                }
                case < 61:
                {
                    var bytes = random.Next(10) == 0 || !sizes.TryGetValue((adapter, global), out var size) ? Pick(Lengths) : size;
                    return ($"RecordTransferredSource({adapter},{global},{segment},{placement},{bytes})",
                        ledger => ledger.RecordTransferredSource(adapter, global, segment, placement, bytes));
                }
                case < 65:
                    return ($"CompleteEviction({global})", ledger => ledger.CompleteEviction(global));
                case < 85:
                {
                    var reference = Reference();
                    var pid = pendingOwners.TryGetValue(reference, out var owner) && random.Next(10) != 0 ? owner : Pick(Pids);
                    var time = Pick(Times);
                    pendingOwners.Remove(reference);
                    return ($"Open({adapter},{global},{reference},{pid},{time})", ledger => ledger.Open(adapter, global, reference, pid, time));
                }
                case < 94:
                {
                    var reference = Reference();
                    pendingOwners.Remove(reference);
                    return ($"Close({reference})", ledger => ledger.Close(reference));
                }
                default:
                    sizes.Remove((adapter, global));
                    return ($"Delete({adapter},{global})", ledger => ledger.Delete(adapter, global));
            }
        }
    }

    private static RecordedEvent[] LoadRecorded([CallerFilePath] string source = "")
    {
        using var file = File.OpenRead(Path.Combine(Path.GetDirectoryName(source)!, "Fixtures/GpuAllocation/recorded-events.jsonl.gz"));
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip);
        var result = new List<RecordedEvent>();
        while (reader.ReadLine() is { } line)
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            var item = root.GetProperty("event");
            var fields = item.GetProperty("fields").EnumerateObject().ToDictionary(pair => pair.Name, pair => Value(pair.Value));
            result.Add(new(root.GetProperty("kind").GetString()!, item.GetProperty("id").GetInt32(),
                item.GetProperty("timestampUtc").GetDateTime(), fields));
        }
        return result.ToArray();
    }

    private static object Value(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number => value.TryGetInt64(out var signed) ? (object)signed : value.GetUInt64(),
        _ => value.ToString()
    };

    private sealed class RecordedEvent(string kind, int id, DateTime at, Dictionary<string, object> fields)
    {
        internal string Kind { get; } = kind;
        internal int Id { get; } = id;
        internal DateTime At { get; } = at;
        internal Dictionary<string, object> Fields { get; } = fields;
        internal Func<string, object> Payload { get; } = key => fields[key];
    }
}
