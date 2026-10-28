using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Dependencies;
using ResourceManager.ReadWrite.Native;
using ResourceManager.ReadWrite.Vendors.Intel.Msr;
using ResourceManager.ReadWrite.Windows.Cpu.Models;
using ResourceManager.ReadWrite.Windows.Metrics;
using ResourceManager.ReadWrite.Windows.Metrics.Configuration;
using ResourceManager.ReadWrite.Windows.Metrics.Resources;
using ResourceManager.ReadWrite.Windows.Metrics.Writes;
using ResourceManager.ReadWrite.Windows.Pdh;
using ResourceManager.Subscription;
using ResourceManager.Subscription.Native;

internal static class IntelPsysSubscriptionConsumer
{
    internal static async Task RunRecordedAsync()
    {
        if (!OperatingSystem.IsWindows()) return;
        var fixture = new RecordedIntelPsysResources();
        using var owner = new WindowsHardwareMetricOwner(WindowsMetricConfiguration.CreateDefault(), fixture.Create(),
            RecordedIntelPsysResources.Options(), modeProvider: fixture.Mode, unavailableCooldown: TimeSpan.Zero);
        using var http = new HttpClient();
        var core = new ReadWriteCore(owner.CreateBackend(), new DependencyActions(http, []));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        Require(core.Catalog.Fields.IsEmpty && fixture.Reads == 0 && fixture.CatalogEngineProbeCount == 0, "construction remains passive");
        var fields = RecordedIntelPsysResources.Fields(await core.DiscoverAsync(timeout.Token));
        Require(fields.Length == 2 && fields.All(field => field.DeviceIdentity == RecordedIntelPsysResources.Identity),
            "discovery publishes one exact platform");
        Require(owner.ReadCachedSnapshot() is null, "discovery does not publish numeric current");
        Require(fixture.Reads == 0, $"discovery does not read MSRs; actual reads={fixture.Reads}");
        Require(fixture.CatalogEngineProbeCount == 0,
            $"complete empty GPU inventory has no engine target to probe; actual probes={fixture.CatalogEngineProbeCount}");
        foreach (var field in fields)
        {
            var capability = core.Query(field);
            Require(capability.Read.Supported && !capability.Write.Supported && capability.ReadSource == NativeMetricSnapshotSources.IntelMsr
                && capability.Definition.ValueKind == (field.FieldName == RecordedIntelPsysResources.RawName ? "unsigned64" : "float64")
                && capability.Definition.Unit == (field.FieldName == RecordedIntelPsysResources.RawName ? "count" : "J/count"),
                "public raw and unit definitions retain their independent types and units");
        }

        await ObserveOnceAsync(failed: false);
        fixture.Raw = uint.MaxValue; fixture.Unit = 1;
        await ObserveOnceAsync(failed: false);
        RecordedIntelPsysResources.RequireValues(await core.CollectAsync(fields, timeout.Token), uint.MaxValue, 1);
        fixture.FailRaw = true; fixture.FailUnit = true;
        await ObserveOnceAsync(failed: true);
        Require(RecordedIntelPsysResources.Fields(core.Catalog).Length == 2, "failed values retain both definitions");
        fixture.FailRaw = false;
        var independent = await core.CollectAsync(fields, timeout.Token);
        Require(independent.Single(reading => reading.Field.FieldName == RecordedIntelPsysResources.RawName)
                is { Status: FieldObservationStatus.Current, Value: { Integer: { LowBits: uint.MaxValue, HighBits: 0, Negative: false }, Number: null } }
            && independent.Single(reading => reading.Field.FieldName == RecordedIntelPsysResources.UnitName)
                is { Status: FieldObservationStatus.Unavailable, Value: null },
            "direct raw recovery after Stop does not depend on unit recovery");
        fixture.FailUnit = false;
        RecordedIntelPsysResources.RequireValues(await core.CollectAsync(fields, timeout.Token), fixture.Raw, fixture.Unit);
        var current = owner.ReadCachedSnapshot(); var reads = fixture.Reads;
        foreach (var field in fields) _ = core.ReadCached(field);
        Require(MetricSnapshotFactAssertions.SameCurrent(current, owner.ReadCachedSnapshot()) && fixture.Reads == reads, "passive cache is the original current without a capture");
        using (var canceled = new CancellationTokenSource())
        {
            canceled.Cancel();
            try { await core.CollectAsync(fields, canceled.Token); throw new InvalidDataException("Canceled direct capture completed."); }
            catch (OperationCanceledException) { }
        }
        Require(MetricSnapshotFactAssertions.SameCurrent(current, owner.ReadCachedSnapshot()) && fixture.Reads == reads
            && fixture.CatalogEngineProbeCount == 0 && fixture.UnselectedReads == 0,
            "cancellation and subscription stop preserve owner lifetime without unrelated queries");
        var raw = fields.Single(field => field.FieldName == RecordedIntelPsysResources.RawName);
        var unit = fields.Single(field => field.FieldName == RecordedIntelPsysResources.UnitName);
        var committedRaw = core.ReadCached(raw);
        fixture.Raw = 17;
        using (var canceledAfterRead = new CancellationTokenSource())
        {
            fixture.ReadCompleted = canceledAfterRead.Cancel;
            try { await core.CollectAsync([raw], canceledAfterRead.Token); throw new InvalidDataException("Read-then-canceled capture completed."); }
            catch (OperationCanceledException) { }
            finally { fixture.ReadCompleted = null; }
        }
        Require(MetricSnapshotFactAssertions.SameCurrent(current, owner.ReadCachedSnapshot()) && core.ReadCached(raw) == committedRaw,
            "canceling a different raw read after Stop preserves committed bits and observation time");
        await RequireFrozenRawAsync("canceled");
        await core.CollectAsync([unit], timeout.Token);
        Require(core.ReadCached(raw) == committedRaw && fixture.LastFields.SetEquals([MetricFieldCode.IntelPsysEnergyUnit]),
            "later unit-only capture cannot expose canceled raw bits or their timestamp");
        using (var retiredBorrow = owner.BorrowSubscriptionCapture())
        {
            retiredBorrow.Bind([raw]); retiredBorrow.SynchronizeDemand([raw]);
            var original = owner.ReadCachedSnapshot()!;
            var beforeRejectedReads = fixture.Reads;
            fixture.Raw = 23;
            var rejected = await retiredBorrow.CollectAsync([raw], new RetiredPermit(original.WorkspaceIdentity, original.ConfigurationGeneration), timeout.Token);
            Require(!rejected.Committed && fixture.Reads == beforeRejectedReads + 1 && MetricSnapshotFactAssertions.SameCurrent(original, owner.ReadCachedSnapshot()),
                "retired borrow rejects the real read after subscription Stop");
        }
        await RequireFrozenRawAsync("rejected borrowed");
        await core.CollectAsync([unit], timeout.Token);
        Require(core.ReadCached(raw) == committedRaw && fixture.CatalogEngineProbeCount == 0 && fixture.UnselectedReads == 0,
            "subsequent publication cannot expose rejected borrowed raw bits or observation time");
        Console.WriteLine("[INTEL-PSYS-SUBSCRIPTION-PACKAGE-RECORDED] native-composition; one-platform; raw=0/4294967295; unit=J/count; same-current; failure-clears; stop-drains; direct-owner-usable; no-live-MSR.");

        async Task RequireFrozenRawAsync(string rejectedRead)
        {
            var readsBeforeFreeze = fixture.Reads;
            fixture.Frozen = true;
            ImmutableArray<FieldReading> frozen;
            try { frozen = await core.CollectAsync([raw], timeout.Token); }
            finally { fixture.Frozen = false; }
            var observed = frozen.Single();
            Require(fixture.Reads == readsBeforeFreeze && observed.Field == committedRaw.Field
                && observed.Status == committedRaw.Status && observed.Value == committedRaw.Value
                && observed.ObservedAt == committedRaw.ObservedAt && observed.SourceId == committedRaw.SourceId,
                $"Freeze explicitly projects raw without querying and cannot expose {rejectedRead} bits or observation time");
        }

        async Task ObserveOnceAsync(bool failed)
        {
            var settings = new SubscriptionRoleSettings(2, 1000, 1, 3000, 1000, 1000);
            var configuration = new NativeSamplingSubscriptionConfiguration
            {
                AbiVersion = NativeSamplingSubscriptionAbi.Version,
                StructSize = (uint)Unsafe.SizeOf<NativeSamplingSubscriptionConfiguration>(), Generation = 1,
                MaximumSourceCount = 8, MaximumItemCount = 8, MaximumMembershipCount = 16,
                MaximumDueItemCount = 8, MaximumSourceViewCount = 8, MaximumExpiredSourceCount = 8,
                DefaultIntervalMilliseconds = 1000, MinimumIntervalMilliseconds = 1,
                ActiveTtlMilliseconds = 3000, MaximumFutureSkewMilliseconds = 1000
            };
            var failures = new List<SubscriptionWorkerFailure>();
            await using var composition = new NativeFieldSubscriptionComposition(owner.BorrowSubscriptionCapture(), settings,
                in configuration, failure => failures.Add(failure));
            await composition.StartAsync(timeout.Token);
            var publicFields = new FieldSubscriptionCore(core, composition);
            await using var stream = publicFields.SubscribeAsync("recorded-psys", fields, TimeSpan.FromMilliseconds(250), timeout.Token).GetAsyncEnumerator();
            ImmutableArray<FieldReading> readings = [];
            for (var attempt = 0; attempt < 8; attempt++)
            {
                Require(await stream.MoveNextAsync(), "native subscription delivers the selected pair");
                readings = stream.Current;
                Require(readings.Select(reading => reading.Field).SequenceEqual(fields), "native subscription keeps exact ordered platform fields");
                foreach (var reading in readings)
                    FieldObservationConsumerAssertions.RequireOriginalOwnerObservation(reading, core.ReadCached(reading.Field));
                if (readings.All(reading => reading.Status == (failed ? FieldObservationStatus.Unavailable : FieldObservationStatus.Current))) break;
            }
            await composition.StopAsync();
            Require(!await stream.MoveNextAsync() && failures.Count == 0, "Stop closes the retired stream and drains its original worker");
            if (failed)
                Require(readings.All(reading => reading is { Status: FieldObservationStatus.Unavailable, Value: null, ObservedAt: null }),
                    "source failure clears the subscribed pair without stale values");
            else RecordedIntelPsysResources.RequireValues(readings, fixture.Raw, fixture.Unit);
            Require(fixture.LastFields.SetEquals([MetricFieldCode.IntelPsysEnergyCounter, MetricFieldCode.IntelPsysEnergyUnit])
                && fixture.LastTarget is { IsPlatform: true, DisplayIndex: 0, Processor: { ProcessorGroup: 1, GroupRelativeIndex: 5 } }
                && fixture.UnselectedReads == 0 && fixture.CatalogEngineProbeCount == 0,
                "borrow queries only the exact non-CPU0 platform target and selected quantities");
        }
    }
    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidDataException("Recorded PSys subscription package: " + message); }
    private sealed class RetiredPermit(ulong workspace, ulong generation) : IMetricPublicationPermit
    {
        public ulong WorkspaceIncarnation => workspace;
        public ulong ConfigurationGeneration => generation;
        public bool IsActive => true;
        public bool TryPublish(Action commit) => false;
    }
}

