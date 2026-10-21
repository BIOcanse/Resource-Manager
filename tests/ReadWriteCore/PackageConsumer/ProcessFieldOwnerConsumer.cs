using ResourceManager.App.Infrastructure.Telemetry.Etw;
using System.Text.Json;
using System.Globalization;
using System.Diagnostics;
using System.Collections.Immutable;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.ProcessFields;
using ResourceManager.ReadWrite.Windows.Processes;

internal static class ProcessFieldOwnerConsumer
{
    // Real Windows readonly process API through the default SDK owner. No ETW fields are collected.
    internal static async Task RunWindowsAsync()
    {
        if (!OperatingSystem.IsWindows()) { Console.WriteLine("Default process owner Windows consumer unavailable on this OS."); return; }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var owner = WindowsProcessFieldOwner.CreateDefault();
        var backend = owner.Backend;
        if (owner.ReadCachedSnapshot() is not null) throw new InvalidOperationException("Construction unexpectedly collected processes.");
        var catalog = await backend.DiscoverAsync(timeout.Token);
        var device = catalog.Devices.FirstOrDefault(device => device.Identity.StartsWith($"windows-process|{Environment.ProcessId}:", StringComparison.Ordinal));
        if (device is null) { Console.WriteLine("Default process owner OS inventory has no confirmed current-process identity; availability not claimed."); return; }
        var fields = catalog.Fields.Where(field => field.Field.DeviceIdentity == device.Identity).ToArray();
        if (fields.Length != 30 || fields.Any(field => field.Write.Supported)) throw new InvalidOperationException("Canonical process directory contract failed.");
        var selected = fields.Where(field => field.Definition.Name is "process_all_start_time_actual" or "process_all_image_path_actual"
            or "process_all_parent_pid_actual" or "process_all_user_sid_actual" or "process_all_cpu_usage_actual").Select(field => field.Field).ToImmutableArray();
        var first = await backend.CollectAsync(selected, timeout.Token);
        await Task.Delay(20, timeout.Token);
        var second = await backend.CollectAsync(selected, timeout.Token);
        foreach (var reading in second)
        {
            var cached = backend.ReadCached(reading.Field);
            if (cached != reading) throw new InvalidOperationException("Direct and cached values use different process current slots.");
            if (reading.SourceId == "windows.process.query" && reading.SampleDurationMilliseconds is not null)
                throw new InvalidOperationException("Identity query falsely received a CPU window.");
        }
        if (first.Length != selected.Length || second.Length != selected.Length) throw new InvalidOperationException("Selected process projection incomplete.");
        Console.WriteLine("Default independent process owner canonical Windows consumer completed; 30-field directory, actual selected identity/CPU capture and shared cached current checked; ETW not started by basic selection.");
    }

    internal static async Task RunIoRatesWindowsAsync()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Actual readonly process IO requires Windows.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var self = Process.GetCurrentProcess();
        var birth = self.StartTime.ToUniversalTime().ToFileTimeUtc().ToString(CultureInfo.InvariantCulture);
        var identity = $"windows-process|{self.Id.ToString(CultureInfo.InvariantCulture)}:{birth}";
        var guard = new NoIoEtwBroker();
        using var owner = WindowsProcessFieldOwner.CreateDefault(borrowedBroker: guard);
        using var http = new HttpClient();
        var core = new ReadWriteCore(owner.Backend, new ResourceManager.ReadWrite.Dependencies.DependencyActions(http, []));
        Require(owner.ReadCachedSnapshot() is null && core.Catalog.Fields.IsEmpty, "IO construction/catalog are passive");

        var beginLower = Stopwatch.GetTimestamp();
        var first = await owner.CollectAsync(IoNames(), timeout.Token); // Explicit IO-only inventory; no broad Discover.
        var beginUpper = Stopwatch.GetTimestamp();
        var before = IoRow(first, self.Id, birth);
        VerifyIoWarmup(first, before);
        var fields = SelectIo(core, identity);
        var original = owner.ReadCachedSnapshot();
        foreach (var field in fields) _ = core.ReadCached(field);
        Require(owner.ReadCachedSnapshot()!.CapturedAt == original!.CapturedAt, "cached IO read does not advance capture");

