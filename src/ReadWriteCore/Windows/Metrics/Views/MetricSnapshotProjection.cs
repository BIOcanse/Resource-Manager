using System.Collections.Immutable;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.ReadWrite.Native.Pdh;
using System.Text;

namespace ResourceManager.ReadWrite.Windows.Metrics.Views;

/// <summary>Cold immutable SDK projection only. All selection, health and history decisions are inputs.</summary>
internal static class MetricSnapshotProjection
{
    internal static HardwareMetricSnapshot Create(MetricObjectVersion version, MetricSnapshotInput input)
    {
        var current = input.Values; var catalog = input.Catalog;
        if (catalog.Count != current.Count || input.GpuProviders.Length != catalog.Gpus.Length
            || input.GpuUsageAvailable.Length != catalog.Gpus.Length
            || !input.Percents.IsEmpty && input.Percents.Length != current.Count
            || !input.Totals.IsEmpty && input.Totals.Length != current.Count
            || input.EngineBaseMetricIndices.Length != input.Engines.Count)
            throw new InvalidDataException("Metric projection columns do not match the admitted catalog.");
        var items = ImmutableDictionary.CreateBuilder<string, MetricReadingValue>(StringComparer.OrdinalIgnoreCase);
        var bindings = ImmutableDictionary.CreateBuilder<string, MetricDeviceFieldBinding>(StringComparer.OrdinalIgnoreCase);
        var index = 0;
        while (index < current.Count)
        {
            if (Suppressed(input, catalog.Items[index].Id)) { index++; continue; }
            if (current.Identity(index) != catalog.Identities[index])
                throw new InvalidDataException("Metric catalog identity has not arrived.");
            if (current.TryRead(index, out var value) != MetricViewReadStatus.Available)
                throw new InvalidDataException("Metric rule value has not arrived or is malformed.");
            var descriptor = catalog.Items[index];
            if (catalog.Bindings[index] is { } binding) bindings.Add(descriptor.Id, binding);
            // The product object exposes only accepted payloads. Typed null is an
            // accepted payload; native typed absence still remains in the direct view.
            if (value.Presence == MetricValuePresence.Absent) { index++; continue; }
            items.Add(descriptor.Id, new(descriptor.Id, value.Float64, descriptor.Unit,
                input.Percents.IsEmpty ? null : input.Percents[index], input.Totals.IsEmpty ? null : input.Totals[index])
            {
                FieldCode = descriptor.FieldCode, FieldName = descriptor.FieldName,
                DeviceIdentity = descriptor.DeviceIdentity, SourceId = descriptor.SourceId,
                // The composed object preserves the accepted payload's annotation, as the
                // active product does. Current acquisition health stays in the dataset/view.
                ValueKind = KindName(value.Kind), Status = value.Status == SamplingObservationStatus.RetainedLastGood
                    ? SamplingObservationStatus.Current : value.Status,
                ObservedAt = value.ObservedAtUtcTicks == 0 ? null : new DateTimeOffset(value.ObservedAtUtcTicks, TimeSpan.Zero),
                SampleDurationMilliseconds = value.HasValue ? value.DurationMilliseconds : null,
                IntegerValue = !value.HasValue ? null : value.Kind switch
                {
                    MetricScalarKind.Signed64 => MetricInteger.FromSigned64(unchecked((long)value.LowBits)),
                    MetricScalarKind.Unsigned64 => new MetricInteger(value.LowBits),
                    MetricScalarKind.Unsigned128 => new MetricInteger(value.LowBits, value.HighBits),
                    _ => null
                },
                BytesValue = value.HasValue && value.Kind == MetricScalarKind.Bytes ? value.Bytes.ToArray() : null,
                BooleanValue = value.Boolean
            });
            index++;
        }

        var facts = input.Aggregates;
        var cpu = new CpuMetrics(catalog.Cpu.Name, Number(items, "cpu.usage"), facts.CpuUsageAvailable,
            facts.CpuStatus, SourceGeneration(input, "cpu.usage"), Duration(input, "cpu.usage"),
            Int(items, "cpu.frequency"), catalog.Cpu.MaxFrequencyMhz, Number(items, "cpu.frequencyPercent"),
            catalog.Cpu.FrequencySource, CpuSensors(items, facts.CpuProvider));
        var memory = new MemoryMetrics(Unsigned(items, "memory.usage") ?? 0, Unsigned(items, "memory.total") ?? 0,
            Percent(items, "memory.usage", "memory.total"), facts.MemoryUsageAvailable)
        {
            AvailableBytes = Unsigned(items, "memory.availableBytes"), ObservationStatus = facts.MemoryStatus
        };
        var virtualMemory = new VirtualMemoryMetrics(Unsigned(items, "virtualMemory.usage") ?? 0,
            Unsigned(items, "virtualMemory.total") ?? 0, Percent(items, "virtualMemory.usage", "virtualMemory.total"),
            facts.VirtualMemorySelectable)
        {
            CommitPeakBytes = Unsigned(items, "virtualMemory.commitPeakBytes"), SystemCacheBytes = Unsigned(items, "memory.systemCacheBytes"),
            KernelPagedBytes = Unsigned(items, "memory.kernelPagedBytes"), KernelNonpagedBytes = Unsigned(items, "memory.kernelNonpagedBytes"),
            ProcessCount = UInt(items, "system.processCount"), ThreadCount = UInt(items, "system.threadCount"),
            HandleCount = UInt(items, "system.handleCount"), ObservationStatus = facts.VirtualMemoryStatus
        };
        var gpus = ImmutableArray.CreateBuilder<GpuMetrics>(catalog.Gpus.Length);
        index = 0;
        while (index < catalog.Gpus.Length)
        {
            if (input.GpuUsageAvailable[index] > 1) throw new InvalidDataException("Invalid GPU availability.");
            var gpu = catalog.Gpus[index]; var prefix = $"gpu.{gpu.Index}.";
            gpus.Add(new(gpu.Index, gpu.Name, Number(items, prefix + "usage"), Int(items, prefix + "graphicsClock"),
                0, Number(items, prefix + "graphicsClockPercent"), Int(items, prefix + "memoryClock"),
                Unsigned(items, prefix + "vram") ?? 0, Unsigned(items, prefix + "vramTotal") ?? 0,
                Percent(items, prefix + "vram", prefix + "vramTotal"), GpuSensors(items, prefix, input.GpuProviders[index]),
                gpu.UsageProvider, input.GpuUsageAvailable[index] == 1, gpu.IdentityKey)
            {
                AdapterKey = gpu.AdapterKey, HasDedicatedMemoryMetrics = gpu.HasDedicatedMemoryMetrics,
                AdditionalReadings = ImmutableDictionary<MetricFieldCode, double?>.Empty,
                AdditionalIntegerReadings = ImmutableDictionary<MetricFieldCode, ulong>.Empty,
                AdditionalObservedAt = ImmutableDictionary<MetricFieldCode, DateTimeOffset>.Empty
            });
            index++;
        }
        var datasets = ImmutableDictionary.CreateBuilder<string, HardwareMetricDatasetObservation>(StringComparer.OrdinalIgnoreCase);
        index = 0;
        var datasetColumns = input.Datasets;
        while (index < datasetColumns.Count)
        {
            if (Suppressed(input, datasetColumns.Ids[index])) { index++; continue; }
            var row = new HardwareMetricDatasetObservation(datasetColumns.Ids[index], datasetColumns.Statuses[index],
                datasetColumns.SourceGenerations[index], MetricTimestamp.UtcTicks(datasetColumns.ObservedAtUtcTicks[index], datasetColumns.TimestampUnit),
                datasetColumns.WorkspaceIdentities[index], datasetColumns.ConfigurationGenerations[index],
                datasetColumns.CatalogGenerations[index], datasetColumns.CommittedGenerations[index])
            {
                LastAttemptAtUtcTicks = MetricTimestamp.UtcTicks(datasetColumns.AttemptAtUtcTicks[index], datasetColumns.TimestampUnit),
                LastSuccessAtUtcTicks = MetricTimestamp.UtcTicks(datasetColumns.SuccessAtUtcTicks[index], datasetColumns.TimestampUnit),
                ReadyUntilUtcTicks = datasetColumns.ReadyUntilUtcTicks[index] == 0 ? 0
                    : MetricTimestamp.UtcTicks(datasetColumns.ReadyUntilUtcTicks[index], datasetColumns.TimestampUnit),
                FailureCode = datasetColumns.FailureCodes[index]
            };
            datasets.Add(row.DatasetId, row); index++;
        }
        AddEngines(version, input, items, bindings, datasets);
        var history = new Dictionary<string, ImmutableArray<HardwareMetricHistorySample>.Builder>(StringComparer.OrdinalIgnoreCase);
        index = 0;
        var historyColumns = input.History;
        while (index < historyColumns.Count)
        {
            var metricIndex = historyColumns.MetricIndices[index];
            if ((uint)metricIndex >= (uint)catalog.Count || catalog.Identities[metricIndex] != historyColumns.MetricIdentities[index]
                || historyColumns.Present[index] > 1)
                throw new InvalidDataException("Metric history identity has not arrived or is malformed.");
            var id = catalog.Items[metricIndex].Id;
            if (Suppressed(input, id)) { index++; continue; }
            if (!history.TryGetValue(id, out var rows))
            {
                rows = ImmutableArray.CreateBuilder<HardwareMetricHistorySample>();
                history.Add(id, rows);
            }
            rows.Add(new(new DateTimeOffset(MetricTimestamp.UtcTicks(historyColumns.ObservedAtUtcTicks[index], historyColumns.TimestampUnit), TimeSpan.Zero),
                historyColumns.Present[index] == 1 ? historyColumns.Values[index] : null));
            index++;
        }
        var adapterColumns = input.Adapters;
        var adapters = ImmutableArray.CreateBuilder<SchedulingGpuAdapterObservation>(adapterColumns.Count);
        index = 0;
        while (index < adapterColumns.Count)
        {
            adapters.Add(new(adapterColumns.Indices[index], adapterColumns.AdapterKeys[index], adapterColumns.Capabilities[index],
                adapterColumns.ValidMetrics[index], adapterColumns.UsageStatuses[index], adapterColumns.CapacityStatuses[index],
                adapterColumns.UsagePercent[index], adapterColumns.UsedBytes[index], adapterColumns.TotalBytes[index],
                adapterColumns.Generations[index], MetricTimestamp.UtcTicks(adapterColumns.ObservedAtUtcTicks[index], adapterColumns.TimestampUnit)));
            index++;
        }
        var inventory = input.Inventory;
        return new(input.CapturedAt, cpu, memory, virtualMemory, gpus.MoveToImmutable(),
            new(inventory.Status, inventory.Generation, MetricTimestamp.UtcTicks(inventory.ObservedAtUtcTicks, inventory.TimestampUnit), inventory.ObservedCount,
                inventory.SkippedCount, inventory.OverflowCount, inventory.TopologyFingerprint, adapters.MoveToImmutable()), items.ToImmutable())
        {
            FieldBindings = bindings.ToImmutable(), Datasets = datasets.ToImmutable(),
            History = history.ToImmutableDictionary(static pair => pair.Key, static pair => pair.Value.ToImmutable(), StringComparer.OrdinalIgnoreCase),
            WorkspaceIdentity = version.RegionIncarnation, ConfigurationGeneration = version.ConfigurationRevision,
            CatalogGeneration = version.CatalogRevision, CommittedGeneration = version.CommittedGeneration
        };
    }

