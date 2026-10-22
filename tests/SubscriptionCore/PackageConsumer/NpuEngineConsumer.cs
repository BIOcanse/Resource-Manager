using System.Collections.Immutable;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Dependencies;
using ResourceManager.ReadWrite.Native;
using ResourceManager.ReadWrite.Windows.Metrics;
using ResourceManager.ReadWrite.Windows.Metrics.Configuration;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite.Windows.Cpu.Models;
using ResourceManager.ReadWrite.Windows.Devices.Npu;
using ResourceManager.ReadWrite.Windows.Gpu;
using ResourceManager.ReadWrite.Windows.Metrics.Resources;
using ResourceManager.ReadWrite.Windows.Metrics.Topology;
using ResourceManager.ReadWrite.Windows.Metrics.Writes;
using ResourceManager.ReadWrite.Windows.Pdh;


internal sealed class NpuEngineRecordedResources
{
    internal const string Raw = "npu_all_engine_running_time_microseconds";
    internal const string Ratio = "npu_all_engine_reported_running_time_ratio_percent";
    internal const string Busy = "npu_all_engine_busy_percent";
    internal int Count = 2, Opens, Reads, UnselectedReads;
    internal bool Fail, Unavailable = false, InvalidateDuringRead = false;
    internal ulong Counter = 9_007_199_254_740_993, Ticks = 1_000_000, Frequency = 1_000_000;
    internal ulong CounterStep = 1_250_000, TickStep = 1_000_000;
    internal Action? AfterRead;
    internal readonly List<Directory> Opened = [];
    internal IReadOnlySet<string> LastRequested = new HashSet<string>();
    internal uint LastMaximum;
    internal NpuEngineTopologyRead Open(uint maximum, IWindowsNpuEngineDirectory? prior)
        => Unavailable ? new(NativeMetricSnapshotCatalogTopologyStatus.Unavailable, [])
            : WindowsNpuEngineTopology.OpenDirectory(bound =>
            {
                Opens++;
                if (Count > bound) return null;
                var directory = new Directory(Count); Opened.Add(directory); return directory;
            }, maximum, prior);
    internal IReadOnlyList<NpuEngineRead> Read(IReadOnlyList<NpuEngineCatalogIdentity> devices,
        IReadOnlySet<string> requested, uint maximum, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); Reads++; LastRequested = requested; LastMaximum = maximum;
        var counter = Counter; var ticks = Ticks;
        Counter = checked(Counter + CounterStep); Ticks = checked(Ticks + TickStep);
        var rows = devices.Where(engine => requested.Contains(engine.ExactIdentityKey)).Select(engine =>
            new NpuEngineRead(engine, Fail ? null : new NpuEngineSample(counter, ticks - 1, ticks, Frequency), InvalidateDuringRead)).ToArray();
        if (InvalidateDuringRead) foreach (var directory in Opened) directory.Current = false;
        AfterRead?.Invoke(); return rows;
    }
    internal WindowsHardwareMetricResources Create() => new()
    {
        CpuName = "recorded", MemoryDescription = "recorded",
        Controls = new WindowsMetricControlResources { ControlReadCpuIdentity = static () => null,
            ControlReadCpuTopology = static () => new CpuOsTopologyInformation([], []) },
        OpenNpuEngineDirectory = Open, ReadNpuEngines = Read,
        ReadNpuMemoryIdentities = static _ => new(NativeMetricSnapshotCatalogTopologyStatus.Complete,
            [new NpuMemoryCatalogIdentity(@"ACPI\RECORDED_ENGINE_NPU\ONE")]),
        ReadGpuInventory = static () => new(SamplingObservationStatus.Current, 1, DateTimeOffset.UtcNow.UtcTicks, 0, 0, 0, 1, []),
        ReadStorageIdentities = static _ => new(NativeMetricSnapshotCatalogTopologyStatus.Complete, []),
        ReadFanIdentities = static _ => new(NativeMetricSnapshotCatalogTopologyStatus.Complete, []),
        ReadNvmlFanIdentities = static _ => [],
        ReadBatteryIdentities = static () => new(NativeMetricSnapshotCatalogTopologyStatus.Complete, []),
        ReadNetworkIdentities = static () => new(NativeMetricSnapshotCatalogTopologyStatus.Complete, []),
        ReadNumaIdentities = static () => new(NativeMetricSnapshotCatalogTopologyStatus.Complete, []),
        ReadCpuCounters = () => throw Unexpected(), ReadMemory = () => throw Unexpected(), ReadVirtualMemory = () => throw Unexpected(),
        ReadNumaAvailable = (_, _) => new Dictionary<string, ulong>(), ReadCpuFrequency = () => throw Unexpected(),
        ReadGpuUsage = _ => PdhGpuEngineUsageRead.NotRequested,
        ReadNvmlAsync = (_, _, _) => throw Unexpected(), ReadNvapiAsync = (_, _, _) => throw Unexpected(), ReadAdlxAsync = (_, _, _) => throw Unexpected(),
        ReadAmdSmu = _ => throw Unexpected(), ReadIntelGpu = (_, _, _) => throw Unexpected(), ReadIntelMsr = (_, _) => throw Unexpected(),
        ReadBattery = (_, _) => throw Unexpected(), ReadNetworkInterfaces = (_, _) => throw Unexpected(), ReadSystemIo = _ => throw Unexpected(),
        ReadStorageSensors = () => throw Unexpected(), ReadNvmeSensors = _ => throw Unexpected(), ReadAtaSensors = _ => throw Unexpected(),
        ReadHardwareMonitor = () => throw Unexpected(), ReadNotebookFans = () => throw Unexpected(), SynchronizeNotebookDemand = static _ => { }
    };
    private Exception Unexpected() { UnselectedReads++; return new InvalidOperationException("Unselected engine fixture source."); }

    internal sealed class Directory : IWindowsNpuEngineDirectory
    {
        private readonly object gate = new();
        private int references = 1, initialReleased;
        internal bool Current = true;
        internal bool Disposed;
        internal Directory(int count)
        {
            var id = Guid.NewGuid();
            Identities = Enumerable.Range(0, count).Select(index => new NpuEngineCatalogIdentity(
                @"ACPI\RECORDED_ENGINE_NPU\ONE", checked((uint)index), "Same name", id, 7, 0x8086, 0x1234,
                checked((uint)count), 1, "1", this)).ToArray();
        }
        public IReadOnlyList<NpuEngineCatalogIdentity> Identities { get; }
        public bool IsCurrent(uint maximum, CancellationToken token = default)
        { token.ThrowIfCancellationRequested(); lock (gate) return !Disposed && Current && maximum > 0 && Identities.Count <= maximum; }
        public IDisposable AcquireLease()
        {
            lock (gate) { ObjectDisposedException.ThrowIf(Disposed, this); references++; return new Borrow(this); }
        }
        public void Dispose() { if (Interlocked.Exchange(ref initialReleased, 1) == 0) Release(); }
        private void Release() { lock (gate) if (--references == 0) Disposed = true; }
        private sealed class Borrow(Directory directory) : IDisposable
        {
            private Directory? owner = directory;
            public void Dispose() => Interlocked.Exchange(ref owner, null)?.Release();
        }
    }
}

