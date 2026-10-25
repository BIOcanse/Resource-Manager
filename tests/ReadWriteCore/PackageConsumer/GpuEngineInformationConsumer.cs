using System.Collections.Immutable;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Dependencies;
using ResourceManager.ReadWrite.Native;
using ResourceManager.ReadWrite.Native.Pdh;
using ResourceManager.ReadWrite.Windows.Cpu.Models;
using ResourceManager.ReadWrite.Windows.Devices.Battery;
using ResourceManager.ReadWrite.Windows.Gpu;
using ResourceManager.ReadWrite.Windows.Metrics;
using ResourceManager.ReadWrite.Windows.Metrics.Configuration;
using ResourceManager.ReadWrite.Windows.Metrics.Resources;
using ResourceManager.ReadWrite.Windows.Metrics.Writes;
using ResourceManager.ReadWrite.Windows.Pdh;

internal static class GpuEngineInformationConsumer
{
    private const string FieldName = "gpu_all_engine_busy_percent";

    // Uses the public native-PDH source seam, the production PDH reader, the
    // packaged metric owner/capture/current, and the public ReadWrite API.
    // It supplies recorded GPU inventory and original NativePdhSnapshot rows;
    // no fake IReadWriteBackend or alternate field projection is involved.
    internal static async Task RunRecordedAsync()
    {
        if (!OperatingSystem.IsWindows()) return;

        var inventory = CreateInventory();
        var fingerprint = WindowsGpuAdapterIdentity.ComputeTopologyFingerprint(inventory.Adapters);
        Require(inventory.TopologyFingerprint == fingerprint, "Recorded GPU inventory has its exact LUID/physical topology fingerprint.");

        var recordedAt = new DateTimeOffset(2026, 10, 9, 18, 0, 0, TimeSpan.Zero);
        var nativeSource = new RecordedPdhSnapshotSource(fingerprint,
        [
            NativePdhReadResultFor(Snapshot(recordedAt, 1)),
            NativePdhReadResultFor(Snapshot(recordedAt.AddSeconds(1), 2)),
            NativePdhReadResultFor(Snapshot(recordedAt.AddSeconds(2), 3)),
            new(null, NativePdhProviderAvailability.Unavailable, NativePdhResultCode.Unavailable, 0)
        ]);
        var pdhReader = new PdhGpuEngineUsageReader(nativeSource);
        var inventoryReads = 0;
        var resources = CreateResources(inventory, pdhReader, () => inventoryReads++);

        using var owner = new WindowsHardwareMetricOwner(WindowsMetricConfiguration.CreateDefault(), resources,
            new WindowsMetricResourceOptions());
        using var http = new HttpClient();
        var core = new ReadWriteCore(owner.CreateBackend(), new DependencyActions(http, []));
        Require(nativeSource.ReadCount == 0 && owner.ReadCachedSnapshot() is null,
            "Construction and the initial owner current read do not query PDH or publish a current.");
        Require(core.Catalog.Fields.IsEmpty && nativeSource.ReadCount == 0,
            "Reading the un-discovered catalog does not query PDH.");

        var catalog = await core.DiscoverAsync();
        Require(nativeSource.ReadCount == 1 && inventoryReads > 0 && owner.ReadCachedSnapshot() is null,
            "Explicit discovery uses the original PDH source to enumerate engine identities without publishing numeric current.");

        var expected = new Dictionary<NativePdhGpuEngineIdentity, double>
        {
            [new NativePdhGpuEngineIdentity(LuidA, 0, 2, NativePdhEngineClass.Compute)] = 22,
            [new NativePdhGpuEngineIdentity(LuidA, 0, 2, NativePdhEngineClass.RayTracing)] = 8,
            [new NativePdhGpuEngineIdentity(LuidA, 0, 3, NativePdhEngineClass.Other)] = 61,
            [new NativePdhGpuEngineIdentity(LuidA, 1, 2, NativePdhEngineClass.Compute)] = 34,
            [new NativePdhGpuEngineIdentity(LuidB, 0, 2, NativePdhEngineClass.Compute)] = 72
        };
        var engineFields = catalog.Fields.Where(static item => item.Definition.Name == FieldName).ToArray();
        Require(engineFields.Length == expected.Count
            && engineFields.Select(static item => item.Field.DeviceIdentity).ToHashSet(StringComparer.Ordinal)
                .SetEquals(expected.Keys.Select(DeviceIdentity)),
            "The public catalog keeps every exact adapter-LUID/physical-index/engine-index/native-class identity separate.");

        var selectedIdentity = new NativePdhGpuEngineIdentity(LuidA, 0, 2, NativePdhEngineClass.Compute);
        var selected = Find(engineFields, selectedIdentity);
        foreach (var capability in engineFields)
        {
            var support = core.Query(capability.Field);
            Require(support.Read.Supported && support.Read.Dependencies.IsEmpty
                && support.ReadSource == NativeMetricSnapshotSources.PdhGpuEngine
                && !support.Write.Supported,
                "The field is read-only and sourced only from the original PDH GPU-engine provider.");
        }

        var undiscoveredCurrent = core.ReadCached(selected.Field);
        Require(undiscoveredCurrent.Status != FieldObservationStatus.Current
            && undiscoveredCurrent.Value is null && undiscoveredCurrent.ObservedAt is null
            && nativeSource.ReadCount == 1,
            "Cached access after catalog discovery does not resample or expose the discovery payload as numeric current.");

        var read = (await core.CollectAsync([selected.Field])).Single();
        Require(read.Status == FieldObservationStatus.Current && read.Value?.Number == expected[selectedIdentity]
            && read.ObservedAt == recordedAt.AddSeconds(1)
            && read.SourceId == NativeMetricSnapshotSources.PdhGpuEngine
            && nativeSource.ReadCount == 2,
            "One explicit selected field request reaches the original PDH engine reader and preserves its observation time/value.");

        var sameAdapter = expected.Where(pair => pair.Key.AdapterLuid == LuidA).ToArray();
        foreach (var (identity, value) in sameAdapter)
        {
            var capability = Find(engineFields, identity);
            var cached = core.ReadCached(capability.Field);
            Require(cached.Status == FieldObservationStatus.Current && cached.Value?.Number == value
                && cached.ObservedAt == read.ObservedAt,
                "Distinct engines on the selected adapter retain their own exact value and are not summed together.");
        }
        var otherAdapter = core.ReadCached(Find(engineFields,
            new NativePdhGpuEngineIdentity(LuidB, 0, 2, NativePdhEngineClass.Compute)).Field);
        Require(otherAdapter.Value is null && otherAdapter.ObservedAt is null,
            "Selecting one adapter does not publish the other LUID's engine values.");

        var current = owner.ReadCachedSnapshot();
        Require(current is not null && core.ReadCached(selected.Field) == read
            && MetricSnapshotFactAssertions.SameCurrent(current, owner.ReadCachedSnapshot()) && nativeSource.ReadCount == 2,
            "Public cached reads return the original owner current without starting another PDH query.");
        Require(current!.Items.Keys.All(static key => !key.EndsWith(".engineBusy", StringComparison.OrdinalIgnoreCase))
            && current.Datasets.Keys.All(static key => !key.EndsWith(".engineBusy", StringComparison.OrdinalIgnoreCase))
            && current.FieldBindings.Values.All(static binding => binding.Field.Name != FieldName
                || binding.PdhGpuEngineIdentity is not null),
            "The original committed current exposes exact engines without the internal adapter sampling trigger.");

        using var borrow = owner.BorrowSubscriptionCapture();
        Require(nativeSource.ReadCount == 2, "Borrow construction does not start a PDH query.");
        var borrowedBinding = borrow.Bind([selected.Field]).Single();
        var expectedPerEngineKey =
            $"gpu-engine/{selectedIdentity.AdapterLuid:X16}/{selectedIdentity.PhysicalIndex}/{selectedIdentity.EngineIndex}/{(uint)selectedIdentity.EngineClass}/busyPercent";
        var engineBindings = current!.FieldBindings.Values
            .Where(static binding => binding.PdhGpuEngineIdentity is not null)
            .ToArray();
        var selectedCurrentBinding = engineBindings.Single(binding =>
            binding.PdhGpuEngineIdentity == selectedIdentity
            && binding.AdapterKey == selectedIdentity.AdapterLuid);
        var expectedAdapterQueryKey = MetricDatasetIds.ForSystemMetric(
            selectedCurrentBinding.EffectiveSampleRequestAddress);
        var sameAdapterQueryKeys = engineBindings
            .Where(binding => binding.PdhGpuEngineIdentity!.Value.AdapterLuid == selectedIdentity.AdapterLuid)
            .Select(binding => MetricDatasetIds.ForSystemMetric(binding.EffectiveSampleRequestAddress))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var otherAdapterQueryKeys = engineBindings
            .Where(binding => binding.PdhGpuEngineIdentity!.Value.AdapterLuid != selectedIdentity.AdapterLuid)
            .Select(binding => MetricDatasetIds.ForSystemMetric(binding.EffectiveSampleRequestAddress))
            .ToArray();
        Require(engineBindings.All(binding =>
                !current.Items.ContainsKey(binding.EffectiveSampleRequestAddress)
                && !current.Datasets.ContainsKey(binding.EffectiveSampleRequestAddress)),
            "Canonical internal adapter query keys remain absent from the committed public items and datasets.");
        var actualBindingDiagnostic = $"field={borrowedBinding.Field}; "
            + $"collection=[{string.Join(", ", borrowedBinding.CollectionItemIds)}]; "
            + $"publication=[{string.Join(", ", borrowedBinding.PublicationKeys)}]; "
            + $"sourceKind={borrowedBinding.SourceKind}; reads={nativeSource.ReadCount}; "
            + $"expectedAdapterQueryKey={expectedAdapterQueryKey}; "
            + $"sameLuidQueryKeys=[{string.Join(", ", sameAdapterQueryKeys)}]; "
            + $"otherLuidQueryKeys=[{string.Join(", ", otherAdapterQueryKeys)}]";
        Require(borrowedBinding.Field == selected.Field
            && borrowedBinding.CollectionItemIds.SequenceEqual([expectedPerEngineKey, MetricDatasetIds.SystemGpuInventory])
            && borrowedBinding.PublicationKeys.SequenceEqual([expectedAdapterQueryKey])
            && expectedAdapterQueryKey.EndsWith("/" + FieldName, StringComparison.Ordinal)
            && !string.Equals(expectedAdapterQueryKey, expectedPerEngineKey, StringComparison.OrdinalIgnoreCase)
            && sameAdapterQueryKeys.Length == 1
            && string.Equals(sameAdapterQueryKeys[0], expectedAdapterQueryKey, StringComparison.OrdinalIgnoreCase)
            && otherAdapterQueryKeys.Length > 0
            && otherAdapterQueryKeys.All(key => !string.Equals(key, expectedAdapterQueryKey, StringComparison.OrdinalIgnoreCase))
            && borrowedBinding.SourceKind == FieldCollectionSourceKind.SourceQuery
            && nativeSource.ReadCount == 2,
            $"Subscription bind retains the exact engine publication item and adapter query key as a source query without sampling. Actual: {actualBindingDiagnostic}; "
            + $"expected collection=[{expectedPerEngineKey}, {MetricDatasetIds.SystemGpuInventory}], "
            + $"publication=[{expectedAdapterQueryKey}], sourceKind={FieldCollectionSourceKind.SourceQuery}, reads=2.");
        borrow.SynchronizeDemand([selected.Field]);
        var borrowedCached = borrow.Backend.ReadCached(selected.Field);
        Require(borrowedCached == read && nativeSource.ReadCount == 2,
            "Demand synchronization and borrowed cached access use the same current without a PDH query.");

        var activeCurrent = owner.ReadCachedSnapshot();
        var borrowedResult = await borrow.CollectAsync([selected.Field],
            new AcceptingPermit(activeCurrent!.WorkspaceIdentity, activeCurrent.ConfigurationGeneration));
        var borrowedReading = borrowedResult.Readings.Single();
        Require(borrowedResult.Committed && borrowedResult.QueriedFields.SequenceEqual([selected.Field])
            && borrowedReading.Status == FieldObservationStatus.Current
            && borrowedReading.Value?.Number == expected[selectedIdentity]
            && borrowedReading.ObservedAt == recordedAt.AddSeconds(2)
            && borrowedReading.SourceId == NativeMetricSnapshotSources.PdhGpuEngine
            && nativeSource.ReadCount == 3,
            "An accepting permit commits one exact queried engine reading through the original owner borrow.");
        var borrowedCurrent = owner.ReadCachedSnapshot();
        Require(borrowedCurrent is not null && MetricSnapshotFactAssertions.SameCurrent(borrowedCurrent, owner.ReadCachedSnapshot())
            && borrow.Backend.ReadCached(selected.Field) == borrowedReading
            && core.ReadCached(selected.Field) == borrowedReading && nativeSource.ReadCount == 3,
            "Borrowed and direct APIs read the same committed current without another PDH query.");
        borrow.Dispose();
        Require(nativeSource.ReadCount == 3, "Disposing the borrow does not query PDH or dispose the caller-owned metric owner.");

        var failed = (await core.CollectAsync([selected.Field])).Single();
        Require(nativeSource.ReadCount == 4 && failed.Status == FieldObservationStatus.Unavailable
            && failed.Value is null && failed.ObservedAt is null,
            "Direct collection remains available after borrow disposal and a PDH provider failure clears value and time.");
        var failedCached = core.ReadCached(selected.Field);
        Require(failedCached.Status == FieldObservationStatus.Unavailable
            && failedCached.Value is null && failedCached.ObservedAt is null
            && nativeSource.ReadCount == 4,
            "The same original current exposes the unavailable failure without stale numeric data or a cache-triggered query.");

        Require(nativeSource.TopologyFingerprints.Count == 4
            && nativeSource.TopologyFingerprints.All(value => value == fingerprint),
            "Every PDH query is scoped to the recorded exact GPU topology fingerprint.");
        Console.WriteLine("[GPU-ENGINE-PACKAGE] recorded NativePdhSnapshot through public PDH reader, packaged metric owner/current, exact per-engine cache, and original subscription borrow passed; no vendor reader or live PDH query.");
    }