internal sealed class RecordedIntelPsysResources
{
    internal const string RawName = "system_intel_psys_energy_counter_raw";
    internal const string UnitName = "system_intel_psys_energy_unit_joules";
    internal const string Identity = "windows-intel-psys-platform|windows-cpu-package|1:5,1:6";
    internal int Reads { get; private set; }
    internal int CatalogEngineProbeCount { get; private set; }
    internal int UnselectedReads { get; private set; }
    internal ulong Raw { get; set; }
    internal double Unit { get; set; } = Math.ScaleB(1d, -31);
    internal bool FailRaw { get; set; }
    internal bool FailUnit { get; set; }
    internal bool Frozen { get; set; }
    internal Action? ReadCompleted { get; set; }
    internal HashSet<MetricFieldCode> LastFields { get; private set; } = [];
    internal IntelMsrCatalogIdentity? LastTarget { get; private set; }
    internal CpuOsTopologyInformation? Topology { get; set; } = CreateTopology();
    internal static WindowsMetricResourceOptions Options() => new()
        { IntelModulePath = "recorded-module-not-opened", PawnIoLibraryPath = "recorded-library-not-loaded" };
    internal NativeMetricSnapshotZoneMode Mode(string source)
        => Frozen && source == NativeMetricSnapshotSources.IntelMsr ? NativeMetricSnapshotZoneMode.Freeze : NativeMetricSnapshotZoneMode.Normal;
    internal WindowsHardwareMetricResources Create() => new()
    {
        Controls = new WindowsMetricControlResources
        { ControlReadCpuIdentity = static () => null,
            ControlReadCpuTopology = () => Topology ?? throw new IOException("Recorded topology unavailable") },
        ReadCpuCounters = () => throw Unexpected(), ReadMemory = () => throw Unexpected(),
        ReadVirtualMemory = () => throw Unexpected(), ReadNumaAvailable = (_, _) => throw Unexpected(),
        ReadNumaIdentities = static () => new(NativeMetricSnapshotCatalogTopologyStatus.Complete, []),
        ReadGpuInventory = static () => new(SamplingObservationStatus.Current, 1, DateTimeOffset.UtcNow.UtcTicks, 0, 0, 0, 1, []),
        ReadCpuFrequency = () => throw Unexpected(),
        ReadGpuUsage = _ => { CatalogEngineProbeCount++; return PdhGpuEngineUsageRead.NotRequested; },
        ReadNvmlAsync = (_, _, _) => throw Unexpected(), ReadNvapiAsync = (_, _, _) => throw Unexpected(),
        ReadNvmlFanIdentities = static _ => [],
        ReadAdlxAsync = (_, _, _) => throw Unexpected(), ReadAmdSmu = _ => throw Unexpected(),
        ReadIntelGpu = (_, _, _) => throw Unexpected(),
        ReadIntelMsr = (targets, requested) =>
        {
            Reads++;
            if (requested.Count != 1 || !requested.TryGetValue(Identity, out var fields)
                || fields.Any(code => code is not (MetricFieldCode.IntelPsysEnergyCounter or MetricFieldCode.IntelPsysEnergyUnit)))
                throw Unexpected();
            LastFields = fields.ToHashSet();
            LastTarget = targets.Single(target => target.ExactIdentityKey == Identity);
            var values = new Dictionary<MetricFieldCode, double>();
            var unsigned = new Dictionary<MetricFieldCode, ulong>();
            if (fields.Contains(MetricFieldCode.IntelPsysEnergyCounter) && !FailRaw) unsigned.Add(MetricFieldCode.IntelPsysEnergyCounter, Raw);
            if (fields.Contains(MetricFieldCode.IntelPsysEnergyUnit) && !FailUnit) values.Add(MetricFieldCode.IntelPsysEnergyUnit, Unit);
            ReadCompleted?.Invoke();
            return [new IntelMsrMetricRead(Identity, values) { UnsignedValues = unsigned }];
        },
        ReadBatteryIdentities = static () => new(NativeMetricSnapshotCatalogTopologyStatus.Complete, []), ReadBattery = (_, _) => throw Unexpected(),
        ReadNetworkIdentities = static () => new(NativeMetricSnapshotCatalogTopologyStatus.Complete, []), ReadNetworkInterfaces = (_, _) => throw Unexpected(),
        ReadSystemIo = _ => throw Unexpected(), ReadStorageIdentities = static _ => new(NativeMetricSnapshotCatalogTopologyStatus.Complete, []),
        ReadFanIdentities = static _ => new(NativeMetricSnapshotCatalogTopologyStatus.Complete, []),
        ReadStorageSensors = () => throw Unexpected(), ReadNvmeSensors = _ => throw Unexpected(), ReadAtaSensors = _ => throw Unexpected(),
        ReadHardwareMonitor = () => throw Unexpected(), ReadNotebookFans = () => throw Unexpected(), SynchronizeNotebookDemand = static _ => { }
    };
    private Exception Unexpected()
    { UnselectedReads++; return new InvalidOperationException("Unselected PSys recorded source queried."); }
    private static CpuOsTopologyInformation CreateTopology()
    {
        var data = new CpuThreadCpuidData(new("GenuineIntel", 6, 0x4E, 0, "Recorded CPU", [], []),
            0x406E0, null, null, null, null, null, []);
        return new([new("recorded-core", 0, true, [69, 70])], [])
        { Packages = [new("windows-cpu-package|1:5,1:6", [70, 69])], ThreadCpuid = [new(70, data), new(69, data)] };
    }
    internal static ImmutableArray<FieldReference> Fields(ReadWriteCatalog catalog)
        => catalog.Fields.Where(field => field.Field.FieldName is RawName or UnitName)
            .Select(field => field.Field).OrderBy(field => field.FieldName, StringComparer.Ordinal).ToImmutableArray();
    internal static void RequireValues(ImmutableArray<FieldReading> readings, ulong raw, double unit)
    {
        foreach (var reading in readings)
            if (reading.Field.DeviceIdentity != Identity || reading.SourceId != NativeMetricSnapshotSources.IntelMsr
                || reading.Status != FieldObservationStatus.Current || reading.ObservedAt is null
                || (reading.Field.FieldName == RawName
                    ? reading.Value?.Integer != MetricInteger.FromUnsigned128((UInt128)raw) || reading.Value?.Number is not null
                    : reading.Value?.Number != unit || reading.Value?.Integer is not null))
                throw new InvalidDataException("PSys raw bits and nominal unit must retain exact independent typed values.");
    }
}

