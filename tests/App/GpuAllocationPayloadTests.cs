using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Diagnostics.Tracing;
using ResourceManager.App.Infrastructure.Telemetry.Etw;
using Xunit.Abstractions;

namespace Resource_Manager_APP.Tests;

public sealed class GpuAllocationPayloadTests(ITestOutputHelper output)
{
    [Fact]
    public void CachedIndicesMatchEveryRecordedFieldAcrossEventIdsAndVersions()
    {
        var expected = RecordedJson();
        var count = 0;
        var schemas = new HashSet<(int Id, int Version)>();
        var payload = new GpuAllocationIndexedPayload();
        using var source = new ETWTraceEventSource(Fixture("recorded-payloads.etl.bin"), TraceEventSourceType.FileOnly);
        source.Dynamic.All += data =>
        {
            if (data.ProviderGuid != GpuAllocationCollector.Provider) return;
            var record = expected[count++];
            Assert.Equal(record.GetProperty("id").GetInt32(), (int)data.ID);
            Assert.Equal(record.GetProperty("version").GetInt32(), (int)data.Version);
            schemas.Add(((int)data.ID, data.Version));
            payload.SetEvent(data);
            foreach (var field in Enum.GetValues<GpuAllocationField>())
            {
                var name = GpuAllocationNamedPayload.Names[(int)field];
                if (data.PayloadIndex(name) < 0) continue;
                var indexed = payload.Get(field);
                Assert.Equal(JsonSerializer.Serialize(data.PayloadByName(name)), JsonSerializer.Serialize(indexed));
                Assert.Equal(record.GetProperty("fields").GetProperty(name).GetRawText(), JsonSerializer.Serialize(indexed));
            }
            if ((int)data.ID == 35)
                Assert.Throws<InvalidDataException>(() => payload.Get(GpuAllocationField.AdapterLuid));
        };
        source.Process();
        Assert.Equal(expected.Length, count);
        Assert.Equal(18, schemas.Count);
        Assert.Contains((35, 3), schemas);
        Assert.Contains((38, 2), schemas);
        Assert.Contains((306, 1), schemas);
    }

    [Fact]
    public void MeasureDynamicAllNamedVersusCachedIndexedPayloadAccess()
    {
        var named = new List<Measurement>();
        var indexed = new List<Measurement>();
        for (var iteration = 0; iteration < 18; iteration++)
        {
            // Alternate order so one arm does not always receive the warm file cache.
            var first = Measure(iteration % 2 == 0);
            var second = Measure(iteration % 2 != 0);
            var old = iteration % 2 == 0 ? second : first;
            var direct = iteration % 2 == 0 ? first : second;
            Assert.Equal(old.Events, direct.Events);
            Assert.Equal(old.LiveEvents, direct.LiveEvents);
            Assert.Equal(old.Errors, direct.Errors);
            Assert.Equal(old.FinalReading, direct.FinalReading);
            Assert.True(old.Events > 1000);
            if (iteration >= 3) { named.Add(old); indexed.Add(direct); }
        }
        static double Median(IEnumerable<double> values) => values.Order().ElementAt(7);
        foreach (var (name, values) in new[] { ("Dynamic.All + PayloadByName", named), ("Dynamic.All + cached PayloadValue", indexed) })
        {
            output.WriteLine($"{name}: {values[0].Events} events/replay; Process+dispatch+decoder median {Median(values.Select(x => x.ProcessNsPerEvent)):F2} ns/event, {Median(values.Select(x => x.ProcessBytesPerEvent)):F2} B/event; {values[0].LiveEvents} live-event handlers+decoder median {Median(values.Select(x => x.HandlerNsPerEvent)):F2} ns/event, {Median(values.Select(x => x.HandlerBytesPerEvent)):F2} B/event.");
        }
        output.WriteLine("Real offline ETL, 18 event/version schemas. Same decoder and event order in both arms; three warmups, 15 measured replays per arm. Process time includes ETL delivery/decompression and dynamic dispatch; handler time excludes common dispatch. No live ETW session. Cold layout creation is included once per schema in each replay.");
        output.WriteLine("Input: " + MeasurementPath());
    }