    private static WindowsHardwareMetricResources CreateResources(WindowsGpuAdapterInventoryRead inventory,
        PdhGpuEngineUsageReader pdhReader, Action inventoryRead)
        => new()
        {
            Controls = new WindowsMetricControlResources
            {
                ControlReadCpuIdentity = static () => null,
                ControlReadCpuTopology = static () => new CpuOsTopologyInformation(null, null)
            },
            ReadCpuCounters = static () => throw new InvalidOperationException("Unselected CPU telemetry was queried."),
            ReadMemory = static () => throw new InvalidOperationException("Unselected memory telemetry was queried."),
            ReadVirtualMemory = static () => throw new InvalidOperationException("Unselected virtual-memory telemetry was queried."),
            ReadNumaAvailable = static (_, _) => throw new InvalidOperationException("Unselected NUMA telemetry was queried."),
            ReadNumaIdentities = static () => new(NativeMetricSnapshotCatalogTopologyStatus.Complete, []),
            ReadGpuInventory = () => { inventoryRead(); return inventory; },
            ReadCpuFrequency = static () => throw new InvalidOperationException("Unselected CPU-frequency telemetry was queried."),
            ReadGpuUsage = pdhReader.ReadUsageSnapshotByAdapterIndex,
            ReadNvmlAsync = static (_, _, _) => throw new InvalidOperationException("Unselected NVML telemetry was queried."),
            ReadNvapiAsync = static (_, _, _) => throw new InvalidOperationException("Unselected NVAPI telemetry was queried."),
            ReadAdlxAsync = static (_, _, _) => throw new InvalidOperationException("Unselected ADLX telemetry was queried."),
            ReadAmdSmu = static _ => throw new InvalidOperationException("Unselected AMD SMU telemetry was queried."),
            ReadIntelGpu = static (_, _, _) => throw new InvalidOperationException("Unselected Intel GPU telemetry was queried."),
            ReadIntelMsr = static (_, _) => throw new InvalidOperationException("Unselected Intel MSR telemetry was queried."),
            ReadBatteryIdentities = static () => new(NativeMetricSnapshotCatalogTopologyStatus.Complete, []),
            ReadBattery = static (_, _) => throw new InvalidOperationException("Unselected battery telemetry was queried."),
            ReadNetworkIdentities = static () => new(NativeMetricSnapshotCatalogTopologyStatus.Complete, []),
            ReadNetworkInterfaces = static (_, _) => throw new InvalidOperationException("Unselected network telemetry was queried."),
            ReadSystemIo = static _ => throw new InvalidOperationException("Unselected system-I/O telemetry was queried."),
            ReadStorageIdentities = static _ => new(NativeMetricSnapshotCatalogTopologyStatus.Complete, []),
            ReadFanIdentities = static _ => new(NativeMetricSnapshotCatalogTopologyStatus.Complete, []),
            ReadStorageSensors = static () => throw new InvalidOperationException("Unselected storage-sensor telemetry was queried."),
            ReadNvmeSensors = static _ => throw new InvalidOperationException("Unselected NVMe telemetry was queried."),
            ReadAtaSensors = static _ => throw new InvalidOperationException("Unselected ATA telemetry was queried."),
            ReadHardwareMonitor = static () => throw new InvalidOperationException("Unselected WMI sensor telemetry was queried."),
            ReadNotebookFans = static () => throw new InvalidOperationException("Unselected notebook-fan telemetry was queried."),
            SynchronizeNotebookDemand = static _ => { }
        };

