using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Dependencies;
using ResourceManager.ReadWrite.Native;
using ResourceManager.ReadWrite.Windows.Cpu.Models;
using ResourceManager.ReadWrite.Windows.Devices.Npu;
using ResourceManager.ReadWrite.Windows.Metrics;
using ResourceManager.ReadWrite.Windows.Metrics.Configuration;
using ResourceManager.ReadWrite.Windows.Metrics.Resources;
using ResourceManager.ReadWrite.Windows.Metrics.Topology;
using ResourceManager.ReadWrite.Windows.Metrics.Writes;
using ResourceManager.ReadWrite.Windows.Pdh;
using ResourceManager.Subscription;
using ResourceManager.Subscription.Native;
using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.CompilerServices;

internal static class NpuMemoryConsumer
{
    internal static async Task RunRecordedAsync(
        Func<ReadWriteCore, WindowsHardwareMetricOwner, RecordedNpuMemoryResources,
            ImmutableArray<FieldReference>, CancellationToken, Task>? observe = null)
    {
        if (!OperatingSystem.IsWindows()) return;
        var fixture = new RecordedNpuMemoryResources();
        using var owner = new WindowsHardwareMetricOwner(WindowsMetricConfiguration.CreateDefault(), fixture.Create(),
            new(), modeProvider: fixture.Mode, unavailableCooldown: TimeSpan.Zero);
        using var http = new HttpClient();
        var core = new ReadWriteCore(owner.CreateBackend(), new DependencyActions(http, []));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        Require(core.Catalog.Fields.IsEmpty && fixture.Reads == 0 && fixture.DirectoryReads == 0, "construction is passive");
        var fields = RecordedNpuMemoryResources.Fields(await core.DiscoverAsync(timeout.Token));
        Require(fields.Length == 8 && fields.Select(field => field.DeviceIdentity).ToHashSet().SetEquals(fixture.Devices.Select(device => device.ExactIdentityKey)),
            "two exact PnP objects retain four independent quantities each");
        Require(core.Catalog.Devices.Count(device => device.Resource == "npu") == 2
            && fields.All(field => core.Query(field) is { Read.Supported: true, Write.Supported: false,
                Definition: { Unit: "B", ValueKind: "unsigned64" }, ReadSource: NativeMetricSnapshotSources.WindowsNpuMemory }),
            "public definitions preserve bytes, unsigned64, source and read-only capability");
        Require(fixture.Reads == 0 && fixture.DirectoryReads > 0 && owner.ReadCachedSnapshot() is null,
            "discovery enumerates only and does not publish sampled current");
        foreach (var value in new[] { 0UL, ulong.MaxValue })
        {
            fixture.Value = value;
            var actual = await core.CollectAsync(fields, timeout.Token);
            RecordedNpuMemoryResources.RequireValues(actual, value);
            foreach (var reading in actual) Require(core.ReadCached(reading.Field) == reading, "collect/cache share original current");
            Require(fixture.LastRequests.Count == 2 && fixture.LastRequests.Values.All(flags => flags == NpuMemoryFields.All),
                "both exact devices receive all four requested flags");
            if (observe is not null) await observe(core, owner, fixture, fields, timeout.Token);
        }
        var current = owner.ReadCachedSnapshot(); var reads = fixture.Reads; var directories = fixture.DirectoryReads;
        foreach (var field in fields) _ = core.ReadCached(field);
        Require(MetricSnapshotFactAssertions.SameCurrent(current, owner.ReadCachedSnapshot()) && fixture.Reads == reads && fixture.DirectoryReads == directories,
            "passive cache reads borrow the original current without querying or enumerating");
        var selected = fields.Single(field => field.DeviceIdentity == fixture.Devices[1].ExactIdentityKey
            && field.FieldName == RecordedNpuMemoryResources.SharedResident);
        RecordedNpuMemoryResources.RequireValues(await core.CollectAsync([selected], timeout.Token), ulong.MaxValue);
        Require(fixture.LastRequests.Count == 1 && fixture.LastRequests[selected.DeviceIdentity] == NpuMemoryFields.SharedResident,
            "one-field request keeps exact identity and excludes other memory domains");
        var unknown = new FieldReference(@"windows-npu|ACPI\ABSENT", selected.FieldName);
        reads = fixture.Reads;
        try { _ = core.Query(unknown); throw new InvalidDataException("Unknown Query succeeded."); } catch (ArgumentException) { }
        try { _ = core.ReadCached(unknown); throw new InvalidDataException("Unknown cache read succeeded."); } catch (ArgumentException) { }
        try { await core.CollectAsync([unknown], timeout.Token); throw new InvalidDataException("Unknown capture succeeded."); } catch (ArgumentException) { }
        Require(fixture.Reads == reads, "unknown exact object never reaches the producer");
        fixture.FailDedicated = true;
        var partial = await core.CollectAsync(fields, timeout.Token);
        foreach (var reading in partial)
        {
            if (reading.Field.FieldName.Contains("dedicated", StringComparison.Ordinal)) Empty(reading);
            else RecordedNpuMemoryResources.RequireValues([reading], ulong.MaxValue);
        }
        fixture.FailDedicated = false; fixture.Fail = true;
        var failed = await core.CollectAsync(fields, timeout.Token);
        foreach (var reading in failed) { Empty(reading); Require(core.ReadCached(reading.Field) == reading, "failure clears cache too"); }
        Require(RecordedNpuMemoryResources.Fields(core.Catalog).Length == 8, "failed values retain all definitions");
        if (observe is not null) await observe(core, owner, fixture, fields, timeout.Token);
        fixture.Fail = false; fixture.Value = 0;
        RecordedNpuMemoryResources.RequireValues(await core.CollectAsync(fields, timeout.Token), 0);
        using (var borrow = owner.BorrowSubscriptionCapture())
        {
            Require(ReferenceEquals(borrow.Backend, owner.CreateBackend()), "borrow retains original public backend");
            borrow.Bind([selected]); borrow.SynchronizeDemand([selected]);
            var committed = owner.ReadCachedSnapshot()!;
            var accepted = await borrow.CollectAsync([selected], new Permit(committed.WorkspaceIdentity, committed.ConfigurationGeneration, true), timeout.Token);
            Require(accepted.Committed, "live borrow commits through original admission");
            RecordedNpuMemoryResources.RequireValues(accepted.Readings, 0);
            Require(accepted.Readings.Single() == core.ReadCached(selected), "borrow and direct API share current");
            committed = owner.ReadCachedSnapshot()!;
            fixture.Value = 23; reads = fixture.Reads;
            var rejected = await borrow.CollectAsync([selected], new Permit(committed.WorkspaceIdentity, committed.ConfigurationGeneration, false), timeout.Token);
            Require(!rejected.Committed && fixture.Reads == reads + 1 && MetricSnapshotFactAssertions.SameCurrent(committed, owner.ReadCachedSnapshot()),
                "retired publication rejects a real different value without replacing current");
        }
        await RequireFrozenAsync();
        current = owner.ReadCachedSnapshot(); reads = fixture.Reads;
        using (var canceled = new CancellationTokenSource())
        {
            canceled.Cancel();
            try { await core.CollectAsync([selected], canceled.Token); throw new InvalidDataException("Pre-canceled capture succeeded."); }
            catch (OperationCanceledException) { }
        }
        Require(fixture.Reads == reads && MetricSnapshotFactAssertions.SameCurrent(current, owner.ReadCachedSnapshot()), "pre-cancellation is passive");
        using (var canceled = new CancellationTokenSource())
        {
            fixture.Value = 17; fixture.AfterRead = canceled.Cancel;
            try { await core.CollectAsync([selected], canceled.Token); throw new InvalidDataException("Read-then-canceled capture succeeded."); }
            catch (OperationCanceledException) { }
            finally { fixture.AfterRead = null; }
        }
        Require(MetricSnapshotFactAssertions.SameCurrent(current, owner.ReadCachedSnapshot()), "cancellation after producer read rejects publication");
        await RequireFrozenAsync();
        fixture.Value = ulong.MaxValue;
        RecordedNpuMemoryResources.RequireValues(await core.CollectAsync(fields, timeout.Token), ulong.MaxValue);
        fixture.DirectoryUnavailable = true;
        fields = RecordedNpuMemoryResources.Fields(await core.DiscoverAsync(timeout.Token)); reads = fixture.Reads;
        Require(fields.Length == 8, "unavailable directory retains exact definitions");
        foreach (var reading in await core.CollectAsync(fields, timeout.Token)) Empty(reading);
        Require(fixture.Reads == reads, "retained unavailable identities cannot authorize stale-target reads");
        fixture.DirectoryUnavailable = false; fixture.EmptyDirectory = true;
        Require(RecordedNpuMemoryResources.Fields(await core.DiscoverAsync(timeout.Token)).IsEmpty,
            "successful empty directory retires the old objects");
        Require(fixture.UnselectedReads == 0 && fixture.EngineReads == 0, "empty GPU inventory and NPU requests never sample another producer");
        Console.WriteLine("[NPU-MEMORY-PACKAGE-RECORDED] exact-PnP; four-unsigned64-bytes; 0/ulong-max; direct/borrow/current; failure-clears; unknown/cancel/retire; no-live-NPU.");

        async Task RequireFrozenAsync()
        {
            var before = core.ReadCached(selected); var beforeReads = fixture.Reads;
            fixture.Frozen = true;
            try
            {
                var frozen = (await core.CollectAsync([selected], timeout.Token)).Single();
                Require(frozen == before && fixture.Reads == beforeReads,
                    "Freeze reprojects native current without leaking uncommitted value or ObservedAt");
            }
            finally { fixture.Frozen = false; }
        }
    }
    internal static void Empty(FieldReading reading)
        => Require(reading is { Status: FieldObservationStatus.Unavailable, Value: null, ObservedAt: null,
            SourceId: NativeMetricSnapshotSources.WindowsNpuMemory }, "failed source is an explicit empty observation");
    internal static void Require(bool condition, string message)
    { if (!condition) throw new InvalidDataException("Recorded NPU memory package: " + message); }
    private sealed class Permit(ulong workspace, ulong generation, bool allow) : IMetricPublicationPermit
    {
        public ulong WorkspaceIncarnation => workspace;
        public ulong ConfigurationGeneration => generation;
        public bool IsActive => true;
        public bool TryPublish(Action commit) { if (!allow) return false; commit(); return true; }
    }
}

