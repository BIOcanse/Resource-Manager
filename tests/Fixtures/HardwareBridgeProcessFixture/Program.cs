using System.Text.Json;
using System.Runtime.InteropServices;
using ResourceManager.ReadWrite.Helpers;

// Offline process-lifetime fixture. It has no hardware dependency or hardware operations.
HardwareBridgeReadingsSection? section = null;
string? name = null;
var stapm = 45d;
var igpu = double.TryParse(Environment.GetEnvironmentVariable("RM_BRIDGE_FIXTURE_IGPU_COUNTS"),
    System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var initialIgpu)
    ? initialIgpu : -5d;
void PublishReadings()
{
    HardwareBridgeReading Value(double number, string unit) => new(number, unit,
        HardwareBridgeReadingStatus.Valid, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    section?.Publish(new(Value(stapm,"W"),Value(25,"W"),Value(54,"W"),Value(45,"W"),
        Value(85,"A"),Value(125,"A"),Value(-10,"档"),Value(igpu,"档"),Value(4800,"MHz")));
}
var ignoreEof = false;
var mode = Environment.GetEnvironmentVariable("RM_BRIDGE_FIXTURE_MODE");
if (mode == "runtime-missing")
{
    Console.Error.WriteLine("The required .NET 10 runtime is missing.");
    return;
}
var version = Environment.GetEnvironmentVariable("RM_BRIDGE_FIXTURE_VERSION");
var holdPath = Environment.GetEnvironmentVariable("RM_BRIDGE_FIXTURE_HOLD_FILE");
using var heldFile = holdPath is null ? null : File.Open(holdPath, FileMode.Open, FileAccess.Read, FileShare.Read);
try
{
    while (await Console.In.ReadLineAsync() is { } line)
    {
        using var document = JsonDocument.Parse(line);
        var request = document.RootElement;
        var operation = request.GetProperty("op").GetString();
        if (operation == "describe" && mode == "unsupported")
        {
            Console.WriteLine(JsonSerializer.Serialize(new { id = request.GetProperty("id").GetInt64(), available = false, reason = "fixture-unsupported-hardware" }));
            Console.Out.Flush();
            continue;
        }
        if (operation == "crash") Environment.Exit(13);
        if (operation == "hang") await Task.Delay(Timeout.InfiniteTimeSpan);
        if (operation == "ignore-eof") ignoreEof = true;
        if (request.TryGetProperty("startedEvent", out var eventName))
        {
            using var started = EventWaitHandle.OpenExisting(eventName.GetString()!);
            started.Set();
        }
        if (request.TryGetProperty("delayMilliseconds", out var delay))
            await Task.Delay(Math.Clamp(delay.GetInt32(), 0, 2000));
        if (operation == "invalid-json")
        {
            Console.WriteLine("{invalid");
            Console.Out.Flush();
            continue;
        }
        var marker = request.TryGetProperty("marker", out var suppliedMarker) ? suppliedMarker.Clone() : default(JsonElement?);
        if (operation == "configure-readings")
        {
            name = request.GetProperty("sectionName").GetString()!;
            section = HardwareBridgeReadingsSection.CreateProducer(name);
            PublishReadings();
            if (mode == "closed-readings-section") { section.Dispose(); section = null; }
        }
        if (operation == "set-power-limit")
        {
            stapm = request.GetProperty("value").GetDouble();
            // Match the real helper's serial sample-and-publish-before-ACK contract.
            PublishReadings();
        }
        if (operation == "set-igpu-curve-optimizer")
        {
            igpu = request.GetProperty("value").GetDouble();
            PublishReadings();
        }
        // Deterministic stand-in for a periodic producer sample between commands.
        if (operation == "sample-current-readings") PublishReadings();
        Native.IsProcessInJob(new IntPtr(-1), IntPtr.Zero, out var inJob);
        Console.WriteLine(JsonSerializer.Serialize(new { id=request.GetProperty("id").GetInt64(), ok=true,
            available=true, powerTable=true, curveOptimizer=true, integratedGpuCurveOptimizer=true,
            accepted=true, status="OK", stapmLimitWatts=stapm, integratedGpuCurveOptimizerCounts=igpu,
            sectionName=name, pid=Environment.ProcessId, inJob, marker, version }));
        Console.Out.Flush();
        if (operation == "configure-readings" && mode == "closed-readings-section") break;
    }
    var observed = Environment.GetEnvironmentVariable("RM_BRIDGE_FIXTURE_EOF_OBSERVED");
    if (observed is not null) File.WriteAllText(observed, "eof");
    var release = Environment.GetEnvironmentVariable("RM_BRIDGE_FIXTURE_EOF_RELEASE");
    var deadline = DateTime.UtcNow.AddSeconds(10);
    while (release is not null && !File.Exists(release) && DateTime.UtcNow < deadline) await Task.Delay(10);
    if (ignoreEof) await Task.Delay(Timeout.InfiniteTimeSpan);
}
finally { section?.Dispose(); }
internal static class Native
{
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsProcessInJob(IntPtr process, IntPtr job, [MarshalAs(UnmanagedType.Bool)] out bool result);
}