        await Task.Delay(25, timeout.Token);
        var endLower = Stopwatch.GetTimestamp();
        var delivered = await core.CollectAsync(fields, timeout.Token);
        var endUpper = Stopwatch.GetTimestamp();
        var second = owner.ReadCachedSnapshot()!;
        var after = IoRow(second, self.Id, birth);
        VerifyIoPair(before, after, beginLower, beginUpper, endLower, endUpper);
        VerifyIoCurrent(core, owner, fields, after);
        foreach (var reading in delivered)
            Require(reading == core.ReadCached(reading.Field), "direct field collection and Current are identical");
        Require(guard.Subscriptions == 0, "no ETW subscription was created");
        Console.WriteLine("[PROCESS-IO-RW-ACTUAL-READONLY] exact-current-PID/FILETIME; raw6/rate6; accepted-query-window; same-current; JSON; zero-valid; no-generated-IO/no-ETW");
    }

    private static readonly (string Raw, string Rate, string RateUnit)[] IoPairs =
    [
        ("process_all_io_read_operations_actual", "process_all_io_read_operations_rate_actual", "ops/s"),
        ("process_all_io_write_operations_actual", "process_all_io_write_operations_rate_actual", "ops/s"),
        ("process_all_io_other_operations_actual", "process_all_io_other_operations_rate_actual", "ops/s"),
        ("process_all_io_read_bytes_actual", "process_all_io_read_bytes_rate_actual", "B/s"),
        ("process_all_io_write_bytes_actual", "process_all_io_write_bytes_rate_actual", "B/s"),
        ("process_all_io_other_bytes_actual", "process_all_io_other_bytes_rate_actual", "B/s")
    ];
    private static string[] IoNames() => IoPairs.SelectMany(static pair => new[] { pair.Raw, pair.Rate }).ToArray();

    private static ProcessMetricFieldValues IoRow(ProcessMetricFieldSnapshot snapshot, int pid, string creation)
    {
        var row = snapshot.FindProcess(pid, creation)
            ?? throw new InvalidDataException("Actual IO current-process PID/creation identity is unavailable.");
        Require(row.ProcessId == pid && row.CreationWindowsFileTime100Ns == creation
            && row.PhysicalDisk is null && row.GpuEngine is null && row.LogicalProcessor is null,
            "IO uses the exact ordinary process creation without an ETW/GPU/CPU scope suffix");
        foreach (var pair in IoPairs) _ = RawIo(row, pair.Raw);
        return row;
    }
    private static ulong RawIo(ProcessMetricFieldValues row, string name)
    {
        var value = row.Values.Single(item => item.Field == name);
        if (value.IntegerValue is not { } text || !ulong.TryParse(text, NumberStyles.None,
            CultureInfo.InvariantCulture, out var integer))
            throw new InvalidDataException("Actual general IO raw counter is unavailable or not UInt64: " + name);
        return integer;
    }
    private static void VerifyIoWarmup(ProcessMetricFieldSnapshot snapshot, ProcessMetricFieldValues row)
    {
        Require(row.IoInterval is null && IoPairs.All(pair => row.Values.Single(value => value.Field == pair.Rate).NumericValue is null),
            "the first accepted IO sample has raw6, unavailable rate6 and no invented interval");
        Require(snapshot.WindowMilliseconds is null && snapshot.Sources.All(static source => source.WindowMilliseconds is null),
            "CPU/ETW/source-level duration is not borrowed for the IO query window");
    }
    private static void VerifyIoPair(ProcessMetricFieldValues before, ProcessMetricFieldValues after,
        long beginLower, long beginUpper, long endLower, long endUpper)
    {
        Require(before.ProcessId == after.ProcessId
            && before.CreationWindowsFileTime100Ns == after.CreationWindowsFileTime100Ns,
            "integer differences never cross process creation identity");
        var interval = after.IoInterval ?? throw new InvalidDataException("Actual second IO sample has no typed accepted-query interval.");
        Require(interval.BeginCounter >= beginLower && interval.BeginCounter <= beginUpper
            && interval.EndCounter >= endLower && interval.EndCounter <= endUpper
            && interval.BeginCounter >= 0 && interval.EndCounter > interval.BeginCounter
            && interval.CounterFrequency == Stopwatch.Frequency && interval.CounterFrequency > 0,
            "IO endpoints belong to the two accepted queries and use the actual monotonic counter frequency");
        var elapsed = interval.EndCounter - interval.BeginCounter;
        foreach (var pair in IoPairs)
        {
            var previous = RawIo(before, pair.Raw);
            var current = RawIo(after, pair.Raw);
            Require(current >= previous, "this live current-process counter must not silently rewind");
            var expected = (double)(current - previous) * interval.CounterFrequency / elapsed;
            var value = after.Values.Single(item => item.Field == pair.Rate).NumericValue;
            Require(value is { } actual && double.IsFinite(actual) && actual >= 0
                && Math.Abs(actual - expected) <= Math.Max(1e-9, Math.Abs(expected) * 1e-12),
                "each rate equals its own unsigned integer delta over the real per-identity window; zero is legal");
        }
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var json = JsonSerializer.SerializeToElement(after, options).GetProperty("ioInterval");
        Require(json.GetProperty("beginCounter").GetString() == interval.BeginCounter.ToString(CultureInfo.InvariantCulture)
            && json.GetProperty("endCounter").GetString() == interval.EndCounter.ToString(CultureInfo.InvariantCulture)
            && json.GetProperty("counterFrequency").GetString() == interval.CounterFrequency.ToString(CultureInfo.InvariantCulture),
            "all three exact interval integers are JSON strings");
        var restored = JsonSerializer.Deserialize<ProcessMetricFieldValues>(JsonSerializer.Serialize(after, options), options)!;
        Require(restored.IoInterval == interval, "public IoInterval JSON round trip is lossless");
    }
    private static void VerifyIoCurrent(ReadWriteCore core, WindowsProcessFieldOwner owner,
        ImmutableArray<FieldReference> fields, ProcessMetricFieldValues row)
    {
        var snapshot = owner.ReadCachedSnapshot() ?? throw new InvalidDataException("Original process Current is absent.");
        var current = IoRow(snapshot, row.ProcessId, row.CreationWindowsFileTime100Ns!);
        Require(current.IoInterval == row.IoInterval, "typed interval belongs to the original Current");
        Require(snapshot.WindowMilliseconds is null && snapshot.Sources.All(static source => source.WindowMilliseconds is null),
            "the general IO window remains per process, not source/global");
        foreach (var pair in IoPairs)
        {
            var rawField = fields.Single(field => field.FieldName == pair.Raw);
            var rateField = fields.Single(field => field.FieldName == pair.Rate);
            var raw = core.ReadCached(rawField);
            var rate = core.ReadCached(rateField);
            Require(raw is { Status: FieldObservationStatus.Current, SampleDurationMilliseconds: null }
                && raw.Value?.Integer == RawIo(row, pair.Raw).ToString(CultureInfo.InvariantCulture)
                && raw.SourceId == "windows.process.query", "raw IO is current, exact and has no rate window");
            var numeric = row.Values.Single(value => value.Field == pair.Rate).NumericValue;
            Require(rate.Status == FieldObservationStatus.Current && rate.Value?.Number == numeric
                && rate.SourceId == "windows.process.io-interval"
                && rate.SampleDurationMilliseconds == row.IoInterval!.WindowMilliseconds,
                "rate IO current shares the exact original per-process duration and values");
            Require(RawIo(current, pair.Raw) == RawIo(row, pair.Raw)
                && current.Values.Single(value => value.Field == pair.Rate).NumericValue == numeric,
                "public field reads and original typed snapshot agree");
        }
    }
    private static ImmutableArray<FieldReference> SelectIo(ReadWriteCore core, string identity)
    {
        var fields = IoNames().Select(name => new FieldReference(identity, name)).ToImmutableArray();
        foreach (var pair in IoPairs)
        {
            foreach (var name in new[] { pair.Raw, pair.Rate })
            {
                var field = fields.Single(item => item.FieldName == name);
                var capability = core.Query(field);
                Require(capability.Read.Supported && !capability.Write.Supported
                    && capability.Read.Dependencies.IsEmpty && core.Catalog.Fields.Any(item => item.Field == field),
                    "all twelve exact process IO fields are public read-only with no ETW dependency");
                Require(capability.Definition.ValueKind == (name == pair.Raw ? "unsigned64" : "float64")
                    && capability.Definition.Unit == (name == pair.Rate ? pair.RateUnit
                        : pair.RateUnit == "B/s" ? "B" : "count"),
                    "operation and byte cumulative/rate units remain distinct");
                var definition = ProcessMetricFieldRegistry.Find(name)!;
                Require(definition.ReadMask == ProcessMetricReadMask.Io
                    && definition.DatasetId == ProcessMetricDatasetIds.BasicMetrics,
                    "the selection requests general IO only, without CPU, memory, GPU or ETW");
            }
        }
        return fields;
    }
    // Existing public broker substitution makes an accidental ETW request fail before any live session exists.
    private sealed class NoIoEtwBroker : IKernelEtwSessionBroker
    {
        internal int Subscriptions;
        public IKernelEtwSubscription Subscribe(KernelEtwSubscriptionRequest request)
        {
            Subscriptions++;
            throw new InvalidOperationException("General IO must not request live ETW.");
        }
        public KernelEtwSessionSnapshot GetSnapshot() => new("Stopped", "IO-only consumer", 0, default, default, 0, 0);
    }

    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidDataException(message); }
}
