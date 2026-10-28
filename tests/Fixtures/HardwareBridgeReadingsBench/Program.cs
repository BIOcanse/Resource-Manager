using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using ResourceManager.ReadWrite.Helpers;

// A synthetic process only: this executable has no hardware dependencies.
if (args is ["--fake-helper"])
{
    HardwareBridgeReadingsSection? producer = null;
    using var input = new StreamReader(Console.OpenStandardInput());
    try
    {
        while (await input.ReadLineAsync() is { } line)
        {
            using var request = JsonDocument.Parse(line);
            var root = request.RootElement;
            var operation = root.GetProperty("op").GetString();
            if (operation == "configure-readings" && producer is null)
            {
                producer = HardwareBridgeReadingsSection.CreateProducer(root.GetProperty("sectionName").GetString()!);
                producer.Publish(Sample());
            }
            var result = new { id = root.GetProperty("id").GetInt64(), ok = true,
                stapmLimitWatts = 45d, stapmValueWatts = 25d, fastLimitWatts = 54d,
                slowLimitWatts = 45d, tdcLimitAmps = 85d, edcLimitAmps = 125d,
                curveOptimizerCounts = -10d, integratedGpuCurveOptimizerCounts = -5d,
                maxBoostFrequencyMhz = 4800d };
            Console.WriteLine(JsonSerializer.Serialize(result));
            Console.Out.Flush();
        }
    }
    finally { producer?.Dispose(); }
    return;
}

VerifySection();
var start = new ProcessStartInfo("dotnet")
{
    RedirectStandardInput = true, RedirectStandardOutput = true,
    RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true
};
start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
start.ArgumentList.Add("--fake-helper");
var startup = Stopwatch.StartNew();
using var helper = Process.Start(start)!;
using var processTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
var diagnostics = helper.StandardError.ReadToEndAsync(processTimeout.Token);
var transportGate = new object();
long requestId = 0;
double JsonRead()
{
    lock (transportGate)
    {
        helper.StandardInput.WriteLine(JsonSerializer.Serialize(new { id = ++requestId, op = "read" }));
        helper.StandardInput.Flush();
        var line = helper.StandardOutput.ReadLine() ?? throw new IOException("Fake helper exited.");
        using var response = JsonDocument.Parse(line);
        return response.RootElement.GetProperty("stapmLimitWatts").GetDouble();
    }
}
var sectionName = $"Local\\ResourceManager.HardwareBridge.Bench.{Guid.NewGuid():N}";
helper.StandardInput.WriteLine(JsonSerializer.Serialize(new { id = ++requestId,
    op = "configure-readings", sectionName, intervalMilliseconds = 1000 }));
helper.StandardInput.Flush();
_ = await helper.StandardOutput.ReadLineAsync(processTimeout.Token)
    ?? throw new IOException("No fake helper acknowledgement.");
using var section = HardwareBridgeReadingsSection.OpenReader(sectionName);
startup.Stop();
for (var warmup = 0; warmup < 2000; warmup++) _ = section.Read();
for (var warmup = 0; warmup < 100; warmup++) _ = JsonRead();
var results = new List<object>();
foreach (var readers in new[] { 1, 2, 8 })
{
    results.Add(Measure("shared-memory", readers, 10000, () => section.Read()!.Value.StapmLimitWatts!.Value));
    results.Add(Measure("json-request-response", readers, 1000, JsonRead));
}
helper.StandardInput.Close();
await helper.WaitForExitAsync(processTimeout.Token);
if (helper.ExitCode != 0) throw new Exception(await diagnostics);
Console.WriteLine(JsonSerializer.Serialize(new { fakeHelperOnly = true, runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
    architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
    startupThroughFirstPublicationMilliseconds = startup.Elapsed.TotalMilliseconds,
    sharedWarmupReads = 2000, jsonWarmupReads = 100, cpuScope = "benchmark parent process", results },
    new JsonSerializerOptions { WriteIndented = true }));