    private static WindowsGpuAdapterInventoryRead CreateInventory()
    {
        var adapters = new[]
        {
            CreateAdapter(0, 1, 8, [PhysicalIdentity(0, "RECORDED-A-0"), PhysicalIdentity(1, "RECORDED-A-1")]),
            CreateAdapter(1, 2, 9, [PhysicalIdentity(0, "RECORDED-B-0")])
        };
        var observedAt = DateTimeOffset.UtcNow;
        return new(SamplingObservationStatus.Current, 1, observedAt.UtcTicks,
            checked((uint)adapters.Length), 0, 0,
            WindowsGpuAdapterIdentity.ComputeTopologyFingerprint(adapters), adapters);
    }

    private static WindowsGpuAdapter CreateAdapter(int index, uint lowPart, int highPart,
        IReadOnlyList<WindowsGpuPhysicalIdentity> physicalIdentities)
        => new(index, $"Recorded GPU {index}", 0x10DE, 0x2684, 1,
            new AdapterLuid { LowPart = lowPart, HighPart = highPart }, false,
            8UL * 1024 * 1024 * 1024, WindowsGpuAdapterKind.Dedicated)
        {
            PhysicalIdentityStatus = SamplingObservationStatus.Current,
            PhysicalIdentities = physicalIdentities
        };

