using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.ReadWrite.Native;
using System.Collections.Immutable;

namespace ResourceManager.App.Infrastructure.Monitoring;

public sealed partial class LastSuccessfulHardwareMetricSnapshot : IDisposable
{
    // Only declarations live here. Every changing number, including publication
    // identities and timestamps, lives in the native session's current table.
    private NativeMetricSnapshotSession? currentSession;
    private readonly Func<NativeMetricSnapshotSession> createCurrentSession;
    private CurrentCatalog catalog = new();
    private bool disposed;

    public LastSuccessfulHardwareMetricSnapshot(string providerName,
        Func<NativeMetricSnapshotSession> createCurrentSession) : this(providerName)
    {
        this.createCurrentSession = createCurrentSession
            ?? throw new ArgumentNullException(nameof(createCurrentSession));
    }

    public void Dispose()
    {
        lock (sync)
        {
            if (disposed) return;
            currentSession?.Dispose();
            currentSession = null;
            catalog = new();
            histories.Clear();
            historyView = historyView.Clear();
            disposed = true;
        }
    }

    public HardwareMetricSnapshot? Read(MetricSampleRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (sync) return ReadState(request).Snapshot;
    }

    /// <summary>Reads only the selected current cells. No snapshot is reconstructed or retained.</summary>
    public IReadOnlyDictionary<string, MetricReadingValue> ReadItems(ImmutableArray<string> ids)
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var result = new Dictionary<string, MetricReadingValue>(StringComparer.OrdinalIgnoreCase);
            if (currentSession is null || ids.IsDefaultOrEmpty) return result;
            var selected = new List<(string Id, ulong Dataset, int Item)>();
            var keys = new List<NativeMetricCurrentKey>();
            foreach (var id in ids.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!catalog.DatasetHandles.TryGetValue(id, out var dataset)
                    && !catalog.DatasetHandles.TryGetValue(DirectDataset, out dataset)) continue;
                var item = catalog.ItemHandles.GetValueOrDefault(id, -1);
                selected.Add((id, dataset, item));
                foreach (var key in new ulong[] { 1, 3, 5 }) keys.Add(new() { DatasetHandle = dataset, CellHandle = key });
                if (item < 0) continue;
                var first = ItemCellBase + (ulong)item * 16;
                for (var offset = 0UL; offset <= 10; offset++)
                    keys.Add(new() { DatasetHandle = dataset, CellHandle = first + offset });
            }
            var frame = currentSession.ReadCurrent(keys.Distinct().ToArray());
            var rows = frame.Values.GroupBy(static cell => cell.DatasetHandle).ToDictionary(
                static group => group.Key, static group => group.ToDictionary(static cell => cell.CellHandle));
            foreach (var (id, dataset, item) in selected)
            {
                if (!rows.TryGetValue(dataset, out var cells)) continue;
                var r = new CellReader(cells, frame.Bytes, catalog);
                var status = (SamplingObservationStatus)r.Unsigned(3);
                var first = item >= 0 ? ItemCellBase + (ulong)item * 16 : 0;
                if (r.Flag(1) && item >= 0 && r.Flag(first))
                {
                    var value = ReadItem(r, first, catalog.Items[item]);
                    result.Add(id, catalog.DatasetIds[(int)dataset - 1] == DirectDataset ? value
                        : value with { Status = status, ObservedAt = r.Signed(5) > 0
                            ? new DateTimeOffset(r.Signed(5), TimeSpan.Zero) : null });
                }
                else if (catalog.FieldBindings.TryGetValue(id, out var binding))
                    result.Add(id, new(id, null, binding.Field.Unit)
                    {
                        FieldCode = binding.Field.Code, FieldName = binding.Field.Name,
                        DeviceIdentity = binding.DeviceIdentity, ValueKind = binding.Field.ValueKind,
                        SourceId = binding.SourceId, Status = status
                    });
            }
            return result;
        }
    }

    private PublicationState ReadState(MetricSampleRequest? request = null)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (currentSession is null) return PublicationState.Empty;
        var frame = request is null || request.IsAll || catalog.DatasetHandles.ContainsKey(DirectDataset)
            ? currentSession.ReadCurrent()
            : currentSession.ReadCurrent(SelectedDatasetCells(request));
        var datasets = new Dictionary<string, DatasetPublication>(StringComparer.OrdinalIgnoreCase);
        HardwareMetricSnapshot? direct = null;
        foreach (var group in frame.Values.GroupBy(static value => value.DatasetHandle))
        {
            var id = catalog.DatasetIds[checked((int)group.Key - 1)];
            var reader = new CellReader(group.ToDictionary(static value => value.CellHandle), frame.Bytes, catalog);
            var snapshot = ReadDataset(reader, id, catalog.FieldBindings);
            if (id == DirectDataset)
            {
                direct = snapshot with { Datasets = new Dictionary<string, HardwareMetricDatasetObservation>(), History = historyView };
                continue;
            }
            var observation = snapshot.Datasets[id];
            datasets.Add(id, new DatasetPublication(id, observation,
                reader.Flag(1) ? snapshot : null, snapshot,
                new DateTimeOffset(reader.Signed(2), TimeSpan.Zero)));
        }
        return new(datasets, direct ?? (datasets.Count == 0 ? null : Compose(datasets) with { History = historyView }));
    }

    private const string DirectDataset = "__direct";

    private NativeMetricCurrentKey[] SelectedDatasetCells(MetricSampleRequest request)
    {
        var keys = new List<NativeMetricCurrentKey>();
        var selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in request.Ids)
            Add(id);
        if (request.IncludesCatalog)
            foreach (var id in new[] { MetricDatasetIds.SystemMetricCatalog, MetricDatasetIds.StorageInventory,
                MetricDatasetIds.FanInventory, MetricDatasetIds.BatteryInventory, MetricDatasetIds.NumaInventory }) Add(id);
        if (request.IncludesPrefix("gpu.")) Add(MetricDatasetIds.SystemGpuInventory);
        if (request.IncludesAllGpuCoreMetrics)
            foreach (var (id, cells) in catalog.DatasetCells)
                if (request.Includes(id) && selected.Add(id)) keys.AddRange(cells);
        return keys.ToArray();

        void Add(string id)
        {
            if (selected.Add(id) && catalog.DatasetCells.TryGetValue(id, out var cells)) keys.AddRange(cells);
        }
    }
    private const ulong InventoryCellBase = 1UL << 40;
    private const ulong GpuCellBase = 2UL << 40;
    private const ulong ItemCellBase = 3UL << 40;
    private const ulong ItemCellEnd = 4UL << 40;

    private void WriteState(PublicationState next)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var nextCatalog = new CurrentCatalog();
        var cells = new List<NativeMetricCurrentValue>();
        var bytes = new List<byte>();
        var snapshots = next.Datasets.Values.OrderBy(static value => value.DatasetId, StringComparer.OrdinalIgnoreCase)
            .Select(static value => (value.DatasetId, value.AttemptSnapshot,
                Payload: value.PayloadSnapshot is not null, value.Observation, value.LastAttemptAt)).ToArray();
        if (snapshots.Length == 0 && next.Snapshot is { } direct)
            snapshots = [(DirectDataset, direct, true,
                new HardwareMetricDatasetObservation(DirectDataset, SamplingObservationStatus.Current, 0,
                    direct.CapturedAt.UtcTicks, direct.WorkspaceIdentity, direct.ConfigurationGeneration,
                    direct.CatalogGeneration, direct.CommittedGeneration), direct.CapturedAt)];
        var latest = snapshots.OrderByDescending(static value => value.LastAttemptAt).FirstOrDefault();
        nextCatalog.FieldBindings = latest.AttemptSnapshot?.FieldBindings
            ?? new Dictionary<string, MetricDeviceFieldBinding>();
        nextCatalog.Gpus = latest.AttemptSnapshot?.Gpus.Select(static gpu => new HardwareMetricGpuDescriptor(
            gpu.Index, gpu.Name, gpu.IdentityKey, gpu.AdapterKey, gpu.UsageProvider)).ToArray() ?? [];
        foreach (var entry in snapshots)
        {
            nextCatalog.DatasetIds.Add(entry.DatasetId);
            nextCatalog.DatasetHandles.Add(entry.DatasetId, (ulong)nextCatalog.DatasetIds.Count);
            var writer = new CellWriter((ulong)nextCatalog.DatasetIds.Count, cells, bytes, nextCatalog);
            WriteDataset(writer, entry.DatasetId, entry.AttemptSnapshot, entry.Payload, entry.Observation, entry.LastAttemptAt);
        }
        cells.Sort(static (left, right) => left.DatasetHandle != right.DatasetHandle
            ? left.DatasetHandle.CompareTo(right.DatasetHandle) : left.CellHandle.CompareTo(right.CellHandle));
        foreach (var group in cells.GroupBy(static cell => cell.DatasetHandle))
            nextCatalog.DatasetCells.Add(nextCatalog.DatasetIds[(int)group.Key - 1], group.Select(static cell =>
                new NativeMetricCurrentKey { DatasetHandle = cell.DatasetHandle, CellHandle = cell.CellHandle }).ToArray());
        // Byte offsets follow the final cell order, regardless of a caller's
        // dictionary enumeration order.
        var orderedBytes = new List<byte>(bytes.Count);
        for (var index = 0; index < cells.Count; index++)
        {
            var cell = cells[index];
            if (cell.ValueKind != (uint)NativeMetricSnapshotValueKind.Bytes || cell.Flags == 0) continue;
            var offset = orderedBytes.Count;
            orderedBytes.AddRange(bytes.GetRange(checked((int)cell.ValueBits), checked((int)cell.ValueHighBits)));
            cell.ValueBits = (ulong)offset;
            cells[index] = cell;
        }
        var requested = Enumerable.Range(1, Math.Max(catalog.DatasetIds.Count, nextCatalog.DatasetIds.Count))
            .Select(static value => (ulong)value).ToArray();
        currentSession ??= createCurrentSession();
        currentSession.CommitCurrent(requested, [], cells.ToArray(), orderedBytes.ToArray(),
            () => catalog = nextCatalog);
    }

    private sealed class CurrentCatalog
    {
        internal readonly List<string> DatasetIds = [];
        internal readonly Dictionary<string, ulong> DatasetHandles = new(StringComparer.OrdinalIgnoreCase);
        internal readonly Dictionary<string, NativeMetricCurrentKey[]> DatasetCells = new(StringComparer.OrdinalIgnoreCase);
        internal readonly List<string> Strings = [];
        internal readonly Dictionary<string, ulong> StringHandles = new(StringComparer.Ordinal);
        internal readonly List<ItemDeclaration> Items = [];
        internal readonly Dictionary<string, int> ItemHandles = new(StringComparer.OrdinalIgnoreCase);
        internal IReadOnlyDictionary<string, MetricDeviceFieldBinding> FieldBindings = new Dictionary<string, MetricDeviceFieldBinding>();
        internal IReadOnlyList<HardwareMetricGpuDescriptor> Gpus = [];
        internal ulong String(string? value)
        {
            if (value is null) return 0;
            if (StringHandles.TryGetValue(value, out var handle)) return handle;
            Strings.Add(value); handle = (ulong)Strings.Count; StringHandles.Add(value, handle); return handle;
        }
        internal int Item(MetricReadingValue value)
        {
            if (ItemHandles.TryGetValue(value.Id, out var handle)) return handle;
            handle = Items.Count;
            Items.Add(new(value.Id, value.Unit, value.FieldCode, value.FieldName,
                value.DeviceIdentity, value.ValueKind, value.SourceId));
            ItemHandles.Add(value.Id, handle);
            return handle;
        }
    }

    private sealed record ItemDeclaration(string Id, string Unit, MetricFieldCode FieldCode,
        string? FieldName, string? DeviceIdentity, string? ValueKind, string? SourceId);

    private sealed class CellWriter(ulong dataset, List<NativeMetricCurrentValue> cells,
        List<byte> bytes, CurrentCatalog catalog)
    {
        internal void Raw(ulong key, NativeMetricSnapshotValueKind kind, ulong low, ulong high = 0, bool present = true)
            => cells.Add(new() { DatasetHandle = dataset, CellHandle = key, ValueKind = (uint)kind,
                ValueBits = present ? low : 0, ValueHighBits = present ? high : 0, Flags = present ? 1U : 0U });
        internal void Unsigned(ulong key, ulong value) => Raw(key, NativeMetricSnapshotValueKind.Unsigned64, value);
        internal void Signed(ulong key, long value) => Raw(key, NativeMetricSnapshotValueKind.Signed64, unchecked((ulong)value));
        internal void Number(ulong key, double? value) => Raw(key, NativeMetricSnapshotValueKind.Float64,
            value.HasValue ? BitConverter.DoubleToUInt64Bits(value.Value) : 0, present: value.HasValue);
        internal void Flag(ulong key, bool value) => Raw(key, NativeMetricSnapshotValueKind.Boolean, value ? 1UL : 0);
        internal void Text(ulong key, string? value) => Unsigned(key, catalog.String(value));
        internal void Optional(ulong key, ulong? value) => Raw(key, NativeMetricSnapshotValueKind.Unsigned64, value ?? 0, present: value.HasValue);
        internal void Bytes(ulong key, byte[]? value)
        {
            Raw(key, NativeMetricSnapshotValueKind.Bytes, (ulong)bytes.Count, (ulong)(value?.Length ?? 0), value is not null);
            if (value is not null) bytes.AddRange(value);
        }
        internal int Item(MetricReadingValue value) => catalog.Item(value);
    }

    private sealed class CellReader(Dictionary<ulong, NativeMetricCurrentValue> cells, byte[] bytes, CurrentCatalog catalog)
    {
        internal IReadOnlyList<HardwareMetricGpuDescriptor> Gpus => catalog.Gpus;
        internal bool Has(ulong key) => cells.TryGetValue(key, out var value) && value.Flags == 1;
        private NativeMetricCurrentValue Value(ulong key, NativeMetricSnapshotValueKind kind)
        {
            if (!cells.TryGetValue(key, out var value)) return default;
            if (value.ValueKind != (uint)kind) throw new InvalidDataException("The native current cell has a different value kind from its declaration.");
            return value;
        }
        internal ulong Unsigned(ulong key) => Value(key, NativeMetricSnapshotValueKind.Unsigned64).ValueBits;
        internal long Signed(ulong key) => unchecked((long)Value(key, NativeMetricSnapshotValueKind.Signed64).ValueBits);
        internal double? Number(ulong key)
        {
            var value = Value(key, NativeMetricSnapshotValueKind.Float64);
            return value.Flags == 1 ? BitConverter.UInt64BitsToDouble(value.ValueBits) : null;
        }
        internal bool Flag(ulong key) => Value(key, NativeMetricSnapshotValueKind.Boolean).ValueBits != 0;
        internal ulong? Optional(ulong key) => Has(key) ? Unsigned(key) : null;
        internal string? Text(ulong key) => Unsigned(key) is var handle && handle != 0 ? catalog.Strings[checked((int)handle - 1)] : null;
        internal byte[]? Bytes(ulong key)
        {
            var value = Value(key, NativeMetricSnapshotValueKind.Bytes);
            return value.Flags == 1 ? bytes.AsSpan(checked((int)value.ValueBits), checked((int)value.ValueHighBits)).ToArray() : null;
        }
        internal MetricInteger? Integer(ulong key, ulong negativeKey)
        {
            var value = Value(key, NativeMetricSnapshotValueKind.Unsigned128);
            return value.Flags == 1 ? new MetricInteger(value.ValueBits, value.ValueHighBits, Flag(negativeKey)) : null;
        }
        internal IEnumerable<(ulong Key, ItemDeclaration Declaration)> Items()
            => cells.Keys.Where(static key => key >= ItemCellBase && key < ItemCellEnd && (key - ItemCellBase) % 16 == 0)
                .Select(key => (key, catalog.Items[checked((int)((key - ItemCellBase) / 16))]));
    }

    private static void WriteDataset(CellWriter w, string id, HardwareMetricSnapshot s, bool payload,
        HardwareMetricDatasetObservation o, DateTimeOffset attemptedAt)
    {
        w.Flag(1, payload); w.Signed(2, attemptedAt.UtcTicks); w.Unsigned(3, (ulong)o.Status);
        w.Unsigned(4, o.SourceGeneration); w.Signed(5, o.ObservedAtUtcTicks);
        w.Unsigned(6, o.WorkspaceIdentity); w.Unsigned(7, o.ConfigurationGeneration);
        w.Unsigned(8, o.CatalogGeneration); w.Unsigned(9, o.CommittedGeneration);
        w.Signed(10, o.LastAttemptAtUtcTicks); w.Signed(11, o.LastSuccessAtUtcTicks);
        w.Signed(12, o.ReadyUntilUtcTicks); w.Text(13, o.FailureCode); w.Signed(14, s.CapturedAt.UtcTicks);
        w.Text(20, s.Cpu.Name); w.Signed(21, s.Cpu.MaxFrequencyMhz); w.Text(22, s.Cpu.FrequencySource);
        w.Text(23, s.Cpu.Sensors.ProviderState.Provider); w.Text(24, s.Cpu.Sensors.ProviderState.State);
        w.Text(25, s.Cpu.Sensors.ProviderState.Message);
        var all = id == DirectDataset;
        if (payload && (all || id == MetricDatasetIds.SystemCpuUsage))
        {
            w.Number(30, s.Cpu.UsagePercent); w.Flag(31, s.Cpu.IsUsageAvailable);
            w.Unsigned(32, (ulong)s.Cpu.ObservationStatus); w.Unsigned(33, s.Cpu.SourceGeneration);
            w.Signed(34, s.Cpu.SampleDurationMilliseconds);
        }
        if (payload && (all || id == MetricDatasetIds.ForSystemMetric("cpu.frequency"))) w.Signed(35, s.Cpu.CurrentFrequencyMhz);
        if (payload && (all || id == MetricDatasetIds.ForSystemMetric("cpu.frequencyPercent"))) w.Number(36, s.Cpu.FrequencyPercent);
        var cpuSensors = s.Cpu.Sensors;
        var cpuNumbers = new double?[] { cpuSensors.PackagePowerWatts, cpuSensors.CoreVoltageVolts, cpuSensors.PackageCurrentAmps,
            cpuSensors.TemperatureCelsius, cpuSensors.StapmPowerWatts, cpuSensors.ActualPowerWatts, cpuSensors.AveragePowerWatts,
            cpuSensors.TdcCurrentAmps, cpuSensors.EdcCurrentAmps, cpuSensors.SocPowerWatts, cpuSensors.SocVoltageVolts,
            cpuSensors.ApuFrequencyMhz, cpuSensors.ApuVoltageVolts, cpuSensors.ApuTemperatureCelsius, cpuSensors.SmuFrequencyMhz };
        string[] cpuIds = ["packagePower", "coreVoltage", "packageCurrent", "temperature", "stapmPower", "actualPower", "averagePower",
            "tdcCurrent", "edcCurrent", "platformPower", "platformVoltage", "igpuFrequency", "igpuVoltage", "igpuTemperature", "smuFrequency"];
        for (var i = 0; i < cpuNumbers.Length; i++)
            if (payload && (all || id == MetricDatasetIds.ForSystemMetric("cpu." + cpuIds[i]))) w.Number((ulong)(40 + i), cpuNumbers[i]);
        if (payload && (all || id == MetricDatasetIds.SystemMemoryUsage))
        {
            w.Unsigned(60, s.Memory.UsedBytes); w.Unsigned(61, s.Memory.TotalBytes); w.Number(62, s.Memory.UsagePercent);
            w.Flag(63, s.Memory.IsUsageAvailable); w.Optional(64, s.Memory.AvailableBytes); w.Unsigned(65, (ulong)s.Memory.ObservationStatus);
        }
        if (payload && (all || id == MetricDatasetIds.SystemVirtualMemoryUsage))
        {
            w.Unsigned(70, s.VirtualMemory.UsedBytes); w.Unsigned(71, s.VirtualMemory.TotalBytes); w.Number(72, s.VirtualMemory.UsagePercent);
            w.Flag(73, s.VirtualMemory.IsSelectable); w.Optional(74, s.VirtualMemory.CommitPeakBytes);
            w.Optional(75, s.VirtualMemory.SystemCacheBytes); w.Optional(76, s.VirtualMemory.KernelPagedBytes);
            w.Optional(77, s.VirtualMemory.KernelNonpagedBytes); w.Optional(78, s.VirtualMemory.ProcessCount);
            w.Optional(79, s.VirtualMemory.ThreadCount); w.Optional(80, s.VirtualMemory.HandleCount);
            w.Unsigned(81, (ulong)s.VirtualMemory.ObservationStatus);
        }
        // Topology belongs to each observation; it cannot be replaced by the latest catalog's topology.
        w.Unsigned(90, (ulong)s.GpuInventory.Status); w.Unsigned(91, s.GpuInventory.Generation);
        w.Signed(92, s.GpuInventory.ObservedAtUtcTicks); w.Unsigned(93, s.GpuInventory.ObservedCount);
        w.Unsigned(94, s.GpuInventory.SkippedCount); w.Unsigned(95, s.GpuInventory.OverflowCount);
        w.Unsigned(96, s.GpuInventory.TopologyFingerprint);
        if (all || id == MetricDatasetIds.SystemGpuInventory)
        {
            w.Unsigned(97, (ulong)s.GpuInventory.Adapters.Count);
            for (var i = 0; i < s.GpuInventory.Adapters.Count; i++)
            {
                var a = s.GpuInventory.Adapters[i]; var k = InventoryCellBase + (ulong)i * 16;
                w.Signed(k, a.Index); w.Unsigned(k + 1, a.AdapterKey); w.Unsigned(k + 2, (ulong)a.CapabilityMask);
                w.Unsigned(k + 3, (ulong)a.ValidMetricMask); w.Unsigned(k + 4, (ulong)a.UsageStatus);
                w.Unsigned(k + 5, (ulong)a.CapacityStatus); w.Number(k + 6, a.UsagePercent);
                w.Unsigned(k + 7, a.UsedDedicatedMemoryBytes); w.Unsigned(k + 8, a.TotalDedicatedMemoryBytes);
                w.Unsigned(k + 9, a.Generation); w.Signed(k + 10, a.ObservedAtUtcTicks);
            }
        }
        var gpus = payload ? s.Gpus.Where(gpu => all || id.StartsWith($"gpu.{gpu.Index}.", StringComparison.OrdinalIgnoreCase)).ToArray() : [];
        w.Unsigned(98, (ulong)gpus.Length);
        for (var i = 0; i < gpus.Length; i++)
        {
            var g = gpus[i]; var k = GpuCellBase + (ulong)i * 64; var prefix = $"gpu.{g.Index}.";
            w.Signed(k, g.Index); w.Text(k + 1, g.Name); w.Text(k + 2, g.IdentityKey); w.Optional(k + 3, g.AdapterKey); w.Text(k + 4, g.UsageProvider);
            w.Text(k + 30, g.Sensors.ProviderState.Provider); w.Text(k + 31, g.Sensors.ProviderState.State); w.Text(k + 32, g.Sensors.ProviderState.Message);
            if (all || id == prefix + "usage") { w.Number(k + 5, g.UsagePercent); w.Flag(k + 6, g.IsUsageAvailable); }
            if (all || id == prefix + "graphicsClock") { w.Signed(k + 7, g.GraphicsFrequencyMhz); w.Signed(k + 8, g.StandardGraphicsFrequencyMhz); }
            if (all || id == prefix + "graphicsClockPercent") w.Number(k + 9, g.GraphicsFrequencyPercent);
            if (all || id == prefix + "memoryClock") w.Signed(k + 10, g.MemoryFrequencyMhz);
            if (all || id == prefix + "vram") { w.Unsigned(k + 11, g.UsedMemoryBytes); w.Unsigned(k + 12, g.TotalMemoryBytes); w.Number(k + 13, g.MemoryUsagePercent); }
            double?[] numbers = [g.Sensors.PowerWatts, g.Sensors.PowerLimitWatts, g.Sensors.BoardPowerWatts, g.Sensors.TemperatureCelsius,
                g.Sensors.HotspotTemperatureCelsius, g.Sensors.IntakeTemperatureCelsius, g.Sensors.FanSpeedPercent, g.Sensors.FanSpeedRpm,
                g.Sensors.CoreVoltageVolts, g.Sensors.CurrentAmps];
            string[] ids = ["power", "powerLimit", "boardPower", "temperature", "hotspotTemperature", "intakeTemperature", "fanPercent", "fanRpm", "coreVoltage", "current"];
            for (var j = 0; j < numbers.Length; j++) if (all || id == prefix + ids[j]) w.Number(k + 20 + (ulong)j, numbers[j]);
        }
        if (!payload) return;
        foreach (var value in s.Items.Values.Where(value => all || MetricDatasetIds.ForSystemMetric(value.Id).Equals(id, StringComparison.OrdinalIgnoreCase)))
        {
            var k = ItemCellBase + (ulong)w.Item(value) * 16;
            w.Flag(k, true); w.Number(k + 1, value.NumericValue); w.Number(k + 2, value.Percent); w.Number(k + 3, value.Total);
            w.Raw(k + 4, NativeMetricSnapshotValueKind.Unsigned128, value.IntegerValue?.LowBits ?? 0,
                value.IntegerValue?.HighBits ?? 0, value.IntegerValue.HasValue);
            w.Flag(k + 5, value.IntegerValue?.Negative == true); w.Bytes(k + 6, value.BytesValue);
            w.Raw(k + 7, NativeMetricSnapshotValueKind.Boolean, value.BooleanValue == true ? 1UL : 0, present: value.BooleanValue.HasValue);
            w.Unsigned(k + 8, (ulong)value.Status); w.Signed(k + 9, value.ObservedAt?.UtcTicks ?? 0);
            w.Optional(k + 10, value.SampleDurationMilliseconds);
        }
    }

    private static HardwareMetricSnapshot ReadDataset(CellReader r, string id,
        IReadOnlyDictionary<string, MetricDeviceFieldBinding> bindings)
    {
        var provider = new HardwareSensorProviderState(r.Text(23) ?? string.Empty, r.Text(24) ?? HardwareSensorProviderStates.Unavailable, r.Text(25));
        var cpu = new CpuMetrics(r.Text(20) ?? string.Empty, r.Number(30) ?? 0, r.Flag(31),
            (CpuMetricObservationStatus)r.Unsigned(32), r.Unsigned(33), r.Signed(34), (int)r.Signed(35),
            (int)r.Signed(21), r.Number(36) ?? 0, r.Text(22) ?? string.Empty,
            new(provider, r.Number(40), r.Number(41), r.Number(42), r.Number(43), r.Number(44), r.Number(45), r.Number(46),
                r.Number(47), r.Number(48), r.Number(49), r.Number(50), r.Number(51), r.Number(52), r.Number(53), r.Number(54)));
        var memory = new MemoryMetrics(r.Unsigned(60), r.Unsigned(61), r.Number(62) ?? 0, r.Flag(63))
            { AvailableBytes = r.Optional(64), ObservationStatus = (SamplingObservationStatus)r.Unsigned(65) };
        var virtualMemory = new VirtualMemoryMetrics(r.Unsigned(70), r.Unsigned(71), r.Number(72) ?? 0, r.Flag(73))
        {
            CommitPeakBytes = r.Optional(74), SystemCacheBytes = r.Optional(75), KernelPagedBytes = r.Optional(76),
            KernelNonpagedBytes = r.Optional(77), ProcessCount = (uint?)r.Optional(78), ThreadCount = (uint?)r.Optional(79),
            HandleCount = (uint?)r.Optional(80), ObservationStatus = (SamplingObservationStatus)r.Unsigned(81)
        };
        var adapters = new List<SchedulingGpuAdapterObservation>();
        for (var i = 0UL; i < r.Unsigned(97); i++)
        {
            var k = InventoryCellBase + i * 16;
            adapters.Add(new((int)r.Signed(k), r.Unsigned(k + 1), (SchedulingGpuCapabilityMask)r.Unsigned(k + 2),
                (SchedulingGpuMetricMask)r.Unsigned(k + 3), (SamplingObservationStatus)r.Unsigned(k + 4),
                (SamplingObservationStatus)r.Unsigned(k + 5), r.Number(k + 6) ?? 0, r.Unsigned(k + 7),
                r.Unsigned(k + 8), r.Unsigned(k + 9), r.Signed(k + 10)));
        }
        var inventory = new SchedulingGpuInventorySnapshot((SamplingObservationStatus)r.Unsigned(90), r.Unsigned(91),
            r.Signed(92), (uint)r.Unsigned(93), (uint)r.Unsigned(94), (uint)r.Unsigned(95), r.Unsigned(96), adapters);
        var gpus = r.Gpus.Select(EmptyGpu).ToList();
        for (var i = 0UL; i < r.Unsigned(98); i++)
        {
            var k = GpuCellBase + i * 64;
            var gpu = new GpuMetrics((int)r.Signed(k), r.Text(k + 1) ?? string.Empty, r.Number(k + 5) ?? 0,
                (int)r.Signed(k + 7), (int)r.Signed(k + 8), r.Number(k + 9) ?? 0, (int)r.Signed(k + 10),
                r.Unsigned(k + 11), r.Unsigned(k + 12), r.Number(k + 13) ?? 0,
                new(new(r.Text(k + 30) ?? string.Empty, r.Text(k + 31) ?? HardwareSensorProviderStates.Unavailable, r.Text(k + 32)),
                    r.Number(k + 20), r.Number(k + 21), r.Number(k + 22), r.Number(k + 23), r.Number(k + 24),
                    r.Number(k + 25), r.Number(k + 26), r.Number(k + 27), r.Number(k + 28), r.Number(k + 29)),
                r.Text(k + 4) ?? string.Empty, r.Flag(k + 6), r.Text(k + 2)) { AdapterKey = r.Optional(k + 3) };
            var found = gpus.FindIndex(value => value.Index == gpu.Index);
            if (found < 0) gpus.Add(gpu); else gpus[found] = gpu;
        }
        var items = new Dictionary<string, MetricReadingValue>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, declaration) in r.Items())
        {
            items.Add(declaration.Id, ReadItem(r, k, declaration));
        }
        var observation = new HardwareMetricDatasetObservation(id, (SamplingObservationStatus)r.Unsigned(3), r.Unsigned(4),
            r.Signed(5), r.Unsigned(6), r.Unsigned(7), r.Unsigned(8), r.Unsigned(9))
        {
            LastAttemptAtUtcTicks = r.Signed(10), LastSuccessAtUtcTicks = r.Signed(11), ReadyUntilUtcTicks = r.Signed(12), FailureCode = r.Text(13)
        };
        return new(new DateTimeOffset(r.Signed(14), TimeSpan.Zero), cpu, memory, virtualMemory, gpus, inventory, items)
        {
            FieldBindings = bindings, Datasets = new Dictionary<string, HardwareMetricDatasetObservation>(StringComparer.OrdinalIgnoreCase) { [id] = observation },
            WorkspaceIdentity = observation.WorkspaceIdentity, ConfigurationGeneration = observation.ConfigurationGeneration,
            CatalogGeneration = observation.CatalogGeneration, CommittedGeneration = observation.CommittedGeneration
        };
    }

    private static MetricReadingValue ReadItem(CellReader r, ulong k, ItemDeclaration declaration)
        => new(declaration.Id, r.Number(k + 1), declaration.Unit, r.Number(k + 2), r.Number(k + 3))
        {
            FieldCode = declaration.FieldCode, FieldName = declaration.FieldName, DeviceIdentity = declaration.DeviceIdentity,
            ValueKind = declaration.ValueKind, SourceId = declaration.SourceId, IntegerValue = r.Integer(k + 4, k + 5),
            BytesValue = r.Bytes(k + 6), BooleanValue = r.Has(k + 7) ? r.Flag(k + 7) : null,
            Status = (SamplingObservationStatus)r.Unsigned(k + 8),
            ObservedAt = r.Signed(k + 9) > 0 ? new DateTimeOffset(r.Signed(k + 9), TimeSpan.Zero) : null,
            SampleDurationMilliseconds = r.Optional(k + 10)
        };

    private static GpuMetrics EmptyGpu(HardwareMetricGpuDescriptor gpu) => new(gpu.Index, gpu.Name,
        0, 0, 0, 0, 0, 0, 0, 0,
        new(new(string.Empty, HardwareSensorProviderStates.NotRequested, null), null, null, null,
            null, null, null, null, null, null, null), gpu.UsageProvider, false, gpu.IdentityKey)
        { AdapterKey = gpu.AdapterKey };
}