static object Measure(string path, int readerCount, int iterations, Func<double> read)
{
    using var ready = new CountdownEvent(readerCount);
    using var release = new ManualResetEventSlim();
    var latency = new long[readerCount][];
    var allocated = new long[readerCount];
    var checksums = new double[readerCount];
    var failures = new Exception?[readerCount];
    var threads = Enumerable.Range(0, readerCount).Select(index => new Thread(() =>
    {
        var samples = latency[index] = new long[iterations];
        ready.Signal(); release.Wait();
        try
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            double checksum = 0;
            for (var iteration = 0; iteration < iterations; iteration++)
            {
                var started = Stopwatch.GetTimestamp();
                checksum += read();
                samples[iteration] = Stopwatch.GetTimestamp() - started;
            }
            allocated[index] = GC.GetAllocatedBytesForCurrentThread() - before;
            checksums[index] = checksum;
        }
        catch (Exception error) { failures[index] = error; }
    })).ToArray();
    foreach (var thread in threads) thread.Start();
    ready.Wait();
    using var process = Process.GetCurrentProcess();
    var cpuBefore = process.TotalProcessorTime;
    var stopwatch = Stopwatch.StartNew();
    release.Set();
    foreach (var thread in threads) thread.Join();
    stopwatch.Stop();
    var cpuMilliseconds = (process.TotalProcessorTime - cpuBefore).TotalMilliseconds;
    foreach (var error in failures) if (error is not null) throw error;
    if (checksums.Any(checksum => checksum != 45d * iterations)) throw new Exception("Unexpected sample value.");
    var all = latency.SelectMany(samples => samples).Order().ToArray();
    double Nanoseconds(long ticks) => ticks * (1_000_000_000d / Stopwatch.Frequency);
    return new { path, readerCount, operations = readerCount * iterations,
        elapsedMilliseconds = stopwatch.Elapsed.TotalMilliseconds, cpuMilliseconds,
        allocatedBytesPerRead = allocated.Sum() / (double)all.Length,
        p50Nanoseconds = Nanoseconds(all[(int)(all.Length * .50)]),
        p95Nanoseconds = Nanoseconds(all[(int)(all.Length * .95)]),
        p99Nanoseconds = Nanoseconds(all[(int)(all.Length * .99)]) };
}

static HardwareBridgeReadingsSnapshot Sample()
{
    var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    HardwareBridgeReading Reading(double value, string unit) =>
        new(value, unit, HardwareBridgeReadingStatus.Valid, timestamp);
    return new(Reading(45, "W"), Reading(25, "W"),
        Reading(54, "W"), Reading(45, "W"),
        Reading(85, "A"), Reading(125, "A"),
        Reading(-10, "档"), Reading(-5, "档"),
        Reading(4800, "MHz"));
}

static void VerifySection()
{
    static void Check(bool condition) { if (!condition) throw new Exception("Offline section contract failed."); }
    var name = $"Local\\ResourceManager.HardwareBridge.SectionCheck.{Guid.NewGuid():N}";
    using var producer = HardwareBridgeReadingsSection.CreateProducer(name);
    using var reader = HardwareBridgeReadingsSection.OpenReader(name);
    Check(reader.Read() is null);
    producer.Publish(Sample());
    var first = reader.Read()!.Value;
    Check(first.StapmLimitWatts == 45 && first.GetReading("tdcLimitAmps").Unit == "A");
    producer.Publish(Sample() with { StapmLimit = new(null, "W", HardwareBridgeReadingStatus.Failed, 1700000001000) });
    Check(reader.Read()!.Value.StapmLimit is { Value: null, Status: HardwareBridgeReadingStatus.Failed,
        TimestampUnixMilliseconds: 1700000001000 });
    producer.Publish(Sample() with { StapmLimit = new(double.NaN, "W", HardwareBridgeReadingStatus.Valid, 1700000001000) });
    Check(reader.Read()!.Value.StapmLimitWatts is null);
    var initial = Sample();
    producer.Publish(initial with { StapmLimit = initial.StapmValue });
    using var started = new CountdownEvent(4);
    using var release = new ManualResetEventSlim();
    var tasks = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
    {
        using var concurrent = HardwareBridgeReadingsSection.OpenReader(name);
        started.Signal(); release.Wait();
        for (var count = 0; count < 10000; count++)
            if (concurrent.Read() is { } snapshot) Check(snapshot.StapmLimitWatts == snapshot.StapmValueWatts);
    })).ToArray();
    started.Wait(); release.Set();
    for (var count = 1; count < 10000; count++)
    {
        var reading = new HardwareBridgeReading(count, "W", HardwareBridgeReadingStatus.Valid, 1700000000000 + count);
        producer.Publish(initial with { StapmLimit = reading, StapmValue = reading });
    }
    Task.WaitAll(tasks);
}
