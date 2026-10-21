using ResourceManager.App.Infrastructure.Telemetry.Etw;
using ResourceManager.ReadWrite.ProcessFields;
using System.Text.Json;
using System.Globalization;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Dependencies;
using ResourceManager.ReadWrite.Native;
using ResourceManager.ReadWrite.Windows.Metrics;
using ResourceManager.ReadWrite.Windows.Processes;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.Subscription;
using ResourceManager.Subscription.Native;

internal static class DefaultOwnerSubscriptionConsumer
{
    internal static async Task RunWindowsAsync()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        await RunIoRatesWindowsAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        using var http = new HttpClient();
        var dependencies = new DependencyActions(http, []);
        using var processes = WindowsProcessFieldOwner.CreateDefault();
        var processCore = new ReadWriteCore(processes.Backend, dependencies);
        var processCatalog = await processCore.DiscoverAsync(timeout.Token);
        using var self = Process.GetCurrentProcess();
        var identity = $"windows-process|{self.Id}:{self.StartTime.ToUniversalTime().ToFileTimeUtc()}";
        var processFields = processCatalog.GetFields(identity).Where(field => field.Definition.Name is
            "process_all_image_path_actual" or "process_all_cpu_usage_actual").Select(field => field.Field).ToImmutableArray();
        Require(processFields.Length == 2, "Actual current process PID/creation identity has image and CPU fields.");
        await RunOneAsync("actual-process", processCore, processes.BorrowSubscriptionCapture(), processFields, timeout.Token);