internal sealed class RecordedNpuMemoryResources
{
    internal const string SharedResident = "npu_all_shared_memory_resident";
    internal static readonly string[] Names = ["npu_all_dedicated_memory_committed", "npu_all_dedicated_memory_resident",
        "npu_all_shared_memory_committed", SharedResident];
    internal NpuMemoryCatalogIdentity[] Devices { get; } = [new(@"ACPI\RECORDED_NPU\A"), new(@"ACPI\RECORDED_NPU\B")];
    internal int Reads, DirectoryReads, UnselectedReads, EngineReads;
    internal ulong Value;
    internal bool Fail, FailDedicated, DirectoryUnavailable, EmptyDirectory, Frozen;
    internal Action? AfterRead;
    internal IReadOnlyDictionary<string, NpuMemoryFields> LastRequests = new Dictionary<string, NpuMemoryFields>();
    internal NativeMetricSnapshotZoneMode Mode(string source)
        => Frozen && source == NativeMetricSnapshotSources.WindowsNpuMemory ? NativeMetricSnapshotZoneMode.Freeze : NativeMetricSnapshotZoneMode.Normal;
    internal WindowsHardwareMetricResources Create() => new()
    {
        Controls = new WindowsMetricControlResources { ControlReadCpuIdentity = static () => null,
            ControlReadCpuTopology = static () => new CpuOsTopologyInformation([], []) },
        ReadCpuCounters = () => throw Unexpected(), ReadMemory = () => throw Unexpected(), ReadVirtualMemory = () => throw Unexpected(),
        ReadNumaAvailable = (_, _) => throw Unexpected(), ReadNumaIdentities = static () => new(NativeMetricSnapshotCatalogTopologyStatus.Complete, []),
        ReadGpuInventory = static () => new(SamplingObservationStatus.Current, 1, DateTimeOffset.UtcNow.UtcTicks, 0, 0, 0, 1, []),
        ReadCpuFrequency = () => throw Unexpected(), ReadGpuUsage = _ => { EngineReads++; return PdhGpuEngineUsageRead.NotRequested; },
        ReadNvmlAsync = (_, _, _) => throw Unexpected(), ReadNvapiAsync = (_, _, _) => throw Unexpected(), ReadAdlxAsync = (_, _, _) => throw Unexpected(),
        ReadNvmlFanIdentities = static _ => [], ReadAmdSmu = _ => throw Unexpected(), ReadIntelGpu = (_, _, _) => throw Unexpected(), ReadIntelMsr = (_, _) => throw Unexpected(),
        ReadBatteryIdentities = static () => new(NativeMetricSnapshotCatalogTopologyStatus.Complete, []), ReadBattery = (_, _) => throw Unexpected(),
        ReadNetworkIdentities = static () => new(NativeMetricSnapshotCatalogTopologyStatus.Complete, []), ReadNetworkInterfaces = (_, _) => throw Unexpected(),
        ReadSystemIo = _ => throw Unexpected(), ReadStorageIdentities = static _ => new(NativeMetricSnapshotCatalogTopologyStatus.Complete, []),
        ReadFanIdentities = static _ => new(NativeMetricSnapshotCatalogTopologyStatus.Complete, []), ReadStorageSensors = () => throw Unexpected(),
        ReadNvmeSensors = _ => throw Unexpected(), ReadAtaSensors = _ => throw Unexpected(), ReadHardwareMonitor = () => throw Unexpected(),
        ReadNotebookFans = () => throw Unexpected(), SynchronizeNotebookDemand = static _ => { },
        ReadNpuMemoryIdentities = maximum =>
        {
            DirectoryReads++;
            NpuMemoryConsumer.Require(maximum == 64, "directory receives current compiled default capacity");
            return new(DirectoryUnavailable ? NativeMetricSnapshotCatalogTopologyStatus.Unavailable : NativeMetricSnapshotCatalogTopologyStatus.Complete,
                DirectoryUnavailable || EmptyDirectory ? [] : Devices);
        },
        ReadNpuMemory = (devices, requested, maximum, token) =>
        {
            token.ThrowIfCancellationRequested(); Reads++;
            NpuMemoryConsumer.Require(maximum == 64 && devices.Select(device => device.ExactIdentityKey).ToHashSet().SetEquals(Devices.Select(device => device.ExactIdentityKey))
                && requested.Count > 0 && requested.Keys.All(key => Devices.Any(device => device.ExactIdentityKey == key)),
                "producer receives exact catalog and actual compiled bound");
            LastRequests = requested.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            var rows = requested.Select(pair => new NpuMemoryRead(pair.Key,
                Get(pair.Value, NpuMemoryFields.DedicatedCommitted, FailDedicated), Get(pair.Value, NpuMemoryFields.DedicatedResident, FailDedicated),
                Get(pair.Value, NpuMemoryFields.SharedCommitted, false), Get(pair.Value, NpuMemoryFields.SharedResident, false))).ToArray();
            AfterRead?.Invoke();
            return rows;
        }
    };
    private ulong? Get(NpuMemoryFields request, NpuMemoryFields field, bool domainFailed)
        => !Fail && !domainFailed && request.HasFlag(field) ? Value : null;
    private Exception Unexpected() { UnselectedReads++; return new InvalidOperationException("Unselected NPU memory recorded producer queried."); }
    internal static ImmutableArray<FieldReference> Fields(ReadWriteCatalog catalog)
        => catalog.Fields.Where(field => Names.Contains(field.Field.FieldName, StringComparer.Ordinal)).Select(field => field.Field)
            .OrderBy(field => field.DeviceIdentity, StringComparer.Ordinal).ThenBy(field => field.FieldName, StringComparer.Ordinal).ToImmutableArray();
    internal static void RequireValues(ImmutableArray<FieldReading> rows, ulong value)
    {
        NpuMemoryConsumer.Require(!rows.IsEmpty && rows.All(row => row.Status == FieldObservationStatus.Current && row.ObservedAt is not null
            && row.SourceId == NativeMetricSnapshotSources.WindowsNpuMemory && row.Value is { Number: null }
            && row.Value.Integer == MetricInteger.FromUnsigned128((UInt128)value)), "exact unsigned byte values and original source/timestamp survive projection");
    }
}

