using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Infrastructure.NativeCore;

namespace ResourceManager.App.Infrastructure.Monitoring;

internal static class NativeMetricSnapshotCommittedProjection
{
    internal static HardwareMetricSnapshot Create(
        NativeMetricSnapshotCommittedFrame frame,
        string cpuName,
        string memoryHardwareDescription,
        ulong workspaceIdentity)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (workspaceIdentity == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(workspaceIdentity));
        }
        var readings = frame.Metrics.ToDictionary(
            metric => frame.Catalog.MetricIds[metric.MetricHandle],
            metric => new Reading(
                metric,
                Decode(metric.ValueKind, metric.ValueBits)),
            StringComparer.OrdinalIgnoreCase);
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
            CreateCpuSensors(readings));
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
            "Windows page file",
            virtualUsed.IsAvailable && virtualTotal.IsAvailable)
        {
            ObservationStatus = ProjectCompositeObservationStatus(
                virtualUsed.Status,
                virtualTotal.Status)
        };
        var gpus = CreateGpus(frame, readings);
        var inventory = CreateGpuInventory(frame, readings);
        var items = CreateItems(frame, readings, memory, virtualMemory);
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
            Datasets = datasets,
            WorkspaceIdentity = workspaceIdentity,
            ConfigurationGeneration =
                frame.Header.ConfigurationGeneration,
            CatalogGeneration = frame.Header.CatalogGeneration,
            CommittedGeneration = frame.Header.CommittedGeneration
        };
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
            var datasetId = SamplingDatasetIds.ForSystemMetric(metricId);
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

        result[SamplingDatasetIds.SystemGpuInventory] =
            new HardwareMetricDatasetObservation(
                SamplingDatasetIds.SystemGpuInventory,
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
                    ProviderState(frame, prefix, readings),
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
                binding.ExactIdentityKey));
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
                NativeMetricSnapshotSourceCatalog.WindowsGpuAdapterOrder,
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
        VirtualMemoryMetrics virtualMemory)
    {
        var items = new Dictionary<string, MetricValue>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var (id, reading) in readings)
        {
            if (IsInternalTotal(id))
            {
                continue;
            }
            var presentation = Presentation(id, reading.Value);
            var valueAvailable = reading.IsAvailable;
            var detail = SourceId(
                frame,
                reading.Output.SourceHandle);
            if (string.Equals(
                    id,
                    "memory.usage",
                    StringComparison.OrdinalIgnoreCase))
            {
                presentation = new PresentationValue(
                    string.Empty,
                    memory.UsedBytes,
                    MetricUnits.Bytes,
                    memory.UsagePercent,
                    memory.TotalBytes);
                valueAvailable = memory.IsUsageAvailable;
            }
            else if (string.Equals(
                         id,
                         "virtualMemory.usage",
                         StringComparison.OrdinalIgnoreCase))
            {
                presentation = new PresentationValue(
                    string.Empty,
                    virtualMemory.UsedBytes,
                    MetricUnits.Bytes,
                    virtualMemory.UsagePercent,
                    virtualMemory.TotalBytes);
                valueAvailable = virtualMemory.IsSelectable;
            }
            else if (id.EndsWith(
                         ".vram",
                         StringComparison.OrdinalIgnoreCase))
            {
                var total = Get(
                    readings,
                    id[..^".vram".Length] + ".vramTotal");
                presentation = new PresentationValue(
                    string.Empty,
                    ToUInt64(reading.Value),
                    MetricUnits.Bytes,
                    RatioPercent(reading, total),
                    ToUInt64(total.Value));
                valueAvailable = reading.IsAvailable && total.IsAvailable;
            }
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
                Label(id),
                Group(id),
                presentation.Display,
                presentation.Numeric,
                presentation.Unit,
                presentation.Percent,
                detail,
                presentation.Total);
        }
        return items;
    }

    private static CpuSensorMetrics CreateCpuSensors(
        IReadOnlyDictionary<string, Reading> readings)
        => new(
            ProviderState(readings, "cpu."),
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
        string prefix,
        IReadOnlyDictionary<string, Reading> readings)
    {
        var reading = readings
            .Where(pair => pair.Key.StartsWith(
                prefix,
                StringComparison.OrdinalIgnoreCase))
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
        IReadOnlyDictionary<string, Reading> readings,
        string prefix)
    {
        var available = readings.Any(pair =>
            pair.Key.StartsWith(
                prefix,
                StringComparison.OrdinalIgnoreCase)
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

    internal static SamplingObservationStatus ProjectSourceStatus(
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

    internal static SamplingObservationStatus ProjectCompositeObservationStatus(
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

    internal static SamplingObservationStatus ProjectCompositeObservationStatus(
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
                NativeMetricSnapshotSourceCatalog.WindowsCpu,
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

    private static bool IsInternalTotal(string id)
        => string.Equals(
                id,
                "memory.total",
                StringComparison.OrdinalIgnoreCase)
            || string.Equals(
                id,
                "virtualMemory.total",
                StringComparison.OrdinalIgnoreCase)
            || id.EndsWith(
                ".vramTotal",
                StringComparison.OrdinalIgnoreCase);

    private static PresentationValue Presentation(
        string id,
        double value)
    {
        if (id.EndsWith(
                "Percent",
                StringComparison.OrdinalIgnoreCase)
            || id.EndsWith(
                ".usage",
                StringComparison.OrdinalIgnoreCase)
            && (id.StartsWith(
                    "cpu.",
                    StringComparison.OrdinalIgnoreCase)
                || id.StartsWith(
                    "gpu.",
                    StringComparison.OrdinalIgnoreCase))
            || id.EndsWith(
                "activePercent",
                StringComparison.OrdinalIgnoreCase))
        {
            return new PresentationValue(
                $"{value:0.#}%",
                value,
                "%",
                value);
        }
        if (id.Contains(
                "frequency",
                StringComparison.OrdinalIgnoreCase)
            || id.Contains(
                "Clock",
                StringComparison.OrdinalIgnoreCase))
        {
            return new PresentationValue(
                $"{value:0} MHz",
                value,
                "MHz",
                null);
        }
        if (id.Contains(
                "temperature",
                StringComparison.OrdinalIgnoreCase))
        {
            return new PresentationValue(
                $"{value:0.#} °C",
                value,
                "°C",
                null);
        }
        if (id.Contains(
                "voltage",
                StringComparison.OrdinalIgnoreCase))
        {
            return new PresentationValue(
                $"{value:0.###} V",
                value,
                "V",
                null);
        }
        if (id.Contains(
                "current",
                StringComparison.OrdinalIgnoreCase))
        {
            return new PresentationValue(
                $"{value:0.#} A",
                value,
                "A",
                null);
        }
        if (id.Contains(
                "power",
                StringComparison.OrdinalIgnoreCase))
        {
            return new PresentationValue(
                $"{value:0.#} W",
                value,
                "W",
                null);
        }
        if (id.EndsWith(
                "fanRpm",
                StringComparison.OrdinalIgnoreCase)
            || id.EndsWith(
                ".speed",
                StringComparison.OrdinalIgnoreCase))
        {
            return new PresentationValue(
                $"{value:0} RPM",
                value,
                "RPM",
                null);
        }
        if (id.Contains(
                "BytesPerSec",
                StringComparison.OrdinalIgnoreCase))
        {
            return new PresentationValue(
                $"{value / 1024d / 1024d:0.##} MB/s",
                value,
                "B/s",
                null);
        }
        return new PresentationValue(
            value.ToString("0.##"),
            value,
            string.Empty,
            null);
    }

    private static string Group(string id)
        => id.StartsWith("cpu.", StringComparison.OrdinalIgnoreCase)
            ? "CPU"
            : id.StartsWith("memory.", StringComparison.OrdinalIgnoreCase)
                ? "Memory"
                : id.StartsWith(
                    "virtualMemory.",
                    StringComparison.OrdinalIgnoreCase)
                    ? "Virtual Memory"
                    : id.StartsWith(
                        "gpu.",
                        StringComparison.OrdinalIgnoreCase)
                        ? "GPU"
                        : id.StartsWith(
                            "disk.",
                            StringComparison.OrdinalIgnoreCase)
                            ? "Disk"
                            : id.StartsWith(
                                "network.",
                                StringComparison.OrdinalIgnoreCase)
                                ? "Network"
                                : "System";

    private static string Label(string id)
        => id switch
        {
            "cpu.usage" => "CPU 占用率",
            "cpu.frequency" => "CPU 频率",
            "cpu.frequencyPercent" => "CPU 频率百分比",
            "memory.usage" => "内存占用",
            "memory.percent" => "内存占用率",
            "virtualMemory.usage" => "虚拟内存占用",
            "virtualMemory.percent" => "虚拟内存占用率",
            _ => id
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
        internal NativeMetricSnapshotMetricStatus Status =>
            (NativeMetricSnapshotMetricStatus)Output.Status;

        internal bool IsAvailable =>
            NativeMetricSnapshotCommittedProjection.IsAvailable(Status);
    }

    private readonly record struct PresentationValue(
        string Display,
        double? Numeric,
        string Unit,
        double? Percent,
        double? Total = null);
}