internal static class NpuEngineConsumer
{
    internal static async Task RunRecordedAsync(Func<ReadWriteCore, WindowsHardwareMetricOwner, NpuEngineRecordedResources,
        ImmutableArray<FieldReference>, CancellationToken, Task>? observe = null)
    {
        if (!OperatingSystem.IsWindows()) return;
        var recording = new NpuEngineRecordedResources();
        using var owner = new WindowsHardwareMetricOwner(WindowsMetricConfiguration.CreateDefault(), recording.Create(), new(), unavailableCooldown: TimeSpan.Zero);
        using var http = new HttpClient(); var core = new ReadWriteCore(owner.CreateBackend(), new DependencyActions(http, []));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        Require(core.Catalog.Fields.IsEmpty && recording.Opens == 0 && recording.Reads == 0, "construction is passive");
        var fields = Fields(await core.DiscoverAsync(timeout.Token));
        Require(fields.Length == 4 && fields.Select(field => field.DeviceIdentity).Distinct().Count() == 2 && recording.Reads == 0,
            "exact independent engines each expose raw and ratio, discovery does not sample");
        Require(core.Catalog.Devices.Count(device => device.Resource == "npu_engine") == 2, "typed engine devices are separate from parent NPU resources");
        foreach (var field in fields)
        {
            var capability = core.Query(field);
            Require(capability.Read.Dependencies.IsEmpty, "OS-provided DXCore source requires no external installation dependency");
            Require(capability.Read.Supported && !capability.Write.Supported && capability.ReadSource == NativeMetricSnapshotSources.WindowsNpuEngine,
                "original read-only source contract");
            Require(capability.Definition.ValueKind == (field.FieldName == NpuEngineRecordedResources.Raw ? "unsigned64" : "float64")
                && capability.Definition.Unit == (field.FieldName == NpuEngineRecordedResources.Raw ? "µs" : "%"), "precise units and value kinds");
        }
        var first = await core.CollectAsync(fields, timeout.Token);
        Require(first.Where(row => row.Field.FieldName == NpuEngineRecordedResources.Raw).All(row => row.Value?.Integer == "9007199254740993"), "raw full unsigned precision");
        Ratio(first, null);
        Ratio(await core.CollectAsync(fields, timeout.Token), 125);
        Require(recording.Reads == 2 && recording.LastRequested.Count == 2 && recording.LastMaximum == 256, "one source read supplies both quantities per engine");
        var before = owner.ReadCachedSnapshot(); var reads = recording.Reads;
        foreach (var field in fields) _ = core.ReadCached(field);
        Require(ReferenceEquals(before, owner.ReadCachedSnapshot()) && recording.Reads == reads, "cache uses original current without sampling");
        var busy = BusyFields(core.Catalog);
        Require(busy.Length == 1, "one device busy field is expanded for the exact parent NPU");
        var busyCapability = core.Query(busy[0]);
        Require(busyCapability.Read.Supported && !busyCapability.Write.Supported
            && busyCapability.ReadSource == NativeMetricSnapshotSources.WindowsNpuEngine
            && busyCapability.Read.Dependencies.IsEmpty
            && busyCapability.Definition.ValueKind == "float64" && busyCapability.Definition.Unit == "%",
            "OS-provided device busy field is read-only and reports percent");
        var busyCurrent = await core.CollectAsync(busy, timeout.Token);
        Require(busyCurrent.Single().Value?.Number == 125 && recording.Reads == 3 && recording.LastRequested.Count == 2,
            "device busy is the maximum over all bound engine ratios");
        before = owner.ReadCachedSnapshot(); reads = recording.Reads;
        _ = core.ReadCached(busy[0]);
        Require(ReferenceEquals(before, owner.ReadCachedSnapshot()) && recording.Reads == reads,
            "device busy cache read uses current without creating another engine sample");
        Require(fields.SequenceEqual(Fields(await core.DiscoverAsync(timeout.Token))) && recording.Opens == 1, "repeated probe retains the real live directory");
        if (observe is not null) await observe(core, owner, recording, fields, timeout.Token);
        recording.Fail = true;
        Require((await core.CollectAsync(fields, timeout.Token)).All(row => row is { Value: null, ObservedAt: null }), "failure publishes unknown, not old percent");
        recording.Fail = false;
        Ratio(await core.CollectAsync(fields, timeout.Token), null);
        Ratio(await core.CollectAsync(fields, timeout.Token), 125);
        using (var canceled = new CancellationTokenSource())
        {
            before = owner.ReadCachedSnapshot(); recording.AfterRead = canceled.Cancel;
            try { await core.CollectAsync(fields, canceled.Token); throw new InvalidDataException("Cancellation published."); }
            catch (OperationCanceledException) { }
            finally { recording.AfterRead = null; }
            Require(ReferenceEquals(before, owner.ReadCachedSnapshot()), "cancellation cannot publish or replace original current");
        }
        using (var borrow = owner.BorrowSubscriptionCapture())
        {
            borrow.Bind(fields); borrow.SynchronizeDemand(fields); before = owner.ReadCachedSnapshot()!;
            Require(!(await borrow.CollectAsync(fields, new RejectedPermit(before.WorkspaceIdentity, before.ConfigurationGeneration), timeout.Token)).Committed
                && ReferenceEquals(before, owner.ReadCachedSnapshot()), "rejected original permit cannot advance current/baseline");
        }
        recording.Opened[0].Current = false;
        var replacement = Fields(await core.DiscoverAsync(timeout.Token));
        Require(!fields.Select(field => field.DeviceIdentity).Intersect(replacement.Select(field => field.DeviceIdentity)).Any(), "real directory replacement retires old engine identities");
        Ratio(await core.CollectAsync(replacement, timeout.Token), null);
        Ratio(await core.CollectAsync(replacement, timeout.Token), 125);
        Require(recording.Opened[0].Disposed && recording.UnselectedReads == 0, "old directory drains without querying unrelated producers");
        owner.Dispose(); Require(recording.Opened.All(directory => directory.Disposed), "last original holder releases all directory leases");
        Console.WriteLine("[NPU-ENGINE-PACKAGE-RECORDED] raw-U64/QPC-Native-ratio125/device-busy-max; warmup/failure/reopen; same-current; live-directory-reuse; cancellation/retirement; no-live-hardware.");
    }
    internal static ImmutableArray<FieldReference> Fields(ReadWriteCatalog catalog)
        => catalog.Fields.Where(field => field.Field.FieldName is NpuEngineRecordedResources.Raw or NpuEngineRecordedResources.Ratio)
            .Select(field => field.Field).OrderBy(field => field.DeviceIdentity, StringComparer.Ordinal).ThenBy(field => field.FieldName, StringComparer.Ordinal).ToImmutableArray();
    internal static ImmutableArray<FieldReference> BusyFields(ReadWriteCatalog catalog)
        => catalog.Fields.Where(field => field.Field.FieldName == NpuEngineRecordedResources.Busy)
            .Select(field => field.Field).ToImmutableArray();
    internal static void Ratio(ImmutableArray<FieldReading> readings, double? expected)
        => Require(readings.Where(row => row.Field.FieldName == NpuEngineRecordedResources.Ratio).All(row => row.Value?.Number == expected), "reported ratio/warmup contract");
    internal static void Require(bool condition, string message) { if (!condition) throw new InvalidDataException("Recorded NPU engine: " + message); }
    private sealed class RejectedPermit(ulong workspace, ulong generation) : IMetricPublicationPermit
    {
        public ulong WorkspaceIncarnation => workspace;
        public ulong ConfigurationGeneration => generation;
        public bool IsActive => false;
        public bool TryPublish(Action commit) => false;
    }
}