    private static Measurement Measure(bool indexed)
    {
        var payload = new GpuAllocationIndexedPayload();
        var ledger = new GpuAllocationLedger();
        var errors = new List<string>();
        var count = 0;
        var liveCount = 0;
        long handlerTicks = 0, handlerBytes = 0;
        var thread = Environment.CurrentManagedThreadId;
        using var source = new ETWTraceEventSource(MeasurementPath(), TraceEventSourceType.FileOnly);
        source.Dynamic.All += data =>
        {
            if (data.ProviderGuid != GpuAllocationCollector.Provider) return;
            if ((int)data.ID is not (33 or 34 or 35 or 36 or 37 or 38 or 39 or 40 or 53 or 54 or 55
                or 74 or 78 or 80 or 110 or 227 or 306 or 307 or 313 or 314 or 391)) return;
            Assert.Equal(thread, Environment.CurrentManagedThreadId);
            var live = (int)data.ID is not (110 or 35 or 38 or 78 or 80 or 227 or 391);
            var beforeBytes = live ? GC.GetAllocatedBytesForCurrentThread() : 0;
            var before = live ? Stopwatch.GetTimestamp() : 0;
            try
            {
                if (indexed)
                {
                    payload.SetEvent(data);
                    GpuAllocationEventDecoder.Apply(ledger, (int)data.ID, payload, data.TimeStamp.ToUniversalTime());
                }
                else GpuAllocationEventDecoder.Apply(ledger, (int)data.ID, data.PayloadByName, data.TimeStamp.ToUniversalTime());
            }
            catch (Exception error) { errors.Add($"{(int)data.ID}/v{data.Version}: {error.GetType().Name}: {error.Message}"); }
            if (live)
            {
                handlerTicks += Stopwatch.GetTimestamp() - before;
                handlerBytes += GC.GetAllocatedBytesForCurrentThread() - beforeBytes;
                liveCount++;
            }
            count++;
        };
        var processBytes = GC.GetAllocatedBytesForCurrentThread();
        var processStarted = Stopwatch.GetTimestamp();
        source.Process();
        var processTicks = Stopwatch.GetTimestamp() - processStarted;
        processBytes = GC.GetAllocatedBytesForCurrentThread() - processBytes;
        string reading;
        try { reading = JsonSerializer.Serialize(ledger.Read(new Dictionary<int, long>())); }
        catch (Exception error) { reading = error.GetType().Name + ": " + error.Message; }
        return new(count, liveCount, processTicks * 1e9 / Stopwatch.Frequency / count, (double)processBytes / count,
            handlerTicks * 1e9 / Stopwatch.Frequency / liveCount, (double)handlerBytes / liveCount, errors.ToArray(), reading);
    }

    private static JsonElement[] RecordedJson()
    {
        using var file = File.OpenRead(Fixture("recorded-payloads.jsonl.gz"));
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip);
        var records = new List<JsonElement>();
        while (reader.ReadLine() is { } line)
        {
            using var document = JsonDocument.Parse(line);
            records.Add(document.RootElement.Clone());
        }
        return records.ToArray();
    }

    private static string Fixture(string name, [CallerFilePath] string source = "")
        => Path.Combine(Path.GetDirectoryName(source)!, "Fixtures/GpuAllocation", name);
    private static string MeasurementPath() => Environment.GetEnvironmentVariable("RESOURCE_MANAGER_GPU_PAYLOAD_ETL")
        ?? Fixture("recorded-payloads.etl.bin");
    private sealed record Measurement(int Events, int LiveEvents, double ProcessNsPerEvent, double ProcessBytesPerEvent,
        double HandlerNsPerEvent, double HandlerBytesPerEvent, string[] Errors, string FinalReading);
}
