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
    // GpuAllocationEventDecoder's DxgKrnl event IDs.
    private static readonly int[] DecodedIds = [33, 34, 35, 36, 37, 38, 39, 40, 53, 54, 55, 74, 78, 80, 110, 227, 306, 307, 313, 314, 391];

    [Fact]
    public void FixedLayoutsMatchEveryRecordedFieldAcrossEventIdsAndVersions()
    {
        Assert.Equal(DecodedIds, LayoutIds(GpuAllocationCollector.Provider));
        Assert.Equal(new[] { 2, 3 }, LayoutIds(GpuAllocationCollector.BoundaryProvider));
        var expected = RecordedJson();
        var count = 0;
        var compared = 0;
        var schemas = new HashSet<(int Id, int Version)>();
        var payload = new GpuAllocationFixedPayload();
        using var source = new ETWTraceEventSource(Fixture("recorded-payloads.etl.bin"), TraceEventSourceType.FileOnly);
        source.Dynamic.All += data =>
        {
            if (data.ProviderGuid != GpuAllocationCollector.Provider) return;
            var record = expected[count++];
            Assert.Equal(record.GetProperty("id").GetInt32(), (int)data.ID);
            Assert.Equal(record.GetProperty("version").GetInt32(), (int)data.Version);
            schemas.Add(((int)data.ID, data.Version));
            var status = payload.SetEvent(data);
            Assert.Equal(GpuAllocationFixedPayload.Catalog.Find(data, out var layout), status);
            Assert.Equal(DecodedIds.Contains((int)data.ID) ? EtwFixedLayoutStatus.Decoded : EtwFixedLayoutStatus.NotConsumed, status);
            if (status != EtwFixedLayoutStatus.Decoded) return;
            foreach (var field in Enum.GetValues<GpuAllocationField>())
            {
                var name = GpuAllocationNamedPayload.Names[(int)field];
                if (!layout.Has((int)field))
                {
                    Assert.Throws<InvalidDataException>(() => payload.Get(field));
                    continue;
                }
                Assert.True(data.PayloadIndex(name) >= 0, $"{(int)data.ID}/v{data.Version} has no field {name}.");
                var named = data.PayloadByName(name);
                var fixedValue = payload.Get(field);
                Assert.Equal(named.GetType(), fixedValue.GetType());
                Assert.Equal(JsonSerializer.Serialize(named), JsonSerializer.Serialize(fixedValue));
                Assert.Equal(record.GetProperty("fields").GetProperty(name).GetRawText(), JsonSerializer.Serialize(fixedValue));
                compared++;
            }
            if ((int)data.ID == 35)
                Assert.Throws<InvalidDataException>(() => payload.Get(GpuAllocationField.AdapterLuid));
        };
        source.Process();
        Assert.Equal(expected.Length, count);
        Assert.True(compared > 0);
        Assert.Equal(18, schemas.Count);
        Assert.Contains((35, 3), schemas);
        Assert.Contains((38, 2), schemas);
        Assert.Contains((306, 1), schemas);
        output.WriteLine($"{count} recorded events, {compared} field values identical to the manifest parser and the recording.");
    }

    [Fact]
    public void FixedLayoutReplayMatchesTheManifestParserReplay()
    {
        var named = new List<Measurement>();
        var fixedLayout = new List<Measurement>();
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
            if (iteration >= 3) { named.Add(old); fixedLayout.Add(direct); }
        }
        static double Median(IEnumerable<double> values) => values.Order().ElementAt(7);
        foreach (var (name, values) in new[] { ("Dynamic.All + PayloadByName", named), ("UnhandledEvents + fixed layout", fixedLayout) })
        {
            output.WriteLine($"{name}: {values[0].Events} events/replay; Process+dispatch+decoder median {Median(values.Select(x => x.ProcessNsPerEvent)):F2} ns/event, {Median(values.Select(x => x.ProcessBytesPerEvent)):F2} B/event; {values[0].LiveEvents} live-event handlers+decoder median {Median(values.Select(x => x.HandlerNsPerEvent)):F2} ns/event, {Median(values.Select(x => x.HandlerBytesPerEvent)):F2} B/event.");
        }
        output.WriteLine("Real offline ETL, 18 event/version schemas. Same decoder and event order in both arms; three warmups, 15 measured replays per arm. Process time includes ETL delivery/decompression and dispatch (manifest lookup in the named arm); handler time excludes common dispatch. No live ETW session. Managed bytes only: TDH's native manifest heap is not counted.");
        output.WriteLine("Input: " + MeasurementPath());
    }

    private static Measurement Measure(bool fixedLayout)
    {
        var payload = new GpuAllocationFixedPayload();
        var ledger = new GpuAllocationLedger();
        var errors = new List<string>();
        var count = 0;
        var liveCount = 0;
        long handlerTicks = 0, handlerBytes = 0;
        var thread = Environment.CurrentManagedThreadId;
        using var source = new ETWTraceEventSource(MeasurementPath(), TraceEventSourceType.FileOnly);
        Action<TraceEvent> handle = data =>
        {
            if (data.ProviderGuid != GpuAllocationCollector.Provider || !DecodedIds.Contains((int)data.ID)) return;
            Assert.Equal(thread, Environment.CurrentManagedThreadId);
            var live = (int)data.ID is not (110 or 35 or 38 or 78 or 80 or 227 or 391);
            var beforeBytes = live ? GC.GetAllocatedBytesForCurrentThread() : 0;
            var before = live ? Stopwatch.GetTimestamp() : 0;
            try
            {
                if (fixedLayout)
                {
                    var status = payload.SetEvent(data);
                    if (status != EtwFixedLayoutStatus.Decoded) throw new InvalidDataException($"Fixed layout: {status}.");
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
        // The fixed-layout arm subscribes as the production session does: no parser, so every
        // event arrives undecoded on UnhandledEvents.
        if (fixedLayout) source.UnhandledEvents += handle;
        else source.Dynamic.All += handle;
        var processBytes = GC.GetAllocatedBytesForCurrentThread();
        var processStarted = Stopwatch.GetTimestamp();
        source.Process();
        var processTicks = Stopwatch.GetTimestamp() - processStarted;
        processBytes = GC.GetAllocatedBytesForCurrentThread() - processBytes;
        if (fixedLayout) EtwFixedLayoutTests.AssertNoManifestParser(source);
        string reading;
        try { reading = JsonSerializer.Serialize(ledger.Read(new Dictionary<int, long>())); }
        catch (Exception error) { reading = error.GetType().Name + ": " + error.Message; }
        return new(count, liveCount, processTicks * 1e9 / Stopwatch.Frequency / count, (double)processBytes / count,
            handlerTicks * 1e9 / Stopwatch.Frequency / liveCount, (double)handlerBytes / liveCount, errors.ToArray(), reading);
    }

    private static int[] LayoutIds(Guid provider) => GpuAllocationFixedPayload.Catalog.Layouts
        .Where(item => item.Provider == provider).Select(item => item.Id).Distinct().Order().ToArray();

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
