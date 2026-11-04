using System.Collections.Immutable;
using System.Runtime.InteropServices;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.ReadWrite.Native;
using ResourceManager.ReadWrite.Windows.Metrics.Capture;

namespace ResourceManager.App.Infrastructure.Monitoring;

public static class NativeMetricSnapshotCommittedProjection
{
    public static HardwareMetricSnapshot Create(
        NativeMetricSnapshotCommittedFrame frame,
        ulong workspaceIdentity)
        => Create(frame: frame, workspaceIdentity: workspaceIdentity, sourceSelection: null);

    /// <param name="previous">The caller's previous projection of this
    /// workspace. Unchanged items and bindings reuse its immutable objects;
    /// every changed value is new. The frame may be workspace scratch: no
    /// published or retained value references its rows.</param>
    internal static HardwareMetricSnapshot Create(
        NativeMetricSnapshotCommittedFrame frame, ulong workspaceIdentity,
        MetricReadingSelection? sourceSelection, Reuse? previous = null)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (workspaceIdentity == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(workspaceIdentity));
        }
        previous?.Begin(frame.Catalog);
        var selectedLegacy = sourceSelection?.LegacyHandles ?? frame.Catalog.LegacyMetricHandles;
        var requestAliases = previous is not null && ReferenceEquals(previous.LegacyHandles, selectedLegacy)
            && previous.RequestAliases is { } knownAliases
                ? knownAliases
                : selectedLegacy.GroupBy(static pair => pair.Value)
                    .ToDictionary(static group => group.Key, static group => group.Select(static pair => pair.Key).ToArray());
        var rows = ImmutableCollectionsMarshal.AsArray(frame.Metrics) ?? [];
        var readings = previous?.Readings
            ?? new Dictionary<string, Reading>(rows.Length + selectedLegacy.Count, StringComparer.OrdinalIgnoreCase);
        try
        {
            readings.EnsureCapacity(rows.Length + selectedLegacy.Count);
            for (var index = 0; index < rows.Length; index++)
            {
                ref readonly var metric = ref rows[index];
                readings.Add(frame.Catalog.MetricIds[metric.MetricHandle],
                    new Reading(rows, index, Decode(metric.ValueKind, metric.ValueBits)));
            }
            foreach (var (address, handle) in selectedLegacy)
            {
                if (readings.TryGetValue(frame.Catalog.MetricIds[handle], out var reading))
                    readings[address] = reading;
            }
            var snapshot = Project(frame, workspaceIdentity, sourceSelection, readings, requestAliases, previous);
            previous?.Commit(sourceSelection, selectedLegacy, requestAliases, snapshot);
            return snapshot;
        }
        finally
        {
            // A reused reading table must not keep the workspace scratch rows.
            if (previous is not null) readings.Clear();
        }
    }

    private static HardwareMetricSnapshot Project(
        NativeMetricSnapshotCommittedFrame frame, ulong workspaceIdentity,
        MetricReadingSelection? sourceSelection, Dictionary<string, Reading> readings,
        Dictionary<ulong, string[]> requestAliases, Reuse? previous)
    {
        var cpuUsage = Get(readings, "cpu.usage");
        var cpuFrequency = Get(readings, "cpu.frequency");
        var cpuFrequencyPercent =
            Get(readings, "cpu.frequencyPercent");
        var cpu = new CpuMetrics(
             frame.Catalog.CpuModelName ?? string.Empty,
             cpuUsage.Value,
             cpuUsage.IsAvailable,
             CpuStatus(frame, cpuUsage.Status),
            cpuUsage.Output.SourceGeneration,
            checked((long)cpuUsage.Output.SampleDurationMilliseconds),
            ToInt(cpuFrequency.Value),
            0,
            cpuFrequencyPercent.Value,
            SourceId(frame, cpuFrequency.Output.SourceHandle),
            CreateCpuSensors(frame, readings));
        var memoryUsed = Get(readings, "memory.usage");
        var memoryTotal = Get(readings, "memory.total");
        var memory = new MemoryMetrics(UsedBytes: ToUInt64(memoryUsed), TotalBytes: ToUInt64(memoryTotal), UsagePercent: RatioPercent(memoryUsed, memoryTotal), IsUsageAvailable: memoryUsed.IsAvailable
                && memoryTotal.IsAvailable)
        {
            AvailableBytes = UInt64OrNull(readings, "memory.availableBytes"),
            ObservationStatus = ProjectCompositeObservationStatus(
                memoryUsed.Status,
                memoryTotal.Status)
        };
        var virtualUsed = Get(readings, "virtualMemory.usage");
        var virtualTotal = Get(readings, "virtualMemory.total");
        var virtualMemory = new VirtualMemoryMetrics(UsedBytes: ToUInt64(virtualUsed), TotalBytes: ToUInt64(virtualTotal), UsagePercent: RatioPercent(virtualUsed, virtualTotal), IsSelectable: virtualUsed.IsAvailable && virtualTotal.IsAvailable)
        {
            CommitPeakBytes = UInt64OrNull(readings, "virtualMemory.commitPeakBytes"),
            SystemCacheBytes = UInt64OrNull(readings, "memory.systemCacheBytes"),
            KernelPagedBytes = UInt64OrNull(readings, "memory.kernelPagedBytes"),
            KernelNonpagedBytes = UInt64OrNull(readings, "memory.kernelNonpagedBytes"),
            ProcessCount = UInt32OrNull(readings, "system.processCount"),
            ThreadCount = UInt32OrNull(readings, "system.threadCount"),
            HandleCount = UInt32OrNull(readings, "system.handleCount"),
            ObservationStatus = ProjectCompositeObservationStatus(
                virtualUsed.Status,
                virtualTotal.Status)
        };
        var gpus = CreateGpus(frame, readings);
        var inventory = CreateGpuInventory(frame, readings);
        var items = CreateItems(frame, readings, previous?.Items);
        var datasets = CreateDatasetObservations(
            frame,
            readings,
            inventory,
            workspaceIdentity);
        return new HardwareMetricSnapshot(
            DateTimeOffset.FromUnixTimeMilliseconds(
                checked((long)frame.Header.CapturedAtMilliseconds)),
            cpu,
            memory,
            virtualMemory,
            gpus,
            inventory,
            items)
        {
            FieldBindings = CreateFieldBindings(frame, sourceSelection, requestAliases, previous),
            Datasets = datasets,
            WorkspaceIdentity = workspaceIdentity,
            ConfigurationGeneration =
                frame.Header.ConfigurationGeneration,
            CatalogGeneration = frame.Header.CatalogGeneration,
            CommittedGeneration = frame.Header.CommittedGeneration
        };
    }

    private static MetricFieldDefinition WithReading(MetricFieldDefinition field, MetricReadingSource? reading)
        => field.Reading == reading ? field : field with { Reading = reading };

    private static IReadOnlyDictionary<string, MetricDeviceFieldBinding> CreateFieldBindings(
        NativeMetricSnapshotCommittedFrame frame,
        MetricReadingSelection? sourceSelection,
        Dictionary<ulong, string[]> requestAliases,
        Reuse? previous)
    {
        // Every binding is a function of the catalog, the selection and the
        // request aliases. The same three inputs give the same published table.
        // Only a table built from a selection is comparable to one being built.
        var known = sourceSelection is not null && previous?.Selection is not null
            ? previous.FieldBindings : null;
        if (known is not null && ReferenceEquals(previous!.Selection, sourceSelection)
            && ReferenceEquals(previous.RequestAliases, requestAliases))
            return known;
        var result = new Dictionary<string, MetricDeviceFieldBinding>(
            frame.Catalog.MetricBindings.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var binding in frame.Catalog.MetricBindings.Values)
        {
            if (binding.Field is null) continue;
            var reading = sourceSelection?.Fields.GetValueOrDefault(binding.MetricHandle);
            var aliases = requestAliases.GetValueOrDefault(binding.MetricHandle) ?? [];
            if (known is not null && known.TryGetValue(binding.MetricId, out var prior)
                && Equals(prior.Field.Reading, reading)
                && SameAliases(prior.SelectedRequestAliases, aliases))
            {
                result.Add(binding.MetricId, prior);
                continue;
            }
            result.Add(binding.MetricId, new MetricDeviceFieldBinding(binding.DeviceIdentity,
                WithReading(binding.Field, reading),
                binding.MetricId, binding.SourceId)
            {
                NpuEngine = binding.NpuEngine,
                Device = binding.Device,
                SelectedRequestAliases = sourceSelection is null ? [] : aliases,
                RequestAliases = aliases
                    .Append(MetricReadingSourceTracker.LegacyAddress(binding))
                    .Where(static alias => alias.Length != 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                AdapterKey = frame.Catalog.GpuScopeBindings.TryGetValue(binding.ScopeHandle, out var gpuBinding)
                    ? gpuBinding.AdapterLuid : null
            });
        }
        return result;
    }

    private static bool SameAliases(IReadOnlyList<string> known, string[] aliases)
    {
        if (known.Count != aliases.Length) return false;
        for (var index = 0; index < aliases.Length; index++)
        {
            if (!string.Equals(known[index], aliases[index], StringComparison.Ordinal)) return false;
        }
        return true;
    }

    /// <summary>One owner's previous committed projection of one workspace
    /// catalog: immutable published objects only, plus a reading table that is
    /// empty between projections. Not thread-safe; the capture uses it only
    /// under its SampleGate.</summary>
    internal sealed class Reuse
    {
        private NativeMetricSnapshotCatalogProjection? catalog;

        internal MetricReadingSelection? Selection { get; private set; }
        internal IReadOnlyDictionary<string, ulong>? LegacyHandles { get; private set; }
        internal Dictionary<ulong, string[]>? RequestAliases { get; private set; }
        internal IReadOnlyDictionary<string, MetricDeviceFieldBinding>? FieldBindings { get; private set; }
        internal IReadOnlyDictionary<string, MetricReadingValue>? Items { get; private set; }
        internal Dictionary<string, Reading> Readings { get; } = new(StringComparer.OrdinalIgnoreCase);

        internal void Begin(NativeMetricSnapshotCatalogProjection current)
        {
            if (ReferenceEquals(catalog, current)) return;
            catalog = current;
            Selection = null;
            LegacyHandles = null;
            RequestAliases = null;
            FieldBindings = null;
            Items = null;
            Readings.Clear();
            Readings.TrimExcess();
        }

        internal void Commit(MetricReadingSelection? selection, IReadOnlyDictionary<string, ulong> legacyHandles,
            Dictionary<ulong, string[]> requestAliases, HardwareMetricSnapshot snapshot)
        {
            Selection = selection;
            LegacyHandles = legacyHandles;
            RequestAliases = requestAliases;
            FieldBindings = snapshot.FieldBindings;
            Items = snapshot.Items;
        }
    }

    private static string NormalizeDatasetId(string metricId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(metricId);
        return metricId.Trim();
    }

    private static IReadOnlyDictionary<string, HardwareMetricDatasetObservation>
        CreateDatasetObservations(
            NativeMetricSnapshotCommittedFrame frame,
            IReadOnlyDictionary<string, Reading> readings,
            SchedulingGpuInventorySnapshot inventory,
            ulong workspaceIdentity)
    {
        var result = new Dictionary<string, HardwareMetricDatasetObservation>(
            readings.Count + 1, StringComparer.OrdinalIgnoreCase);
        var attemptedAtUtcTicks = DateTimeOffset.FromUnixTimeMilliseconds(
                checked((long)frame.Header.CapturedAtMilliseconds))
            .UtcTicks;
        foreach (var (metricId, reading) in readings)
        {
            var datasetId = NormalizeDatasetId(metricId);
            var status = Status(reading.Status);
            var observedAtUtcTicks = reading.Output.ObservedAtMilliseconds == 0
                ? 0
                : DateTimeOffset.FromUnixTimeMilliseconds(
                        checked((long)reading.Output.ObservedAtMilliseconds))
                    .UtcTicks;
            result[datasetId] = new HardwareMetricDatasetObservation(
                datasetId,
                status,
                reading.Output.SourceGeneration,
                observedAtUtcTicks,
                workspaceIdentity,
                frame.Header.ConfigurationGeneration,
                frame.Header.CatalogGeneration,
                frame.Header.CommittedGeneration)
            {
                LastAttemptAtUtcTicks = attemptedAtUtcTicks,
                LastSuccessAtUtcTicks = status == SamplingObservationStatus.Current
                    ? observedAtUtcTicks
                    : 0,
                FailureCode = DatasetFailureCode(frame, reading)
            };
        }

        ReplaceCompositeDatasetObservation(
            result,
            readings,
            "memory.usage",
            "memory.total",
            workspaceIdentity,
            frame,
            attemptedAtUtcTicks);
        ReplaceCompositeDatasetObservation(
            result,
            readings,
            "virtualMemory.usage",
            "virtualMemory.total",
            workspaceIdentity,
            frame,
            attemptedAtUtcTicks);
        foreach (var scope in frame.Catalog.GpuScopeBindings.Values)
        {
            string? valueMetricId = null;
            string? capacityMetricId = null;
            foreach (var binding in frame.Catalog.MetricBindings.Values)
            {
                if (binding.ScopeHandle != scope.ScopeHandle) continue;
                if (binding.FieldCode == MetricFieldCode.GpuMemoryUsed)
                    valueMetricId = binding.LegacyMetricId;
                else if (binding.FieldCode == MetricFieldCode.GpuMemoryTotal)
                    capacityMetricId = binding.LegacyMetricId;
            }
            if (string.IsNullOrEmpty(valueMetricId)) continue;
            ReplaceCompositeDatasetObservation(
                result,
                readings,
                valueMetricId,
                capacityMetricId ?? string.Empty,
                workspaceIdentity,
                frame,
                attemptedAtUtcTicks);
        }

        result["system.gpu.inventory"] =
            new HardwareMetricDatasetObservation(
                "system.gpu.inventory",
                inventory.Status,
                inventory.Generation,
                inventory.ObservedAtUtcTicks,
                workspaceIdentity,
                frame.Header.ConfigurationGeneration,
                frame.Header.CatalogGeneration,
                frame.Header.CommittedGeneration)
            {
                LastAttemptAtUtcTicks = attemptedAtUtcTicks,
                LastSuccessAtUtcTicks = inventory.ObservedAtUtcTicks,
                FailureCode = DatasetFailureCode(inventory.Status)
            };
        return result;
    }

    private static void ReplaceCompositeDatasetObservation(
        IDictionary<string, HardwareMetricDatasetObservation> result,
        IReadOnlyDictionary<string, Reading> readings,
        string valueMetricId,
        string capacityMetricId,
        ulong workspaceIdentity,
        NativeMetricSnapshotCommittedFrame frame,
        long attemptedAtUtcTicks)
    {
        if (!readings.TryGetValue(valueMetricId, out var value))
        {
            return;
        }

        var capacity = Get(readings, capacityMetricId);
        var status = ProjectCompositeObservationStatus(
            value.Status,
            capacity.Status);
        var observedAtUtcTicks = value.Output.ObservedAtMilliseconds == 0
            ? 0
            : DateTimeOffset.FromUnixTimeMilliseconds(
                    checked((long)value.Output.ObservedAtMilliseconds))
                .UtcTicks;
        result[valueMetricId] = new HardwareMetricDatasetObservation(
            valueMetricId,
            status,
            value.Output.SourceGeneration,
            observedAtUtcTicks,
            workspaceIdentity,
            frame.Header.ConfigurationGeneration,
            frame.Header.CatalogGeneration,
            frame.Header.CommittedGeneration)
        {
            LastAttemptAtUtcTicks = attemptedAtUtcTicks,
            LastSuccessAtUtcTicks = status == SamplingObservationStatus.Current
                ? observedAtUtcTicks
                : 0,
            FailureCode = DatasetFailureCode(status)
        };
    }

    private static string? DatasetFailureCode(SamplingObservationStatus status)
        => status switch
        {
            SamplingObservationStatus.Current => null,
            SamplingObservationStatus.Unavailable =>
                "hardware-native-unavailable",
            SamplingObservationStatus.NotRequested =>
                "hardware-native-not-requested",
            SamplingObservationStatus.Unsupported =>
                "hardware-native-unsupported",
            SamplingObservationStatus.Partial =>
                "hardware-native-partial",
            SamplingObservationStatus.Warming =>
                "hardware-native-warming",
            _ => "hardware-native-invalid"
        };

    private static string? DatasetFailureCode(NativeMetricSnapshotCommittedFrame frame, Reading reading)
    {
        if (reading.Status == NativeMetricSnapshotMetricStatus.Current) return null;
        var source = default(NativeMetricSnapshotSourceOutput);
        foreach (var row in frame.Sources)
        {
            if (row.SourceHandle != reading.Output.SourceHandle) continue;
            source = row;
            break;
        }
        if (source.Status == (uint)NativeMetricSnapshotSourceStatus.Unavailable)
            return "hardware-source-unavailable";
        if (reading.Status == NativeMetricSnapshotMetricStatus.Skipped
            && source.Status == (uint)NativeMetricSnapshotSourceStatus.Complete
            && frame.Catalog.MetricBindings.TryGetValue(reading.Output.MetricHandle, out var binding)
            && (binding.MetricKind == (uint)NativeMetricSnapshotMetricKind.CpuUsage
                || (binding.SourceId == NativeMetricSnapshotSources.PdhGpuEngine
                    && binding.FieldCode == MetricFieldCode.GpuUsage)))
            return "hardware-counter-baseline";
        return DatasetFailureCode(Status(reading.Status));
    }

    private static IReadOnlyList<GpuMetrics> CreateGpus(
        NativeMetricSnapshotCommittedFrame frame,
        IReadOnlyDictionary<string, Reading> readings)
    {
        var result = new List<GpuMetrics>(
            frame.Catalog.GpuScopeBindings.Count);
        foreach (var binding in frame.Catalog.GpuScopeBindings.Values
                     .OrderBy(static value => value.DisplayIndex))
        {
            var prefix = $"gpu.{binding.DisplayIndex}.";
            var usage = Get(readings, prefix + "usage");
            var graphicsClock = Get(
                readings,
                prefix + "graphicsClock");
            var graphicsClockPercent = Get(
                readings,
                prefix + "graphicsClockPercent");
            var memoryClock = Get(
                readings,
                prefix + "memoryClock");
            var usedMemory = Get(readings, prefix + "vram");
            var totalMemory = Get(
                readings,
                prefix + "vramTotal");
            result.Add(new GpuMetrics(
                binding.DisplayIndex,
                binding.DisplayName,
                usage.Value,
                ToInt(graphicsClock.Value),
                0,
                graphicsClockPercent.Value,
                ToInt(memoryClock.Value),
                ToUInt64(usedMemory),
                ToUInt64(totalMemory),
                RatioPercent(usedMemory, totalMemory),
                new GpuSensorMetrics(
                    ProviderState(frame, binding.ScopeHandle, readings),
                    ValueOrNull(readings, prefix + "power"),
                    ValueOrNull(readings, prefix + "powerLimit"),
                    ValueOrNull(readings, prefix + "boardPower"),
                    ValueOrNull(readings, prefix + "temperature"),
                    ValueOrNull(
                        readings,
                        prefix + "hotspotTemperature"),
                    ValueOrNull(
                        readings,
                        prefix + "intakeTemperature"),
                    ValueOrNull(readings, prefix + "fanPercent"),
                    ValueOrNull(readings, prefix + "fanRpm"),
                    ValueOrNull(
                        readings,
                        prefix + "coreVoltage"),
                    ValueOrNull(readings, prefix + "current")),
                SourceId(frame, usage.Output.SourceHandle),
                usage.IsAvailable,
                binding.ExactIdentityKey) { AdapterKey = binding.AdapterLuid,
                    HasDedicatedMemoryMetrics = binding.SupportsDedicatedMetrics });
        }
        return result;
    }

    private static SchedulingGpuInventorySnapshot CreateGpuInventory(
        NativeMetricSnapshotCommittedFrame frame,
        IReadOnlyDictionary<string, Reading> readings)
    {
        var source = frame.Sources.SingleOrDefault(source =>
            frame.Catalog.SourceIds.TryGetValue(
                source.SourceHandle,
                out var sourceId)
            && string.Equals(
                sourceId,
                NativeMetricSnapshotSources.WindowsGpuAdapterOrder,
                StringComparison.Ordinal));
        if (source.SourceHandle == 0)
        {
            return new SchedulingGpuInventorySnapshot(
                SamplingObservationStatus.Unavailable,
                0,
                0,
                0,
                0,
                0,
                0,
                []);
        }
        var adapters = new List<SchedulingGpuAdapterObservation>();
        foreach (var row in frame.GpuInventory)
        {
            if (!frame.Catalog.GpuScopeBindings.TryGetValue(
                    row.AdapterHandle,
                    out var binding))
            {
                throw new InvalidOperationException(
                    "The native GPU inventory references an unknown catalog scope.");
            }
            var prefix = $"gpu.{binding.DisplayIndex}.";
            var usage = Get(readings, prefix + "usage");
            var used = Get(readings, prefix + "vram");
            var total = Get(readings, prefix + "vramTotal");
            var capability = SchedulingGpuCapabilityMask.Usage;
            var validity = SchedulingGpuMetricMask.None;
            if (usage.IsAvailable)
            {
                validity |= SchedulingGpuMetricMask.Usage;
            }
            var capacityStatus = SamplingObservationStatus.Unsupported;
            if (total.IsAvailable)
            {
                capability |=
                    SchedulingGpuCapabilityMask.DedicatedMemory;
                if (used.IsAvailable)
                {
                    validity |=
                        SchedulingGpuMetricMask.UsedDedicatedMemory
                        | SchedulingGpuMetricMask.TotalDedicatedMemory;
                    capacityStatus = Status(total.Status);
                }
                else
                {
                    capacityStatus =
                        SamplingObservationStatus.Unavailable;
                }
            }
            adapters.Add(new SchedulingGpuAdapterObservation(
                binding.DisplayIndex,
                row.AdapterLuidLow,
                capability,
                validity,
                Status(usage.Status),
                capacityStatus,
                usage.Value,
                ToUInt64(used),
                ToUInt64(total),
                row.SourceGeneration,
                DateTimeOffset.FromUnixTimeMilliseconds(
                        checked((long)row.ObservedAtMilliseconds))
                    .UtcTicks));
        }
        var observedAt = source.LastCurrentAtMilliseconds == 0
            ? 0
            : DateTimeOffset.FromUnixTimeMilliseconds(
                    checked((long)source.LastCurrentAtMilliseconds))
                .UtcTicks;
        return new SchedulingGpuInventorySnapshot(
            ProjectSourceStatus((NativeMetricSnapshotSourceStatus)source.Status),
            source.SourceGeneration,
            observedAt,
            source.ObservedCount,
            source.SkippedCount,
            source.OverflowCount,
            frame.GpuInventory.FirstOrDefault().TopologyFingerprint,
            adapters);
    }

    private static IReadOnlyDictionary<string, MetricReadingValue> CreateItems(
        NativeMetricSnapshotCommittedFrame frame,
        IReadOnlyDictionary<string, Reading> readings,
        IReadOnlyDictionary<string, MetricReadingValue>? previous)
    {
        var items = new Dictionary<string, MetricReadingValue>(
            readings.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var (id, reading) in readings)
        {
            var binding = frame.Catalog.MetricBindings.GetValueOrDefault(reading.Output.MetricHandle);
            if (id != binding.MetricId && binding.FieldCode is MetricFieldCode.MemoryTotal
                or MetricFieldCode.CommitLimit or MetricFieldCode.GpuMemoryTotal) continue;
            var field = binding.Field;
            ref readonly var output = ref reading.Output;
            var valueAvailable = reading.IsAvailable;
            var capacityId = binding.FieldCode switch
            {
                MetricFieldCode.MemoryUsed => "memory.total",
                MetricFieldCode.CommitUsed => "virtualMemory.total",
                MetricFieldCode.GpuMemoryUsed => $"gpu.{binding.DisplayIndex}.vramTotal",
                _ => string.Empty
            };
            double? numericValue = valueAvailable && output.ValueKind == (uint)NativeMetricSnapshotValueKind.Float64
                ? reading.Value : null;
            var unit = field?.Unit ?? string.Empty;
            var total = valueAvailable ? ValueOrNull(readings, capacityId) : null;
            var sourceId = SourceId(frame, output.SourceHandle);
            ulong? sampleDuration = valueAvailable && output.SampleDurationMilliseconds > 0
                ? output.SampleDurationMilliseconds : null;
            MetricInteger? integerValue = valueAvailable ? (NativeMetricSnapshotValueKind)output.ValueKind switch
            {
                NativeMetricSnapshotValueKind.Signed64 => MetricInteger.FromSigned64(unchecked((long)output.ValueBits)),
                NativeMetricSnapshotValueKind.Unsigned64 => new MetricInteger(output.ValueBits),
                NativeMetricSnapshotValueKind.Unsigned128 => new MetricInteger(output.ValueBits, output.ValueHighBits),
                _ => null
            } : null;
            bool? booleanValue = valueAvailable && output.ValueKind == (uint)NativeMetricSnapshotValueKind.Boolean
                ? output.ValueBits == 1 : null;
            var status = Status(reading.Status);
            DateTimeOffset? observedAt = output.ObservedAtMilliseconds == 0 ? null
                : DateTimeOffset.FromUnixTimeMilliseconds(checked((long)output.ObservedAtMilliseconds));
            var hasBytes = valueAvailable && output.ValueKind == (uint)NativeMetricSnapshotValueKind.Bytes;
            // An unchanged reading keeps the previous projection's immutable item.
            if (!hasBytes && previous is not null && previous.TryGetValue(id, out var known)
                && string.Equals(known.Id, id, StringComparison.Ordinal)
                && SameValue(known.NumericValue, numericValue)
                && string.Equals(known.Unit, unit, StringComparison.Ordinal)
                && known.Percent is null
                && SameValue(known.Total, total)
                && known.FieldCode == binding.FieldCode
                && string.Equals(known.FieldName, field?.Name, StringComparison.Ordinal)
                && string.Equals(known.DeviceIdentity, binding.DeviceIdentity, StringComparison.Ordinal)
                && string.Equals(known.SourceId, sourceId, StringComparison.Ordinal)
                && string.Equals(known.ValueKind, field?.ValueKind, StringComparison.Ordinal)
                && known.SampleDurationMilliseconds == sampleDuration
                && known.IntegerValue == integerValue
                && known.BooleanValue == booleanValue
                && known.Status == status
                && SameInstant(known.ObservedAt, observedAt)
                && known.BytesValue is null)
            {
                items[id] = known;
                continue;
            }
            items[id] = new MetricReadingValue(Id: id,
                NumericValue: numericValue,
                Unit: unit,
                Total: total)
            {
                FieldCode = binding.FieldCode,
                FieldName = field?.Name,
                DeviceIdentity = binding.DeviceIdentity,
                SourceId = sourceId,
                ValueKind = field?.ValueKind,
                SampleDurationMilliseconds = sampleDuration,
                IntegerValue = integerValue,
                BytesValue = hasBytes ? DecodeBytes(reading) : null,
                BooleanValue = booleanValue,
                Status = status,
                ObservedAt = observedAt
            };
        }
        return items;
    }

    private static bool SameValue(double? left, double? right)
        => left.HasValue == right.HasValue
            && (!left.HasValue || BitConverter.DoubleToInt64Bits(left.Value) == BitConverter.DoubleToInt64Bits(right!.Value));

    private static bool SameInstant(DateTimeOffset? left, DateTimeOffset? right)
        => left.HasValue == right.HasValue
            && (!left.HasValue || left.Value.UtcTicks == right!.Value.UtcTicks && left.Value.Offset == right.Value.Offset);

    private static CpuSensorMetrics CreateCpuSensors(
        NativeMetricSnapshotCommittedFrame frame,
        IReadOnlyDictionary<string, Reading> readings)
        => new(
            ProviderState(frame, readings, MetricResource.Cpu),
            ValueOrNull(readings, "cpu.packagePower"),
            ValueOrNull(readings, "cpu.coreVoltage"),
            ValueOrNull(readings, "cpu.packageCurrent"),
            ValueOrNull(readings, "cpu.temperature"),
            ValueOrNull(readings, "cpu.stapmPower"),
            ValueOrNull(readings, "cpu.actualPower"),
            ValueOrNull(readings, "cpu.averagePower"),
            ValueOrNull(readings, "cpu.tdcCurrent"),
            ValueOrNull(readings, "cpu.edcCurrent"),
            ValueOrNull(readings, "cpu.platformPower"),
            ValueOrNull(readings, "cpu.platformVoltage"),
            ValueOrNull(readings, "cpu.igpuFrequency"),
            ValueOrNull(readings, "cpu.igpuVoltage"),
            ValueOrNull(readings, "cpu.igpuTemperature"),
            ValueOrNull(readings, "cpu.smuFrequency"));

    private static HardwareSensorProviderState ProviderState(
        NativeMetricSnapshotCommittedFrame frame,
        ulong scopeHandle,
        IReadOnlyDictionary<string, Reading> readings)
    {
        var reading = readings
            .Where(pair => frame.Catalog.MetricBindings.GetValueOrDefault(pair.Value.Output.MetricHandle).ScopeHandle == scopeHandle)
            .Select(static pair => pair.Value)
            .FirstOrDefault(static value => value.IsAvailable);
        return reading.Output.SourceHandle == 0
            ? new HardwareSensorProviderState(
                string.Empty,
                HardwareSensorProviderStates.Unavailable,
                null)
            : new HardwareSensorProviderState(
                SourceId(frame, reading.Output.SourceHandle),
                HardwareSensorProviderStates.Current,
                null);
    }

    private static HardwareSensorProviderState ProviderState(
        NativeMetricSnapshotCommittedFrame frame,
        IReadOnlyDictionary<string, Reading> readings,
        MetricResource resource)
    {
        var available = readings.Any(pair =>
            frame.Catalog.MetricBindings.GetValueOrDefault(pair.Value.Output.MetricHandle).Field?.Resource == resource
            && pair.Value.IsAvailable);
        return new HardwareSensorProviderState(
            string.Empty,
            available ? HardwareSensorProviderStates.Active : HardwareSensorProviderStates.Unavailable,
            null);
    }

    private static Reading Get(
        IReadOnlyDictionary<string, Reading> readings,
        string id)
        => readings.GetValueOrDefault(id);

    private static double? ValueOrNull(
        IReadOnlyDictionary<string, Reading> readings,
        string id)
    {
        var reading = Get(readings, id);
        return reading.IsAvailable ? reading.Value : null;
    }

    private static ulong? UInt64OrNull(IReadOnlyDictionary<string, Reading> readings, string id)
        => Get(readings, id) is { IsAvailable: true } reading ? ToUInt64(reading) : null;

    private static uint? UInt32OrNull(IReadOnlyDictionary<string, Reading> readings, string id)
        => UInt64OrNull(readings, id) is <= uint.MaxValue and var value ? (uint)value : null;

    private static unsafe byte[]? DecodeBytes(Reading reading)
    {
        if (!reading.IsAvailable || reading.Output.ValueKind != (uint)NativeMetricSnapshotValueKind.Bytes) return null;
        var output = reading.Output;
        return new ReadOnlySpan<byte>(output.ValueBytes, checked((int)output.ByteCount)).ToArray();
    }

    private static double Decode(uint valueKind, ulong bits)
        => (NativeMetricSnapshotValueKind)valueKind switch
        {
            NativeMetricSnapshotValueKind.Float64 =>
                BitConverter.UInt64BitsToDouble(bits),
            NativeMetricSnapshotValueKind.Signed64 =>
                unchecked((long)bits),
            NativeMetricSnapshotValueKind.Unsigned64 => bits,
            _ => 0
        };

    private static bool IsAvailable(NativeMetricSnapshotMetricStatus status)
        => status is NativeMetricSnapshotMetricStatus.Current;

    private static SamplingObservationStatus Status(
        NativeMetricSnapshotMetricStatus status)
        => status switch
        {
            NativeMetricSnapshotMetricStatus.Current =>
                SamplingObservationStatus.Current,
            NativeMetricSnapshotMetricStatus.Unsupported =>
                SamplingObservationStatus.Unsupported,
            NativeMetricSnapshotMetricStatus.Skipped =>
                SamplingObservationStatus.NotRequested,
            _ => SamplingObservationStatus.Unavailable
        };

    public static SamplingObservationStatus ProjectSourceStatus(
        NativeMetricSnapshotSourceStatus status)
        => status switch
        {
            NativeMetricSnapshotSourceStatus.Complete =>
                SamplingObservationStatus.Current,
            NativeMetricSnapshotSourceStatus.Partial =>
                SamplingObservationStatus.Partial,
            NativeMetricSnapshotSourceStatus.Unsupported =>
                SamplingObservationStatus.Unsupported,
            NativeMetricSnapshotSourceStatus.Skipped =>
                SamplingObservationStatus.NotRequested,
            _ => SamplingObservationStatus.Unavailable
        };

    public static SamplingObservationStatus ProjectCompositeObservationStatus(
        NativeMetricSnapshotMetricStatus first,
        NativeMetricSnapshotMetricStatus second)
    {
        if (first == NativeMetricSnapshotMetricStatus.Current
            && second == NativeMetricSnapshotMetricStatus.Current)
        {
            return SamplingObservationStatus.Current;
        }
        if (first == NativeMetricSnapshotMetricStatus.Unsupported
            && second == NativeMetricSnapshotMetricStatus.Unsupported)
        {
            return SamplingObservationStatus.Unsupported;
        }
        return SamplingObservationStatus.Unavailable;
    }

    public static SamplingObservationStatus ProjectCompositeObservationStatus(
        NativeMetricSnapshotMetricStatus first,
        NativeMetricSnapshotMetricStatus second,
        NativeMetricSnapshotMetricStatus third)
    {
        if (first == NativeMetricSnapshotMetricStatus.Current
            && second == NativeMetricSnapshotMetricStatus.Current
            && third == NativeMetricSnapshotMetricStatus.Current)
        {
            return SamplingObservationStatus.Current;
        }
        if (first == NativeMetricSnapshotMetricStatus.Unsupported
            && second == NativeMetricSnapshotMetricStatus.Unsupported
            && third == NativeMetricSnapshotMetricStatus.Unsupported)
        {
            return SamplingObservationStatus.Unsupported;
        }
        return SamplingObservationStatus.Unavailable;
    }

    private static double RatioPercent(Reading value, Reading capacity)
    {
        if (!value.IsAvailable
            || !capacity.IsAvailable
            || !double.IsFinite(value.Value)
            || !double.IsFinite(capacity.Value)
            || capacity.Value <= 0)
        {
            return 0;
        }

        return Math.Clamp(value.Value * 100 / capacity.Value, 0, 100);
    }

    private static CpuMetricObservationStatus CpuStatus(
        NativeMetricSnapshotCommittedFrame frame,
        NativeMetricSnapshotMetricStatus status)
    {
        if (status is NativeMetricSnapshotMetricStatus.Current)
        {
            return CpuMetricObservationStatus.Complete;
        }
        if (status != NativeMetricSnapshotMetricStatus.Unavailable)
        {
            return CpuMetricObservationStatus.Unavailable;
        }

        var source = frame.Sources.SingleOrDefault(source =>
            frame.Catalog.SourceIds.TryGetValue(
                source.SourceHandle,
                out var sourceId)
            && string.Equals(
                sourceId,
                NativeMetricSnapshotSources.WindowsCpu,
                StringComparison.Ordinal));
        return (NativeMetricSnapshotSourceStatus)source.Status switch
        {
            NativeMetricSnapshotSourceStatus.Complete
                or NativeMetricSnapshotSourceStatus.Partial =>
                CpuMetricObservationStatus.Warming,
            NativeMetricSnapshotSourceStatus.Skipped =>
                CpuMetricObservationStatus.NotRequested,
            _ => CpuMetricObservationStatus.Unavailable
        };
    }

    private static string SourceId(
        NativeMetricSnapshotCommittedFrame frame,
        ulong sourceHandle)
        => frame.Catalog.SourceIds.GetValueOrDefault(sourceHandle)
            ?? string.Empty;


    private static int ToInt(double value)
        => double.IsFinite(value) && value > 0
            ? checked((int)Math.Round(value))
            : 0;

    private static ulong ToUInt64(Reading reading)
    {
        if (!reading.IsAvailable) return 0;
        return (NativeMetricSnapshotValueKind)reading.Output.ValueKind switch
        {
            NativeMetricSnapshotValueKind.Unsigned64 => reading.Output.ValueBits,
            NativeMetricSnapshotValueKind.Signed64 when reading.Output.ValueBits <= long.MaxValue => reading.Output.ValueBits,
            NativeMetricSnapshotValueKind.Float64 => double.IsFinite(reading.Value) && reading.Value > 0
                ? checked((ulong)Math.Round(reading.Value)) : 0,
            _ => throw new InvalidDataException("The aggregate field requires a declared nonnegative numeric value.")
        };
    }

    // A position in the frame's metric rows, not a copy of the 648-byte row.
    // Readings live only while one projection runs.
    internal readonly struct Reading
    {
        private static readonly NativeMetricSnapshotMetricOutput[] Missing = new NativeMetricSnapshotMetricOutput[1];
        private readonly NativeMetricSnapshotMetricOutput[]? rows;
        private readonly int index;

        public Reading(NativeMetricSnapshotMetricOutput[] rows, int index, double value)
        {
            this.rows = rows;
            this.index = index;
            Value = value;
        }

        public double Value { get; }

        public ref readonly NativeMetricSnapshotMetricOutput Output
        {
            get
            {
                if (rows is null) return ref Missing[0];
                return ref rows[index];
            }
        }

        public NativeMetricSnapshotMetricStatus Status =>
            (NativeMetricSnapshotMetricStatus)Output.Status;

        public bool IsAvailable =>
            NativeMetricSnapshotCommittedProjection.IsAvailable(Status);
    }

}
