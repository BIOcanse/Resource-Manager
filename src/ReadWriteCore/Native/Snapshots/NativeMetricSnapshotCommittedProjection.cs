using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.ReadWrite.Native;
using ResourceManager.ReadWrite.Windows.Metrics.Capture;

namespace ResourceManager.App.Infrastructure.Monitoring;

public static class NativeMetricSnapshotCommittedProjection
{
    public static HardwareMetricSnapshot Create(
        NativeMetricSnapshotCommittedFrame frame,
        string cpuName,
        string memoryHardwareDescription,
        ulong workspaceIdentity,
        string virtualMemoryDetail = "",
        Func<string, string>? legacyLabel = null)
        => Create(frame, cpuName, memoryHardwareDescription, workspaceIdentity, virtualMemoryDetail, legacyLabel, null);

    internal static HardwareMetricSnapshot Create(
        NativeMetricSnapshotCommittedFrame frame, string cpuName, string memoryHardwareDescription,
        ulong workspaceIdentity, string virtualMemoryDetail, Func<string, string>? legacyLabel,
        MetricReadingSelection? sourceSelection)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (workspaceIdentity == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(workspaceIdentity));
        }
        var selectedLegacy = sourceSelection?.LegacyHandles ?? frame.Catalog.LegacyMetricHandles;
        var requestAliases = selectedLegacy.GroupBy(static pair => pair.Value)
            .ToDictionary(static group => group.Key, static group => group.Select(static pair => pair.Key).ToArray());
        var readings = frame.Metrics.ToDictionary(
            metric => frame.Catalog.MetricIds[metric.MetricHandle],
            metric => new Reading(
                metric,
                Decode(metric.ValueKind, metric.ValueBits)),
            StringComparer.OrdinalIgnoreCase);
        foreach (var (address, handle) in selectedLegacy)
        {
            if (readings.TryGetValue(frame.Catalog.MetricIds[handle], out var reading))
                readings[address] = reading;
        }
        var cpuUsage = Get(readings, "cpu.usage");
        var cpuFrequency = Get(readings, "cpu.frequency");
        var cpuFrequencyPercent =
            Get(readings, "cpu.frequencyPercent");
        var cpu = new CpuMetrics(
             cpuName,
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
        var memory = new MemoryMetrics(
            ToUInt64(memoryUsed.Value),
            ToUInt64(memoryTotal.Value),
            RatioPercent(memoryUsed, memoryTotal),
            memoryUsed.IsAvailable
                && memoryTotal.IsAvailable,
            memoryHardwareDescription)
        {
            AvailableBytes = UInt64OrNull(readings, "memory.availableBytes"),
            ObservationStatus = ProjectCompositeObservationStatus(
                memoryUsed.Status,
                memoryTotal.Status)
        };
        var virtualUsed = Get(readings, "virtualMemory.usage");
        var virtualTotal = Get(readings, "virtualMemory.total");
        var virtualMemory = new VirtualMemoryMetrics(
            ToUInt64(virtualUsed.Value),
            ToUInt64(virtualTotal.Value),
            RatioPercent(virtualUsed, virtualTotal),
            virtualMemoryDetail,
            virtualUsed.IsAvailable && virtualTotal.IsAvailable)
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
        var items = CreateItems(frame, readings, memory, virtualMemory, legacyLabel);
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
            FieldBindings = frame.Catalog.MetricBindings.Values
                .Where(static binding => binding.Field is not null)
                .ToDictionary(static binding => binding.MetricId,
                    binding => new MetricDeviceFieldBinding(binding.DeviceIdentity,
                        binding.Field! with { Reading = sourceSelection?.Fields.GetValueOrDefault(binding.MetricHandle) },
                        binding.MetricId, binding.SourceId)
                    {
                        NpuEngine = binding.NpuEngine,
                        Device = binding.Device,
                        RequestAliases = requestAliases.GetValueOrDefault(binding.MetricHandle) ?? [],
                        AdapterKey = frame.Catalog.GpuScopeBindings.TryGetValue(binding.ScopeHandle, out var gpuBinding)
                            ? gpuBinding.AdapterLuid : null
                    }, StringComparer.Ordinal),
            Datasets = datasets,
            WorkspaceIdentity = workspaceIdentity,
            ConfigurationGeneration =
                frame.Header.ConfigurationGeneration,
            CatalogGeneration = frame.Header.CatalogGeneration,
            CommittedGeneration = frame.Header.CommittedGeneration
        };
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
            StringComparer.OrdinalIgnoreCase);
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
                FailureCode = DatasetFailureCode(status)
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
        foreach (var binding in frame.Catalog.GpuScopeBindings.Values)
        {
            var prefix = $"gpu.{binding.DisplayIndex}.";
            ReplaceCompositeDatasetObservation(
                result,
                readings,
                prefix + "vram",
                prefix + "vramTotal",
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
            SamplingObservationStatus.RetainedLastGood =>
                "hardware-native-retained",
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
                ToUInt64(usedMemory.Value),
                ToUInt64(totalMemory.Value),
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
                binding.ExactIdentityKey) { AdapterKey = binding.AdapterLuid });
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
                ToUInt64(used.Value),
                ToUInt64(total.Value),
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

    private static IReadOnlyDictionary<string, MetricValue> CreateItems(
        NativeMetricSnapshotCommittedFrame frame,
        IReadOnlyDictionary<string, Reading> readings,
        MemoryMetrics memory,
        VirtualMemoryMetrics virtualMemory,
        Func<string, string>? legacyLabel)
    {
        var items = new Dictionary<string, MetricValue>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var (id, reading) in readings)
        {
            var binding = frame.Catalog.MetricBindings.GetValueOrDefault(reading.Output.MetricHandle);
            if (id != binding.MetricId && binding.FieldCode is MetricFieldCode.MemoryTotal
                or MetricFieldCode.CommitLimit or MetricFieldCode.GpuMemoryTotal) continue;
            var field = binding.Field;
            var presentation = Presentation(field?.Unit ?? string.Empty, reading.Value);
            var valueAvailable = reading.IsAvailable;
            var detail = SourceId(
                frame,
                reading.Output.SourceHandle);
            if (id == binding.MetricId && field?.Meaning is { } meaning) detail += "; " + meaning;
            if (id != binding.MetricId && binding.FieldCode == MetricFieldCode.MemoryUsed)
            {
                presentation = new PresentationValue(
                    string.Empty,
                    memory.UsedBytes,
                    MetricUnits.Bytes,
                    memory.UsagePercent,
                    memory.TotalBytes);
                valueAvailable = memory.IsUsageAvailable;
            }
            else if (id != binding.MetricId && binding.FieldCode == MetricFieldCode.CommitUsed)
            {
                presentation = new PresentationValue(
                    string.Empty,
                    virtualMemory.UsedBytes,
                    MetricUnits.Bytes,
                    virtualMemory.UsagePercent,
                    virtualMemory.TotalBytes);
                valueAvailable = virtualMemory.IsSelectable;
            }
            else if (id != binding.MetricId && binding.FieldCode == MetricFieldCode.GpuMemoryUsed)
            {
                var totalBinding = frame.Catalog.MetricBindings.Values.FirstOrDefault(row =>
                    row.ScopeHandle == binding.ScopeHandle && row.FieldCode == MetricFieldCode.GpuMemoryTotal);
                var total = Get(readings, totalBinding.MetricId ?? string.Empty);
                presentation = new PresentationValue(
                    string.Empty,
                    ToUInt64(reading.Value),
                    MetricUnits.Bytes,
                    RatioPercent(reading, total),
                    ToUInt64(total.Value));
                valueAvailable = reading.IsAvailable && total.IsAvailable;
            }
            if ((id == binding.MetricId && reading.Output.ValueKind == (uint)NativeMetricSnapshotValueKind.Unsigned64)
                || reading.Output.ValueKind is (uint)NativeMetricSnapshotValueKind.Unsigned128 or (uint)NativeMetricSnapshotValueKind.Bytes or (uint)NativeMetricSnapshotValueKind.Boolean)
                presentation = new PresentationValue(string.Empty, null, presentation.Unit, null);
            if (!valueAvailable)
            {
                presentation = presentation with
                {
                    // 容量类指标不带显示字符串，空读数就是 Numeric 为 null，前端自己说「无数据」。
                    Display = presentation.Unit == MetricUnits.Bytes ? string.Empty : "N/A",
                    Numeric = null,
                    Percent = null,
                    Total = null
                };
            }
            items[id] = new MetricValue(
                id,
                id == binding.MetricId ? field?.Name ?? id : legacyLabel?.Invoke(id) ?? id,
                field?.Resource.ToString() ?? string.Empty,
                presentation.Display,
                presentation.Numeric,
                presentation.Unit,
                presentation.Percent,
                detail,
                presentation.Total)
            {
                FieldName = field?.Name,
                DeviceIdentity = binding.DeviceIdentity,
                SourceId = SourceId(frame, reading.Output.SourceHandle),
                ValueKind = field?.ValueKind,
                SampleDurationMilliseconds = valueAvailable && reading.Output.SampleDurationMilliseconds > 0
                    ? reading.Output.SampleDurationMilliseconds : null,
                IntegerValue = valueAvailable ? (NativeMetricSnapshotValueKind)reading.Output.ValueKind switch
                {
                    NativeMetricSnapshotValueKind.Signed64 => unchecked((long)reading.Output.ValueBits)
                        .ToString(System.Globalization.CultureInfo.InvariantCulture),
                    NativeMetricSnapshotValueKind.Unsigned64 => reading.Output.ValueBits.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    NativeMetricSnapshotValueKind.Unsigned128 => (((UInt128)reading.Output.ValueHighBits << 64) | reading.Output.ValueBits)
                        .ToString(System.Globalization.CultureInfo.InvariantCulture),
                    _ => null
                } : null,
                BytesValue = DecodeBytes(reading),
                BooleanValue = valueAvailable && reading.Output.ValueKind == (uint)NativeMetricSnapshotValueKind.Boolean
                    ? reading.Output.ValueBits == 1 : null
            };
            if (field?.Unit == "bitmask64")
                items[id] = items[id] with { NumericValue = null, DisplayValue = string.Empty };
        }
        return items;
    }

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
                "Host Manager metric snapshot",
                "Unavailable",
                null)
            : new HardwareSensorProviderState(
                SourceId(frame, reading.Output.SourceHandle),
                reading.Status.ToString(),
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
            "Host Manager metric snapshot",
            available ? "Active" : "Unavailable",
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
        => ValueOrNull(readings, id) is { } value ? ToUInt64(value) : null;

    private static uint? UInt32OrNull(IReadOnlyDictionary<string, Reading> readings, string id)
        => ValueOrNull(readings, id) is >= 0 and <= uint.MaxValue and var value ? (uint)value : null;

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
        => status is NativeMetricSnapshotMetricStatus.Current
            or NativeMetricSnapshotMetricStatus.Retained;

    private static SamplingObservationStatus Status(
        NativeMetricSnapshotMetricStatus status)
        => status switch
        {
            NativeMetricSnapshotMetricStatus.Current =>
                SamplingObservationStatus.Current,
            NativeMetricSnapshotMetricStatus.Retained =>
                SamplingObservationStatus.RetainedLastGood,
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
        if (IsAvailable(first) && IsAvailable(second))
        {
            return SamplingObservationStatus.RetainedLastGood;
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
        if (IsAvailable(first) && IsAvailable(second) && IsAvailable(third))
        {
            return SamplingObservationStatus.RetainedLastGood;
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
        if (status is NativeMetricSnapshotMetricStatus.Current
            or NativeMetricSnapshotMetricStatus.Retained)
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
            ?? "Host Manager metric snapshot";

    private static PresentationValue Presentation(string unit, double value)
        => unit switch
        {
            "%" => new($"{value:0.#}%", value, unit, value),
            "MHz" => new($"{value:0} MHz", value, unit, null),
            "°C" => new($"{value:0.#} °C", value, unit, null),
            "V" => new($"{value:0.###} V", value, unit, null),
            "A" => new($"{value:0.#} A", value, unit, null),
            "W" => new($"{value:0.#} W", value, unit, null),
            "RPM" => new($"{value:0} RPM", value, unit, null),
            "B" or "bitmask64" => new(string.Empty, value, unit, null),
            "B/s" => new($"{value / 1024d / 1024d:0.##} MB/s", value, unit, null),
            _ => new(value.ToString("0.##"), value, unit, null)
        };

    private static int ToInt(double value)
        => double.IsFinite(value) && value > 0
            ? checked((int)Math.Round(value))
            : 0;

    private static ulong ToUInt64(double value)
        => double.IsFinite(value) && value > 0
            ? checked((ulong)Math.Round(value))
            : 0;

    private readonly record struct Reading(
        NativeMetricSnapshotMetricOutput Output,
        double Value)
    {
        public NativeMetricSnapshotMetricStatus Status =>
            (NativeMetricSnapshotMetricStatus)Output.Status;

        public bool IsAvailable =>
            NativeMetricSnapshotCommittedProjection.IsAvailable(Status);
    }

    private readonly record struct PresentationValue(
        string Display,
        double? Numeric,
        string Unit,
        double? Percent,
        double? Total = null);
}