    private static bool Suppressed(MetricSnapshotInput input, string id)
    {
        var i = 0;
        while (i < input.ProjectionSuppressedIds.Length)
            if (string.Equals(input.ProjectionSuppressedIds[i++], id, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static void AddEngines(MetricObjectVersion version, MetricSnapshotInput input,
        ImmutableDictionary<string, MetricReadingValue>.Builder items,
        ImmutableDictionary<string, MetricDeviceFieldBinding>.Builder bindings,
        ImmutableDictionary<string, HardwareMetricDatasetObservation>.Builder datasets)
    {
        var engines = input.Engines; var index = 0;
        while (index < engines.Count)
        {
            var baseIndex = input.EngineBaseMetricIndices[index];
            if ((uint)baseIndex >= (uint)input.Catalog.Count
                || input.Catalog.Bindings[baseIndex] is not { } format
                || format.Field.Code != MetricFieldCode.GpuEngineBusy)
                throw new InvalidDataException("Native engine projection format binding is missing.");
            if (engines.TryRead(index, out var row, out var detail) != MetricViewReadStatus.Available)
                throw new MetricDataAreaReadException(detail);
            var address = Encoding.UTF8.GetString(row.AddressUtf8);
            var device = Encoding.UTF8.GetString(row.DeviceKeyUtf8);
            var value = row.Value;
            var binding = format with
            {
                DeviceIdentity = device, RequestAddress = address, AdapterKey = row.AdapterLuid,
                SampleRequestAddress = format.RequestAddress,
                RequestAliases = ImmutableArray<string>.Empty, SelectedRequestAliases = ImmutableArray<string>.Empty,
                PdhGpuEngineIdentity = new(row.AdapterLuid, row.PhysicalIndex, row.EngineIndex, (NativePdhEngineClass)row.EngineClass),
                Device = new("gpu_engine", MetricDeviceAddress.GpuEngine(format.Device.Address
                    ?? throw new InvalidDataException("Engine format requires the GPU locator."),
                    row.AdapterLuid, row.PhysicalIndex, row.EngineIndex, row.EngineClass), null, index)
            };
            bindings.Add(address, binding);
            items.Add(address, new(address, value.Float64, format.Field.Unit, value.Float64)
            {
                FieldCode = format.Field.Code, FieldName = format.Field.Name, DeviceIdentity = device,
                SourceId = format.SourceId, ValueKind = "float64", Status = value.Status,
                ObservedAt = value.ObservedAtUtcTicks == 0 ? null : new DateTimeOffset(value.ObservedAtUtcTicks, TimeSpan.Zero)
            });
            datasets.Add(address, new(address, value.Status, value.SourceGeneration, value.ObservedAtUtcTicks,
                version.RegionIncarnation, version.ConfigurationRevision, version.CatalogRevision, version.CommittedGeneration)
            {
                LastAttemptAtUtcTicks = value.LastAttemptAtUtcTicks, LastSuccessAtUtcTicks = value.LastSuccessAtUtcTicks
            });
            index++;
        }
    }

    private static string KindName(MetricScalarKind kind) => kind switch
    {
        MetricScalarKind.Float64 => "float64", MetricScalarKind.Signed64 => "signed64",
        MetricScalarKind.Unsigned64 => "unsigned64", MetricScalarKind.Unsigned128 => "unsigned128",
        MetricScalarKind.Bytes => "bytes", MetricScalarKind.Boolean => "boolean",
        _ => throw new InvalidDataException("Unknown metric scalar kind.")
    };

    private static double? Optional(IReadOnlyDictionary<string, MetricReadingValue> items, string id)
    {
        if (!items.TryGetValue(id, out var item)) return null;
        if (item.NumericValue is { } value) return value;
        // Aggregate display doubles may be rounded; exact integer items remain untouched.
        if (item.IntegerValue is { } integer)
            return integer.Negative ? unchecked((long)integer.LowBits) : (double)(((UInt128)integer.HighBits << 64) | integer.LowBits);
        return null;
    }
    private static double Number(IReadOnlyDictionary<string, MetricReadingValue> items, string id) => Optional(items, id) ?? 0;
    private static int Int(IReadOnlyDictionary<string, MetricReadingValue> items, string id)
        => checked((int)Math.Round(Number(items, id)));
    private static ulong? Unsigned(IReadOnlyDictionary<string, MetricReadingValue> items, string id)
    {
        if (!items.TryGetValue(id, out var item)) return null;
        if (item.IntegerValue is { Negative: false, HighBits: 0 } integer) return integer.LowBits;
        if (item.NumericValue is { } value && double.IsFinite(value) && value >= 0 && value < 18446744073709551616d)
            return checked((ulong)Math.Round(value));
        return null;
    }
    private static uint? UInt(IReadOnlyDictionary<string, MetricReadingValue> items, string id)
        => Unsigned(items, id) is <= uint.MaxValue and var value ? (uint)value : null;
    private static double Percent(IReadOnlyDictionary<string, MetricReadingValue> items, string used, string total)
        => items.TryGetValue(used, out var item) && item.Percent is { } percent ? percent
            : Optional(items, used) is { } u && Optional(items, total) is > 0 and var t ? u * 100 / t : 0;

    private static ulong SourceGeneration(MetricSnapshotInput input, string id)
    {
        var current = input.Values;
        var i = 0;
        while (i < input.Catalog.Count)
        {
            if (input.Catalog.Items[i].Id.Equals(id, StringComparison.OrdinalIgnoreCase)
                && current.TryRead(i, out var value) == MetricViewReadStatus.Available) return value.SourceGeneration;
            i++;
        }
        return 0;
    }
    private static long Duration(MetricSnapshotInput input, string id)
    {
        var current = input.Values;
        var i = 0;
        while (i < input.Catalog.Count)
        {
            if (input.Catalog.Items[i].Id.Equals(id, StringComparison.OrdinalIgnoreCase)
                && current.TryRead(i, out var value) == MetricViewReadStatus.Available)
                return checked((long)value.DurationMilliseconds);
            i++;
        }
        return 0;
    }

    private static CpuSensorMetrics CpuSensors(IReadOnlyDictionary<string, MetricReadingValue> items, HardwareSensorProviderState provider)
        => new(provider, Optional(items, "cpu.packagePower"), Optional(items, "cpu.coreVoltage"),
            Optional(items, "cpu.packageCurrent"), Optional(items, "cpu.temperature"), Optional(items, "cpu.stapmPower"),
            Optional(items, "cpu.actualPower"), Optional(items, "cpu.averagePower"), Optional(items, "cpu.tdcCurrent"),
            Optional(items, "cpu.edcCurrent"), Optional(items, "cpu.platformPower"), Optional(items, "cpu.platformVoltage"),
            Optional(items, "cpu.igpuFrequency"), Optional(items, "cpu.igpuVoltage"), Optional(items, "cpu.igpuTemperature"),
            Optional(items, "cpu.smuFrequency"));

    private static GpuSensorMetrics GpuSensors(IReadOnlyDictionary<string, MetricReadingValue> items, string prefix, HardwareSensorProviderState provider)
        => new(provider, Optional(items, prefix + "power"), Optional(items, prefix + "powerLimit"),
            Optional(items, prefix + "boardPower"), Optional(items, prefix + "temperature"), Optional(items, prefix + "hotspotTemperature"),
            Optional(items, prefix + "intakeTemperature"), Optional(items, prefix + "fanPercent"), Optional(items, prefix + "fanRpm"),
            Optional(items, prefix + "coreVoltage"), Optional(items, prefix + "current"));
}