    private static WindowsGpuPhysicalIdentity PhysicalIdentity(uint index, string suffix)
        => new(index, $@"PCI\VEN_10DE&DEV_2684\{suffix}", WindowsGpuPnpKind.Pci,
            true, true, 0, 1, index, 0, 0x10DE, 0x2684, 0x10DE, 1, 1,
            WindowsGpuIdentityEvidence.KmtHardwarePnpKey | WindowsGpuIdentityEvidence.CmExactDevNode,
            WindowsGpuPhysicalIdentityStatus.Bound, 0);

    private static NativePdhReadResult NativePdhReadResultFor(NativePdhSnapshot snapshot)
        => new(snapshot, NativePdhProviderAvailability.Available, NativePdhResultCode.Ok, 0);

    private static NativePdhSnapshot Snapshot(DateTimeOffset observedAt, ulong sequence)
    {
        NativePdhGpuEngineRow Row(ulong luid, uint process, uint physical, uint engine,
            NativePdhEngineClass engineClass, double percent)
            => new()
            {
                AdapterLuid = luid,
                ProcessId = process,
                PhysicalIndex = physical,
                EngineIndex = engine,
                EngineClass = engineClass,
                UsagePercent = percent
            };

        IReadOnlyList<NativePdhGpuEngineRow> rows =
        [
            Row(LuidA, 0, 0, 2, NativePdhEngineClass.Compute, 13),
            Row(LuidA, 101, 0, 2, NativePdhEngineClass.Compute, 9),
            Row(LuidA, 0, 0, 2, NativePdhEngineClass.RayTracing, 8),
            Row(LuidA, 0, 0, 3, NativePdhEngineClass.Other, 61),
            Row(LuidA, 0, 1, 2, NativePdhEngineClass.Compute, 34),
            Row(LuidB, 0, 0, 2, NativePdhEngineClass.Compute, 72)
        ];
        return new NativePdhSnapshot(observedAt, sequence, 1, rows, [], default,
            NativePdhFrameFlags.HasData | NativePdhFrameFlags.GpuEngineComplete,
            NativePdhDatasetObservation.Current(observedAt, sequence),
            NativePdhDatasetObservation.Unavailable,
            NativePdhDatasetObservation.Unavailable,
            NativePdhDatasetObservation.Unavailable);
    }

