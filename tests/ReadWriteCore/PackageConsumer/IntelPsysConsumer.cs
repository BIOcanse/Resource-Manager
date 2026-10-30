using System.Collections.Immutable;
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

internal static class IntelPsysConsumer
{
    internal static async Task RunRecordedAsync()
    {
        if (!OperatingSystem.IsWindows()) return;
        var fixture = new RecordedIntelPsysResources();
        using var owner = new WindowsHardwareMetricOwner(WindowsMetricConfiguration.CreateDefault(), fixture.Create(),
            RecordedIntelPsysResources.Options(), unavailableCooldown: TimeSpan.Zero);
        using var http = new HttpClient();
        var core = new ReadWriteCore(owner.CreateBackend(), new DependencyActions(http, []));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        Require(core.Catalog.Fields.IsEmpty && fixture.Reads == 0 && fixture.CatalogEngineProbeCount == 0,
            "construction and passive catalog do not capture");
        var fields = RecordedIntelPsysResources.Fields(await core.DiscoverAsync(timeout.Token));
        Require(fields.Length == 2 && fields.All(field => field.DeviceIdentity == RecordedIntelPsysResources.Identity),
            "one platform publishes exactly two fields without per-core/package copies");
        Require(owner.ReadCachedSnapshot() is null, "directory discovery does not publish numeric current");
        Require(fixture.Reads == 0, $"directory discovery does not read MSRs; actual reads={fixture.Reads}");
        Require(fixture.CatalogEngineProbeCount == 0,
            $"complete empty GPU inventory has no engine target to probe; actual probes={fixture.CatalogEngineProbeCount}");
        var raw = fields.Single(field => field.FieldName == RecordedIntelPsysResources.RawName);
        var unit = fields.Single(field => field.FieldName == RecordedIntelPsysResources.UnitName);
        Require(core.Query(raw) is { Read.Supported: true, Write.Supported: false,
                Definition: { ValueKind: "unsigned64", Unit: "count" }, ReadSource: NativeMetricSnapshotSources.IntelMsr }
            && core.Query(unit) is { Read.Supported: true, Write.Supported: false,
                Definition: { ValueKind: "float64", Unit: "J/count" }, ReadSource: NativeMetricSnapshotSources.IntelMsr },
            "public types/units/source and raw-zero validity limit are explicit");

        var zero = await core.CollectAsync(fields, timeout.Token);
        RecordedIntelPsysResources.RequireValues(zero, 0, Math.ScaleB(1d, -31));
        SameCurrent(zero);
        Require(fixture.Reads == 1 && fixture.LastFields.SetEquals([MetricFieldCode.IntelPsysEnergyCounter, MetricFieldCode.IntelPsysEnergyUnit])
            && fixture.LastTarget is { IsPlatform: true, DisplayIndex: 0, Processor: { ProcessorGroup: 1, GroupRelativeIndex: 5 } },
            "selected source receives one exact platform and the actual non-CPU0 representative");
        var current = owner.ReadCachedSnapshot();
        foreach (var field in fields) _ = core.ReadCached(field);
        Require(MetricSnapshotFactAssertions.SameCurrent(current, owner.ReadCachedSnapshot()) && fixture.Reads == 1,
            "passive current and cache have no collection side effect");

        fixture.Raw = uint.MaxValue;
        RecordedIntelPsysResources.RequireValues(await core.CollectAsync([raw], timeout.Token), uint.MaxValue, fixture.Unit);
        Require(fixture.LastFields.SetEquals([MetricFieldCode.IntelPsysEnergyCounter]), "raw-only demand excludes the unit register");
        fixture.Unit = 1;
        RecordedIntelPsysResources.RequireValues(await core.CollectAsync([unit], timeout.Token), fixture.Raw, 1);
        Require(fixture.LastFields.SetEquals([MetricFieldCode.IntelPsysEnergyUnit]), "unit-only demand excludes the raw counter");

        fixture.FailRaw = true;
        var rawFailed = await core.CollectAsync(fields, timeout.Token);
        Empty(rawFailed.Single(reading => reading.Field == raw));
        Require(rawFailed.Single(reading => reading.Field == unit).Value?.Number == 1, "unit success does not depend on raw success");
        SameCurrent(rawFailed);
        fixture.FailRaw = false; fixture.FailUnit = true;
        var unitFailed = await core.CollectAsync(fields, timeout.Token);
        Require(unitFailed.Single(reading => reading.Field == raw).Value?.Integer == MetricInteger.Parse("4294967295"), "raw32 maximum remains exact when unit fails");
        Empty(unitFailed.Single(reading => reading.Field == unit));
        fixture.FailRaw = true;
        var failed = await core.CollectAsync(fields, timeout.Token);
        foreach (var reading in failed) Empty(reading);
        Require(RecordedIntelPsysResources.Fields(core.Catalog).Length == 2, "source failures retain definitions and clear values");
        SameCurrent(failed);
        fixture.FailRaw = false; fixture.FailUnit = false;
        RecordedIntelPsysResources.RequireValues(await core.CollectAsync(fields, timeout.Token), fixture.Raw, fixture.Unit);

        var beforeCanceled = owner.ReadCachedSnapshot();
        using (var canceled = new CancellationTokenSource())
        {
            canceled.Cancel();
            try { await core.CollectAsync(fields, canceled.Token); throw new InvalidDataException("Canceled capture completed."); }
            catch (OperationCanceledException) { }
        }
        Require(MetricSnapshotFactAssertions.SameCurrent(beforeCanceled, owner.ReadCachedSnapshot()), "cancellation preserves current");
        var committedRaw = core.ReadCached(raw);
        fixture.Raw = 17;
        using (var canceledAfterRead = new CancellationTokenSource())
        {
            fixture.ReadCompleted = canceledAfterRead.Cancel;
            try { await core.CollectAsync([raw], canceledAfterRead.Token); throw new InvalidDataException("Read-then-canceled capture completed."); }
            catch (OperationCanceledException) { }
            finally { fixture.ReadCompleted = null; }
        }
        Require(MetricSnapshotFactAssertions.SameCurrent(beforeCanceled, owner.ReadCachedSnapshot()) && core.ReadCached(raw) == committedRaw,
            "canceling after a different raw read preserves committed value and observation time");

        await core.CollectAsync([unit], timeout.Token);
        Require(core.ReadCached(raw) == committedRaw && fixture.LastFields.SetEquals([MetricFieldCode.IntelPsysEnergyUnit]),
            "later unit-only capture cannot expose canceled raw bits or their observation time");
        using (var borrow = owner.BorrowSubscriptionCapture())
        {
            Require(ReferenceEquals(borrow.Backend, owner.CreateBackend()), "subscription borrow retains the original backend");
            borrow.Bind([raw]); borrow.SynchronizeDemand([raw]);
            var original = owner.ReadCachedSnapshot()!;
            var readsBeforeRejected = fixture.Reads;
            fixture.Raw = 23;
            var rejected = await borrow.CollectAsync([raw], new RetiredPermit(original.WorkspaceIdentity, original.ConfigurationGeneration), timeout.Token);
            Require(!rejected.Committed && MetricSnapshotFactAssertions.SameCurrent(original, owner.ReadCachedSnapshot()) && fixture.Reads == readsBeforeRejected + 1,
                "retired publication rejects a real different raw read without replacing current");
        }

        await core.CollectAsync([unit], timeout.Token);
        Require(core.ReadCached(raw) == committedRaw,
            "later unit-only capture cannot expose rejected borrowed raw bits or their observation time");
        fixture.Raw = uint.MaxValue;
        RecordedIntelPsysResources.RequireValues(await core.CollectAsync(fields, timeout.Token), fixture.Raw, fixture.Unit);
        Require(fixture.UnselectedReads == 0 && fixture.CatalogEngineProbeCount == 0, "PSys capture does not query other sources or GPU engines");

        fixture.Topology = null;
        fields = RecordedIntelPsysResources.Fields(await core.DiscoverAsync(timeout.Token));
        var sourceReads = fixture.Reads;
        var unavailableDirectory = await core.CollectAsync(fields, timeout.Token);
        Require(fields.Length == 2 && fixture.Reads == sourceReads, "unavailable platform directory retains definitions without reading its old target");
        foreach (var reading in unavailableDirectory) Empty(reading);
        fixture.Topology = new([], []) { Packages = [] };
        Require(RecordedIntelPsysResources.Fields(await core.DiscoverAsync(timeout.Token)).IsEmpty,
            "successful empty topology retires the platform");
        Require(fixture.UnselectedReads == 0 && fixture.CatalogEngineProbeCount == 0,
            "platform directory changes never query an absent GPU engine or unrelated value source");
        Console.WriteLine("[INTEL-PSYS-PACKAGE-RECORDED] one-platform; raw=0/4294967295; unit=J/count; selected-only; same-current; independent-failures; cancellation/retirement; no-live-MSR.");

        void SameCurrent(ImmutableArray<FieldReading> readings)
        {
            foreach (var reading in readings) Require(core.ReadCached(reading.Field) == reading, "collect/cache share original current");
        }
    }
    private static void Empty(FieldReading reading)
        => Require(reading is { Status: FieldObservationStatus.Unavailable, Value: null, ObservedAt: null }, "failed value is unavailable");
    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidDataException("Recorded PSys package: " + message); }
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

    internal Action? ReadCompleted { get; set; }
    internal HashSet<MetricFieldCode> LastFields { get; private set; } = [];
    internal IntelMsrCatalogIdentity? LastTarget { get; private set; }
    internal CpuOsTopologyInformation? Topology { get; set; } = CreateTopology();
    internal static WindowsMetricResourceOptions Options() => new()
        { IntelModulePath = "recorded-module-not-opened", PawnIoLibraryPath = "recorded-library-not-loaded" };

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
