using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text.Json;
using ResourceManager.App.Infrastructure.Telemetry.Etw;
using Xunit.Abstractions;

namespace Resource_Manager_APP.Tests;

public sealed class GpuAllocationRecordedDecodingTests(ITestOutputHelper output)
{
    [Fact]
    public void CompareOneRecordedRundownWithOneSecondOfRecordedLiveEvents()
    {
        var records = Load();
        var rundown = records.Where(item => item.Kind == "rundown").ToArray();
        var live = records.Where(item => item.Kind == "live").ToArray();
        Assert.NotEmpty(rundown);
        Assert.NotEmpty(live);
        var births = records.SelectMany(item => item.Fields.Where(pair => pair.Key == "hProcessId"))
            .Select(pair => Convert.ToInt32(pair.Value)).Distinct().ToDictionary(pid => pid, _ => 1L);
        var complete = new GpuAllocationLedger();
        Apply(complete, rundown);
        Assert.NotEmpty(complete.Read(births).Adapters);

        var rundownMs = new List<double>();
        var liveMs = new List<double>();
        var rundownBytes = new List<long>();
        var liveBytes = new List<long>();
        for (var iteration = 0; iteration < 18; iteration++)
        {
            var beforeBytes = GC.GetAllocatedBytesForCurrentThread();
            var before = Stopwatch.GetTimestamp();
            var fresh = new GpuAllocationLedger();
            Apply(fresh, rundown);
            var elapsed = Stopwatch.GetElapsedTime(before).TotalMilliseconds;
            var allocated = GC.GetAllocatedBytesForCurrentThread() - beforeBytes;
            if (iteration >= 3) { rundownMs.Add(elapsed); rundownBytes.Add(allocated); }
            Assert.NotEmpty(fresh.Read(births).Adapters);

            var continuous = new GpuAllocationLedger();
            Apply(continuous, rundown); // continuous mode's existing state, outside measurement
            beforeBytes = GC.GetAllocatedBytesForCurrentThread();
            before = Stopwatch.GetTimestamp();
            Apply(continuous, live);
            elapsed = Stopwatch.GetElapsedTime(before).TotalMilliseconds;
            allocated = GC.GetAllocatedBytesForCurrentThread() - beforeBytes;
            if (iteration >= 3) { liveMs.Add(elapsed); liveBytes.Add(allocated); }
        }
        rundownMs.Sort(); liveMs.Sort(); rundownBytes.Sort(); liveBytes.Sort();
        output.WriteLine($"Recorded rundown: {rundown.Length} events, median {rundownMs[7]:F4} ms, {rundownBytes[7]} allocated bytes.");
        output.WriteLine($"Recorded live second: {live.Length} events, median {liveMs[7]:F4} ms, {liveBytes[7]} allocated bytes.");
        output.WriteLine($"Rundown/live-second decoding ratio: {rundownMs[7] / liveMs[7]:F3}; amortized at 30 s: {rundownMs[7] / 30:F4} ms/s; at 5 s: {rundownMs[7] / 5:F4} ms/s.");
        output.WriteLine("15 measured iterations after 3 warmups. JSON parsing/decompression, ETW dynamic parser, native rundown latency and snapshot projection are excluded.");
    }

    private static void Apply(GpuAllocationLedger ledger, IEnumerable<RecordedEvent> records)
    {
        foreach (var item in records) GpuAllocationEventDecoder.Apply(ledger, item.Id, item.Payload, item.At);
    }

    private static RecordedEvent[] Load([CallerFilePath] string source = "")
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