internal static class NpuMemorySubscriptionConsumer
{
    internal static Task RunRecordedAsync() => NpuMemoryConsumer.RunRecordedAsync(ObserveAsync);
    private static async Task ObserveAsync(ReadWriteCore core, WindowsHardwareMetricOwner owner,
        RecordedNpuMemoryResources fixture, ImmutableArray<FieldReference> fields, CancellationToken token)
    {
        var configuration = new NativeSamplingSubscriptionConfiguration
        {
            AbiVersion = NativeSamplingSubscriptionAbi.Version,
            StructSize = (uint)Unsafe.SizeOf<NativeSamplingSubscriptionConfiguration>(), Generation = 1,
            MaximumSourceCount = 8, MaximumItemCount = 8, MaximumMembershipCount = 16,
            MaximumDueItemCount = 8, MaximumSourceViewCount = 8, MaximumExpiredSourceCount = 8,
            DefaultIntervalMilliseconds = 1000, MinimumIntervalMilliseconds = 1,
            ActiveTtlMilliseconds = 3000, MaximumFutureSkewMilliseconds = 1000
        };
        var settings = new SubscriptionRoleSettings(2, 1000, 1, 3000, 1000, 1000);
        var failures = new List<SubscriptionWorkerFailure>();
        await using var composition = new NativeFieldSubscriptionComposition(owner.BorrowSubscriptionCapture(), settings,
            in configuration, failure => failures.Add(failure));
        await composition.StartAsync(token);
        var publicFields = new FieldSubscriptionCore(core, composition);
        await using var stream = publicFields.SubscribeAsync("recorded-npu-memory", fields,
            TimeSpan.FromMilliseconds(250), token).GetAsyncEnumerator();
        ImmutableArray<FieldReading> delivered = [];
        var ready = false;
        for (var attempt = 0; attempt < 8; attempt++)
        {
            NpuMemoryConsumer.Require(await stream.MoveNextAsync(), "original native composition delivers selected NPU fields");
            delivered = stream.Current;
            NpuMemoryConsumer.Require(delivered.Select(row => row.Field).SequenceEqual(fields), "subscription preserves exact field ordering");
            foreach (var reading in delivered)
                FieldObservationConsumerAssertions.RequireOriginalOwnerObservation(reading, core.ReadCached(reading.Field));
            ready = delivered.All(row => row.Status == (fixture.Fail ? FieldObservationStatus.Unavailable : FieldObservationStatus.Current));
            if (ready) break;
        }
        NpuMemoryConsumer.Require(ready, "original worker publishes expected state within bounded observations");
        await composition.StopAsync();
        NpuMemoryConsumer.Require(!await stream.MoveNextAsync() && failures.Count == 0, "Stop closes the stream and drains its worker");
        if (fixture.Fail) foreach (var reading in delivered) NpuMemoryConsumer.Empty(reading);
        else RecordedNpuMemoryResources.RequireValues(delivered, fixture.Value);
        var current = owner.ReadCachedSnapshot(); var reads = fixture.Reads;
        foreach (var field in fields) _ = core.ReadCached(field);
        NpuMemoryConsumer.Require(MetricSnapshotFactAssertions.SameCurrent(current, owner.ReadCachedSnapshot()) && fixture.Reads == reads,
            "post-Stop passive cache retains same original current without capture");
        var direct = await core.CollectAsync(fields, token);
        NpuMemoryConsumer.Require(fixture.Reads == reads + 1 && fixture.UnselectedReads == 0 && fixture.EngineReads == 0,
            "Stop retires only subscription demand and direct original owner remains usable");
        if (fixture.Fail) foreach (var reading in direct) NpuMemoryConsumer.Empty(reading);
        else RecordedNpuMemoryResources.RequireValues(direct, fixture.Value);
        Console.WriteLine("[NPU-MEMORY-SUBSCRIPTION-PACKAGE-RECORDED] actual-native-composition; same-current; Stop-drains; direct-owner-usable; no-live-NPU.");
    }
}