    private static FieldCapability Find(IReadOnlyList<FieldCapability> fields, NativePdhGpuEngineIdentity identity)
        => fields.Single(item => item.Field.DeviceIdentity == DeviceIdentity(identity));

    private static string DeviceIdentity(NativePdhGpuEngineIdentity identity)
        => $"gpu-engine|{identity.AdapterLuid:X16}|{identity.PhysicalIndex}|{identity.EngineIndex}|{(uint)identity.EngineClass}";

    private const ulong LuidA = 0x0000_0008_0000_0001;
    private const ulong LuidB = 0x0000_0009_0000_0002;

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }

    private sealed class AcceptingPermit(ulong workspaceIncarnation, ulong configurationGeneration)
        : IMetricPublicationPermit
    {
        public ulong WorkspaceIncarnation { get; } = workspaceIncarnation;
        public ulong ConfigurationGeneration { get; } = configurationGeneration;
        public bool IsActive => true;
        public bool TryPublish(Action commit) { commit(); return true; }
    }

    private sealed class RecordedPdhSnapshotSource(ulong expectedTopologyFingerprint,
        IEnumerable<NativePdhReadResult> results) : INativePdhSnapshotSource
    {
        private readonly Queue<NativePdhReadResult> pending = new(results);
        public List<ulong?> TopologyFingerprints { get; } = [];
        public int ReadCount => TopologyFingerprints.Count;

        public NativePdhReadResult Read(ulong? adapterTopologyFingerprint = null)
        {
            TopologyFingerprints.Add(adapterTopologyFingerprint);
            if (adapterTopologyFingerprint != expectedTopologyFingerprint)
                throw new InvalidDataException("PDH query used a different adapter topology fingerprint.");
            if (pending.Count == 0) throw new InvalidOperationException("Unexpected additional PDH query.");
            return pending.Dequeue();
        }
    }
}