        using var metrics = WindowsHardwareMetricOwner.CreateDefault(modeProvider: static source => source is
            NativeMetricSnapshotSources.WindowsCpu or NativeMetricSnapshotSources.WindowsMemory
            or NativeMetricSnapshotSources.WindowsVirtualMemory ? NativeMetricSnapshotZoneMode.Normal : NativeMetricSnapshotZoneMode.Freeze);
        var metricCore = new ReadWriteCore(metrics.CreateBackend(), dependencies);
        ReadWriteCatalog metricCatalog;
        try { metricCatalog = await metricCore.DiscoverAsync(timeout.Token); }
        catch (InvalidOperationException error) when (error.Message == "The required exact GPU topology is unavailable.")
        {
            Console.WriteLine($"Default subscription metric unavailable: {error.Message}; actual memory/CPU not claimed.");
            return;
        }
        var metricFields = metricCatalog.Fields.Where(field => field.Definition.Name is
            "cpu_all_usage_percent" or "memory_all_physical_total_bytes").Select(field => field.Field).ToImmutableArray();
        Require(metricFields.Length == 2, "Actual default Windows CPU and memory canonical fields.");
        await RunOneAsync("actual-metric", metricCore, metrics.BorrowSubscriptionCapture(), metricFields, timeout.Token);
    }

    private static async Task RunOneAsync(string name, ReadWriteCore core, IReadWriteCollectionBorrow borrow,
        ImmutableArray<FieldReference> fields, CancellationToken cancellationToken)
    {
        foreach (var field in fields) Require(core.Query(field).Read.Supported, "Explicit selected-source read support.");
        var settings = new SubscriptionRoleSettings(1, 1000, 1, 3000, 1000, 1000);
        var configuration = new NativeSamplingSubscriptionConfiguration
        {
            AbiVersion = NativeSamplingSubscriptionAbi.Version,
            StructSize = (uint)Unsafe.SizeOf<NativeSamplingSubscriptionConfiguration>(), Generation = 1,
            MaximumSourceCount = 32, MaximumItemCount = 32, MaximumMembershipCount = 128,
            MaximumDueItemCount = 32, MaximumSourceViewCount = 32, MaximumExpiredSourceCount = 32,
            DefaultIntervalMilliseconds = 1000, MinimumIntervalMilliseconds = 1,
            ActiveTtlMilliseconds = 3000, MaximumFutureSkewMilliseconds = 1000
        };
        await using var subscriptions = new NativeFieldSubscriptionComposition(borrow, settings, in configuration,
            failure => Console.WriteLine($"{name} worker failure: {failure.Stage}; {failure.Error.Message}"), TimeSpan.FromMilliseconds(250));
        await subscriptions.StartAsync(cancellationToken);
        var publicFields = new FieldSubscriptionCore(core, subscriptions);
        await using (var stream = publicFields.SubscribeAsync(name, fields, TimeSpan.FromMilliseconds(250), cancellationToken)
            .GetAsyncEnumerator(cancellationToken))
        {
            ImmutableArray<FieldReading> observed = [];
            for (var attempt = 0; attempt < 8; attempt++)
            {
                Require(await stream.MoveNextAsync(), "Native demand published an observation.");
                observed = stream.Current;
                Require(observed.Select(static reading => reading.Field).SequenceEqual(fields), "Exact ordered selected identities.");
                foreach (var reading in observed)
                    FieldObservationConsumerAssertions.RequireOriginalOwnerObservation(reading, core.ReadCached(reading.Field));
                if (observed.All(static reading => reading.Status == FieldObservationStatus.Current)) break;
            }
            foreach (var reading in observed)
            {
                if (reading.Status != FieldObservationStatus.Current)
                {
                    Require(reading.Value is null, "Unavailable/warming values are not numeric zero.");
                    Console.WriteLine($"{name}: field={reading.Field.FieldName}; status={reading.Status}; actual value not claimed.");
                }
                else
                {
                    Require(reading.Value is not null && reading.ObservedAt.HasValue, "Actual source observation and timestamp.");
                    if (reading.Field.FieldName == "memory_all_physical_total_bytes")
                        Require(reading.Value!.Number is > 0, "Actual physical memory is positive.");
                    Console.WriteLine($"{name}: identity={reading.Field.DeviceIdentity}; field={reading.Field.FieldName}; status={reading.Status}; number={reading.Value!.Number}; text={reading.Value.Text}; same-current=yes.");
                }
            }
            await subscriptions.StopAsync();
            Require(!await stream.MoveNextAsync(), "Stop retires the old stream.");
        }
        var direct = await core.CollectAsync(fields, cancellationToken);
        Require(direct.Length == fields.Length, "External owner still supports direct collection after subscription stop.");
        Console.WriteLine($"{name}: explicit native subscription stopped; direct owner still available; ETW/control/driver writes not requested.");
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
        var core = new ReadWriteCore(owner.Backend, new DependencyActions(http, []));
        Require(owner.ReadCachedSnapshot() is null && core.Catalog.Fields.IsEmpty, "IO construction/catalog are passive");
        var initial = await owner.CollectAsync(IoNames(), timeout.Token);
        VerifyIoWarmup(initial, IoRow(initial, self.Id, birth));
        var fields = SelectIo(core, identity);
        var failures = new System.Collections.Concurrent.ConcurrentQueue<SubscriptionWorkerFailure>();
        var settings = new SubscriptionRoleSettings(1, 1000, 1, 3000, 1000, 1000);
        var configuration = new NativeSamplingSubscriptionConfiguration {
            AbiVersion = NativeSamplingSubscriptionAbi.Version,
            StructSize = (uint)Unsafe.SizeOf<NativeSamplingSubscriptionConfiguration>(), Generation = 1,
            MaximumSourceCount = 16, MaximumItemCount = 16, MaximumMembershipCount = 64,
            MaximumDueItemCount = 16, MaximumSourceViewCount = 16, MaximumExpiredSourceCount = 16,
            DefaultIntervalMilliseconds = 1000, MinimumIntervalMilliseconds = 1,
            ActiveTtlMilliseconds = 3000, MaximumFutureSkewMilliseconds = 1000 };
        await using (var composition = new NativeFieldSubscriptionComposition(owner.BorrowSubscriptionCapture(),
            settings, in configuration, failure => failures.Enqueue(failure), TimeSpan.FromMilliseconds(250)))
        {
            await composition.StartAsync(timeout.Token);
            var subscriptions = new FieldSubscriptionCore(core, composition);
            await using var stream = subscriptions.SubscribeAsync("actual-general-io", fields,
                TimeSpan.FromMilliseconds(250), timeout.Token).GetAsyncEnumerator(timeout.Token);
            ImmutableArray<FieldReading> delivered = [];
            for (var attempt = 0; attempt < 8; attempt++)
            {
                Require(await stream.MoveNextAsync(), "native subscription delivers selected IO fields");
                delivered = stream.Current;
                Require(delivered.Select(static reading => reading.Field).SequenceEqual(fields), "exact ordered IO field identity");
                foreach (var reading in delivered)
                    FieldObservationConsumerAssertions.RequireOriginalOwnerObservation(reading, core.ReadCached(reading.Field));
                if (delivered.All(static reading => reading.Status == FieldObservationStatus.Current)) break;
            }
            Require(delivered.Length == 12 && delivered.All(static reading => reading.Status == FieldObservationStatus.Current),
                "actual subscription produces raw6/rate6, allowing numeric zero");
            await composition.StopAsync();
            Require(!await stream.MoveNextAsync() && failures.IsEmpty, "Stop drains source and stream without failure");
        }
        // The worker is fully stopped. This stable last accepted row is the next Direct baseline.
        var stopped = IoRow(owner.ReadCachedSnapshot()!, self.Id, birth);
        VerifyIoCurrent(core, owner, fields, stopped);
        var lastAccepted = stopped.IoInterval ?? throw new InvalidDataException("Stopped IO current lacks its real interval.");
        await Task.Delay(25, timeout.Token);
        var directLower = Stopwatch.GetTimestamp();
        var direct = await core.CollectAsync(fields, timeout.Token);
        var directUpper = Stopwatch.GetTimestamp();
        var after = IoRow(owner.ReadCachedSnapshot()!, self.Id, birth);
        VerifyIoPair(stopped, after, lastAccepted.EndCounter, lastAccepted.EndCounter, directLower, directUpper);
        VerifyIoCurrent(core, owner, fields, after);
        foreach (var reading in direct)
            Require(reading == core.ReadCached(reading.Field), "stopped subscription leaves the same original Direct Current");
        Require(guard.Subscriptions == 0, "no ETW session or subscription was created");
        Console.WriteLine("[PROCESS-IO-SUB-ACTUAL-READONLY] raw6/rate6; native-composition; original-current; stop/drain; exact-last-baseline-to-Direct; zero-valid; no-generated-IO/no-ETW");
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
