using System.Collections.Immutable;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Infrastructure.Monitoring.AmdSmu;
using ResourceManager.App.Infrastructure.NativeCore;

namespace ResourceManager.App.Infrastructure.Monitoring;

public sealed partial class WindowsHardwareMetricSampler
{
    private async Task<NativeMetricSnapshotSourceCompletion>
        CollectNativeSourceAsync(
            NativeMetricSnapshotCollectionPlan plan,
            NativeMetricSnapshotSourcePlanOutput sourcePlan,
            CancellationToken cancellationToken)
    {
        var sourceId = plan.Catalog.SourceIds[sourcePlan.SourceHandle];
        var identity =
            sourceRuntimeFacts.BeginObservation(sourcePlan.SourceHandle);
        var capturedAt = DateTimeOffset.UtcNow;
        try
        {
            var completion = sourceId switch
            {
                NativeMetricSnapshotSourceCatalog.WindowsCpu =>
                    CollectCpu(plan, sourcePlan, identity),
                NativeMetricSnapshotSourceCatalog.WindowsMemory =>
                    CollectMemory(plan, sourcePlan, identity, capturedAt),
                NativeMetricSnapshotSourceCatalog.WindowsVirtualMemory =>
                    CollectVirtualMemory(
                        plan,
                        sourcePlan,
                        identity,
                        capturedAt),
                NativeMetricSnapshotSourceCatalog.WindowsGpuAdapterOrder =>
                    CollectGpuInventory(
                        plan,
                        sourcePlan,
                        identity,
                        capturedAt),
                NativeMetricSnapshotSourceCatalog.PdhCpuFrequency =>
                    CollectCpuFrequency(
                        plan,
                        sourcePlan,
                        identity,
                        capturedAt),
                NativeMetricSnapshotSourceCatalog.PdhGpuEngine =>
                    CollectPdhGpu(
                        plan,
                        sourcePlan,
                        identity,
                        capturedAt),
                NativeMetricSnapshotSourceCatalog.NvidiaNvml =>
                    await CollectNvmlAsync(
                        plan,
                        sourcePlan,
                        identity,
                        capturedAt,
                        cancellationToken),
                NativeMetricSnapshotSourceCatalog.NvidiaNvapi =>
                    await CollectNvapiAsync(
                        plan,
                        sourcePlan,
                        identity,
                        capturedAt,
                        cancellationToken),
                NativeMetricSnapshotSourceCatalog.AmdAdlx =>
                    await CollectAdlxAsync(
                        plan,
                        sourcePlan,
                        identity,
                        capturedAt,
                        cancellationToken),
                NativeMetricSnapshotSourceCatalog.AmdSmu =>
                    CollectAmdSmu(
                        plan,
                        sourcePlan,
                        identity,
                        capturedAt),
                NativeMetricSnapshotSourceCatalog.WindowsStorageSensors =>
                    CollectStorageSensors(
                        plan,
                        sourcePlan,
                        identity,
                        capturedAt),
                NativeMetricSnapshotSourceCatalog.HardwareMonitorWmi =>
                    CollectHardwareMonitor(
                        plan,
                        sourcePlan,
                        identity,
                        capturedAt),
                NativeMetricSnapshotSourceCatalog.NotebookOemFan =>
                    CollectNotebookFans(
                        plan,
                        sourcePlan,
                        identity,
                        capturedAt),
                NativeMetricSnapshotSourceCatalog.PdhSystemIo =>
                    CollectSystemIo(
                        plan,
                        sourcePlan,
                        identity,
                        capturedAt),
                _ => throw new InvalidOperationException(
                    $"Unknown native metric source '{sourceId}'.")
            };
            sourceRuntimeFacts.RecordCompletion(
                sourcePlan.SourceHandle,
                completion.Status,
                DateTimeOffset.UtcNow);
            return completion;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Metric source {SourceId} failed.",
                sourceId);
            sourceRuntimeFacts.RecordCompletion(
                sourcePlan.SourceHandle,
                NativeMetricSnapshotSourceStatus.Unavailable,
                DateTimeOffset.UtcNow);
            return Unavailable(
                sourcePlan.SourceHandle,
                identity,
                capturedAt);
        }
    }

    private NativeMetricSnapshotSourceCompletion CollectCpu(
        NativeMetricSnapshotCollectionPlan plan,
        NativeMetricSnapshotSourcePlanOutput sourcePlan,
        NativeMetricSnapshotSourceObservationIdentity identity)
    {
        var counter = cpuZone.ReadCounters();
        if (!counter.IsAvailable)
        {
            return Unavailable(
                sourcePlan.SourceHandle,
                identity,
                counter.ObservedAt);
        }
        var metric = AssertSingleMetric(plan, sourcePlan.SourceHandle);
        var binding = plan.Catalog.RuleBindings[metric.RuleHandle];
        var nativeCounter =
            NativeMetricSnapshotObservationProjection.CreateCpuCounter(
                metric,
                binding,
                identity,
                counter);
        return Complete(
            sourcePlan.SourceHandle,
            identity,
            counter.ObservedAt,
            [],
            nativeCounter);
    }

    private NativeMetricSnapshotSourceCompletion CollectMemory(
        NativeMetricSnapshotCollectionPlan plan,
        NativeMetricSnapshotSourcePlanOutput sourcePlan,
        NativeMetricSnapshotSourceObservationIdentity identity,
        DateTimeOffset capturedAt)
    {
        var metrics = memoryZone.ReadMetrics();
        if (!metrics.IsUsageAvailable || metrics.TotalBytes == 0)
        {
            return Unavailable(
                sourcePlan.SourceHandle,
                identity,
                capturedAt);
        }
        return CompleteWithValues(
            plan,
            sourcePlan,
            identity,
            capturedAt,
            id => id switch
            {
                "memory.usage" => metrics.UsedBytes,
                "memory.total" => metrics.TotalBytes,
                "memory.percent" => metrics.UsagePercent,
                _ => null
            });
    }

    private NativeMetricSnapshotSourceCompletion CollectVirtualMemory(
        NativeMetricSnapshotCollectionPlan plan,
        NativeMetricSnapshotSourcePlanOutput sourcePlan,
        NativeMetricSnapshotSourceObservationIdentity identity,
        DateTimeOffset capturedAt)
    {
        var metrics = virtualMemoryZone.ReadMetrics();
        if (!metrics.IsSelectable || metrics.TotalBytes == 0)
        {
            return CompleteWithValues(
                plan,
                sourcePlan,
                identity,
                capturedAt,
                static _ => null,
                NativeMetricSnapshotObservationStatus.Unsupported);
        }
        return CompleteWithValues(
            plan,
            sourcePlan,
            identity,
            capturedAt,
            id => id switch
            {
                "virtualMemory.usage" => metrics.UsedBytes,
                "virtualMemory.total" => metrics.TotalBytes,
                "virtualMemory.percent" => metrics.UsagePercent,
                _ => null
            });
    }

    private NativeMetricSnapshotSourceCompletion CollectGpuInventory(
        NativeMetricSnapshotCollectionPlan plan,
        NativeMetricSnapshotSourcePlanOutput sourcePlan,
        NativeMetricSnapshotSourceObservationIdentity identity,
        DateTimeOffset capturedAt)
    {
        var inventory = RequireCurrentGpuInventory(plan.Catalog);
        var source = plan.Catalog.Sources.Single(
            source => source.SourceHandle == sourcePlan.SourceHandle);
        var rows =
            NativeMetricSnapshotObservationProjection.CreateGpuInventory(
                plan.Catalog,
                identity,
                inventory,
                source.CapabilityMask,
                capturedAt);
        if (rows.Length != inventory.Adapters.Count)
        {
            return Unavailable(
                sourcePlan.SourceHandle,
                identity,
                capturedAt);
        }
        return Complete(
            sourcePlan.SourceHandle,
            identity,
            capturedAt,
            [],
            null,
            rows,
            checked((uint)rows.Length));
    }

    private NativeMetricSnapshotSourceCompletion CollectCpuFrequency(
        NativeMetricSnapshotCollectionPlan plan,
        NativeMetricSnapshotSourcePlanOutput sourcePlan,
        NativeMetricSnapshotSourceObservationIdentity identity,
        DateTimeOffset capturedAt)
    {
        var frequency = cpuFrequencyZone.ReadFrequency();
        if (frequency.CurrentMhz <= 0)
        {
            return Unavailable(
                sourcePlan.SourceHandle,
                identity,
                capturedAt);
        }
        return CompleteWithValues(
            plan,
            sourcePlan,
            identity,
            capturedAt,
            id => id switch
            {
                "cpu.frequency" => frequency.CurrentMhz,
                "cpu.frequencyPercent" => frequency.Percent,
                _ => null
            });
    }

    private NativeMetricSnapshotSourceCompletion CollectPdhGpu(
        NativeMetricSnapshotCollectionPlan plan,
        NativeMetricSnapshotSourcePlanOutput sourcePlan,
        NativeMetricSnapshotSourceObservationIdentity identity,
        DateTimeOffset capturedAt)
    {
        var inventory = RequireCurrentGpuInventory(plan.Catalog);
        var usage =
            gpuEngineZone.ReadUsageSnapshotByAdapterIndex(
                inventory.Adapters);
        if (usage.ProviderAvailability
                != NativePdhProviderAvailability.Available
            || usage.ObservedAt is not { } observedAt)
        {
            return Unavailable(
                sourcePlan.SourceHandle,
                identity,
                capturedAt);
        }
        return CompleteWithValues(
            plan,
            sourcePlan,
            identity,
            observedAt,
            id => TryGpuMetricId(id, out var index, out var name)
                && string.Equals(
                    name,
                    "usage",
                    StringComparison.Ordinal)
                && usage.UsageByAdapterIndex.TryGetValue(
                    index,
                    out var value)
                    ? value
                    : null);
    }

    private async Task<NativeMetricSnapshotSourceCompletion>
        CollectNvmlAsync(
            NativeMetricSnapshotCollectionPlan plan,
            NativeMetricSnapshotSourcePlanOutput sourcePlan,
            NativeMetricSnapshotSourceObservationIdentity identity,
            DateTimeOffset capturedAt,
            CancellationToken cancellationToken)
    {
        var selected = SelectedMetricIds(plan, sourcePlan.SourceHandle);
        var indexes = GpuIndexes(selected);
        var request = new NvidiaNvmlReadRequest(
            indexes,
            Includes(selected, "usage"),
            Includes(selected, "graphicsClock")
                || Includes(selected, "graphicsClockPercent"),
            Includes(selected, "vram")
                || Includes(selected, "vramPercent")
                || Includes(selected, "vramTotal"),
            Includes(selected, "memoryClock"),
            Includes(selected, "power"),
            Includes(selected, "powerLimit"),
            Includes(selected, "temperature"),
            Includes(selected, "fanPercent"),
            IncludeElectricalState: false);
        var inventory = RequireCurrentGpuInventory(plan.Catalog);
        var values = await nvidiaZone.ReadAsync(
            request,
            inventory,
            cancellationToken);
        if (values.Count == 0)
        {
            return Unavailable(
                sourcePlan.SourceHandle,
                identity,
                capturedAt);
        }
        var byIndex = values.ToDictionary(static gpu => gpu.Index);
        return CompleteWithValues(
            plan,
            sourcePlan,
            identity,
            capturedAt,
            id => ResolveGpuValue(id, byIndex));
    }

    private async Task<NativeMetricSnapshotSourceCompletion>
        CollectNvapiAsync(
            NativeMetricSnapshotCollectionPlan plan,
            NativeMetricSnapshotSourcePlanOutput sourcePlan,
            NativeMetricSnapshotSourceObservationIdentity identity,
            DateTimeOffset capturedAt,
            CancellationToken cancellationToken)
    {
        var selected = SelectedMetricIds(plan, sourcePlan.SourceHandle);
        var request = new NvidiaNvapiReadRequest(
            GpuIndexes(selected),
            Includes(selected, "fanRpm"),
            Includes(selected, "coreVoltage"),
            Includes(selected, "current"));
        var values = await nvidiaNvapiZone.ReadAsync(
            request,
            RequireCurrentGpuInventory(plan.Catalog),
            cancellationToken);
        if (values.Count == 0)
        {
            return Unavailable(
                sourcePlan.SourceHandle,
                identity,
                capturedAt);
        }
        var byIndex = values.ToDictionary(
            static sensor => sensor.DisplayIndex);
        return CompleteWithValues(
            plan,
            sourcePlan,
            identity,
            capturedAt,
            id =>
            {
                if (!TryGpuMetricId(id, out var index, out var name)
                    || !byIndex.TryGetValue(index, out var sensor))
                {
                    return null;
                }
                return name switch
                {
                    "fanRpm" => sensor.Sensors.FanSpeedRpm,
                    "coreVoltage" =>
                        sensor.Sensors.CoreVoltageVolts,
                    "current" => sensor.Sensors.CurrentAmps,
                    _ => null
                };
            });
    }

    private async Task<NativeMetricSnapshotSourceCompletion>
        CollectAdlxAsync(
            NativeMetricSnapshotCollectionPlan plan,
            NativeMetricSnapshotSourcePlanOutput sourcePlan,
            NativeMetricSnapshotSourceObservationIdentity identity,
            DateTimeOffset capturedAt,
            CancellationToken cancellationToken)
    {
        var selected = SelectedMetricIds(plan, sourcePlan.SourceHandle);
        var request = new AmdAdlxReadRequest(
            GpuIndexes(selected),
            Includes(selected, "usage"),
            Includes(selected, "graphicsClock")
                || Includes(selected, "graphicsClockPercent"),
            Includes(selected, "vram")
                || Includes(selected, "vramPercent")
                || Includes(selected, "vramTotal"),
            Includes(selected, "memoryClock"),
            Includes(selected, "power"),
            Includes(selected, "boardPower"),
            Includes(selected, "temperature"),
            Includes(selected, "hotspotTemperature"),
            Includes(selected, "intakeTemperature"),
            Includes(selected, "fanPercent"),
            Includes(selected, "coreVoltage"));
        var values = await amdAdlxZone.ReadAsync(
            request,
            RequireCurrentGpuInventory(plan.Catalog),
            cancellationToken);
        if (values.Count == 0)
        {
            return Unavailable(
                sourcePlan.SourceHandle,
                identity,
                capturedAt);
        }
        var byIndex = values.ToDictionary(static gpu => gpu.Index);
        return CompleteWithValues(
            plan,
            sourcePlan,
            identity,
            capturedAt,
            id => ResolveGpuValue(id, byIndex));
    }

    private NativeMetricSnapshotSourceCompletion CollectAmdSmu(
        NativeMetricSnapshotCollectionPlan plan,
        NativeMetricSnapshotSourcePlanOutput sourcePlan,
        NativeMetricSnapshotSourceObservationIdentity identity,
        DateTimeOffset capturedAt)
    {
        var selected = SelectedMetricIds(plan, sourcePlan.SourceHandle);

        // 核显的频率/温度/电压也从这里出。核显没有自己的传感库（ADLX 服务的是独显），
        // 但那几个量一直躺在处理器的 PM table 里 —— 因为核显就是这颗处理器的一部分。
        // 哪一块是核显，由启动时认定的路由回答，不按索引猜。
        var integratedGpuIndex = integratedGpuSensorRoute.Resolve(
            gpuAdapterOrderZone.ReadInventory(requested: true).Adapters);
        var integratedGpuSensors = integratedGpuIndex is { } gpuIndex
            ? selected
                .Select(id => AmdIntegratedGpuSensorRoute.SensorKindOf(id, gpuIndex))
                .Where(static kind => kind is not null)
                .Select(static kind => kind!.Value)
                .ToHashSet()
            : [];

        var metrics = amdSmuZone.Read(new AmdSmuCpuSensorReadRequest(
            IncludesExact(selected, "cpu.packagePower"),
            IncludesExact(selected, "cpu.coreVoltage"),
            IncludesExact(selected, "cpu.packageCurrent"),
            IncludesExact(selected, "cpu.temperature"),
            IncludesExact(selected, "cpu.stapmPower"),
            IncludesExact(selected, "cpu.actualPower"),
            IncludesExact(selected, "cpu.averagePower"),
            IncludesExact(selected, "cpu.tdcCurrent"),
            IncludesExact(selected, "cpu.edcCurrent"),
            IncludesExact(selected, "cpu.platformPower"),
            IncludesExact(selected, "cpu.platformVoltage"),
            IncludesExact(selected, "cpu.igpuFrequency")
                || integratedGpuSensors.Contains(AmdIntegratedGpuSensorKind.GraphicsClock),
            IncludesExact(selected, "cpu.igpuVoltage")
                || integratedGpuSensors.Contains(AmdIntegratedGpuSensorKind.CoreVoltage),
            IncludesExact(selected, "cpu.igpuTemperature")
                || integratedGpuSensors.Contains(AmdIntegratedGpuSensorKind.Temperature),
            IncludesExact(selected, "cpu.frequency")
                || IncludesExact(selected, "cpu.smuFrequency")));
        return CompleteWithValues(
            plan,
            sourcePlan,
            identity,
            capturedAt,
            id => id switch
            {
                "cpu.frequency" => metrics.SmuFrequencyMhz,
                "cpu.frequencyPercent" => null,
                "cpu.packagePower" => metrics.PackagePowerWatts,
                "cpu.coreVoltage" => metrics.CoreVoltageVolts,
                "cpu.packageCurrent" =>
                    metrics.PackageCurrentAmps,
                "cpu.temperature" => metrics.TemperatureCelsius,
                "cpu.stapmPower" => metrics.StapmPowerWatts,
                "cpu.actualPower" => metrics.ActualPowerWatts,
                "cpu.averagePower" => metrics.AveragePowerWatts,
                "cpu.tdcCurrent" => metrics.TdcCurrentAmps,
                "cpu.edcCurrent" => metrics.EdcCurrentAmps,
                "cpu.platformPower" => metrics.SocPowerWatts,
                "cpu.platformVoltage" => metrics.SocVoltageVolts,
                "cpu.igpuFrequency" => metrics.ApuFrequencyMhz,
                "cpu.igpuVoltage" => metrics.ApuVoltageVolts,
                "cpu.igpuTemperature" =>
                    metrics.ApuTemperatureCelsius,
                "cpu.smuFrequency" => metrics.SmuFrequencyMhz,
                // 核显那三项：认出是核显之后就从 PM table 里出，
                // 和 CPU 的那几个传感量同一次读、同一条路。
                _ => integratedGpuIndex is { } index
                    ? AmdIntegratedGpuSensorRoute.SensorKindOf(id, index) switch
                    {
                        AmdIntegratedGpuSensorKind.GraphicsClock => metrics.ApuFrequencyMhz,
                        AmdIntegratedGpuSensorKind.Temperature => metrics.ApuTemperatureCelsius,
                        AmdIntegratedGpuSensorKind.CoreVoltage => metrics.ApuVoltageVolts,
                        _ => null
                    }
                    : null
            },
            id => string.Equals(
                id,
                "cpu.frequencyPercent",
                StringComparison.Ordinal)
                ? NativeMetricSnapshotObservationStatus.Unsupported
                : NativeMetricSnapshotObservationStatus.Unavailable);
    }

    private NativeMetricSnapshotSourceCompletion CollectStorageSensors(
        NativeMetricSnapshotCollectionPlan plan,
        NativeMetricSnapshotSourcePlanOutput sourcePlan,
        NativeMetricSnapshotSourceObservationIdentity identity,
        DateTimeOffset capturedAt)
    {
        var sensors = platformSensorReader.ReadStorageSensors();
        var byIndex = sensors.Disks.ToDictionary(
            static disk => disk.Index);
        return CompleteWithValues(
            plan,
            sourcePlan,
            identity,
            capturedAt,
            id => TryIndexedMetricId(
                    id,
                    "disk.",
                    ".temperature",
                    out var index)
                && byIndex.TryGetValue(index, out var sensor)
                    ? sensor.TemperatureCelsius
                    : null);
    }

    private NativeMetricSnapshotSourceCompletion CollectHardwareMonitor(
        NativeMetricSnapshotCollectionPlan plan,
        NativeMetricSnapshotSourcePlanOutput sourcePlan,
        NativeMetricSnapshotSourceObservationIdentity identity,
        DateTimeOffset capturedAt)
    {
        var sensors = platformSensorReader.ReadHardwareMonitorSensors();
        return CompleteWithValues(
            plan,
            sourcePlan,
            identity,
            capturedAt,
            id => id switch
            {
                "memory.temperature" =>
                    sensors.MemoryTemperatureCelsius,
                "system.motherboardTemperature" =>
                    sensors.MotherboardTemperature?.Value,
                "system.vrmTemperature" =>
                    sensors.VrmTemperature?.Value,
                "system.chipsetTemperature" =>
                    sensors.ChipsetTemperature?.Value,
                "system.motherboardVoltage" =>
                    sensors.MotherboardVoltage?.Value,
                "cpu.fanRpm" => sensors.CpuFanSpeedRpm,
                "cpu.fanPercent" => sensors.CpuFanSpeedPercent,
                _ => null
            },
            id => id.StartsWith(
                "gpu.",
                StringComparison.Ordinal)
                ? NativeMetricSnapshotObservationStatus.Unsupported
                : NativeMetricSnapshotObservationStatus.Unavailable);
    }

    private NativeMetricSnapshotSourceCompletion CollectNotebookFans(
        NativeMetricSnapshotCollectionPlan plan,
        NativeMetricSnapshotSourcePlanOutput sourcePlan,
        NativeMetricSnapshotSourceObservationIdentity identity,
        DateTimeOffset capturedAt)
    {
        var sensors = platformSensorReader.ReadNotebookOemFanSensors();
        return CreateNotebookFanCompletion(plan, sourcePlan, identity, capturedAt, sensors);
    }

    internal static NativeMetricSnapshotSourceCompletion CreateNotebookFanCompletion(
        NativeMetricSnapshotCollectionPlan plan,
        NativeMetricSnapshotSourcePlanOutput sourcePlan,
        NativeMetricSnapshotSourceObservationIdentity identity,
        DateTimeOffset capturedAt,
        NotebookOemFanSensorSnapshot sensors)
    {
        var providerStatus = ResolveNotebookOemProviderStatus(
            sensors.ProviderState);
        if (providerStatus == NativeMetricSnapshotSourceStatus.Unsupported)
        {
            return Unsupported(
                sourcePlan.SourceHandle,
                identity,
                capturedAt);
        }
        if (providerStatus == NativeMetricSnapshotSourceStatus.Unavailable)
        {
            return Unavailable(
                sourcePlan.SourceHandle,
                identity,
                capturedAt);
        }

        var dedicatedBindings = plan.Catalog.GpuScopeBindings.Values
            .Where(static binding => binding.SupportsDedicatedMetrics)
            .ToArray();
        var exactGpuDisplayIndex =
            dedicatedBindings.Length == 1
            && sensors.GpuFanSensors.Count == 1
                ? dedicatedBindings[0].DisplayIndex
                : (int?)null;
        var gpuFan = exactGpuDisplayIndex is null
            ? null
            : sensors.GpuFanSensors[0];
        return CompleteWithValues(
            plan,
            sourcePlan,
            identity,
            capturedAt,
            id => ResolveNotebookOemFanValue(
                id,
                sensors,
                exactGpuDisplayIndex,
                gpuFan),
            id => id is "cpu.fanRpm" or "cpu.fanPercent"
                || TryGpuMetricId(id, out var index, out var name)
                    && exactGpuDisplayIndex == index
                    && name is "fanRpm" or "fanPercent"
                ? NativeMetricSnapshotObservationStatus.Unavailable
                : NativeMetricSnapshotObservationStatus.Unsupported);
    }

    internal static NativeMetricSnapshotSourceStatus
        ResolveNotebookOemProviderStatus(
            HardwareSensorProviderState providerState)
    {
        ArgumentNullException.ThrowIfNull(providerState);
        return providerState.State switch
        {
            "Active" => NativeMetricSnapshotSourceStatus.Complete,
            "UnsupportedFirmware" =>
                NativeMetricSnapshotSourceStatus.Unsupported,
            _ => NativeMetricSnapshotSourceStatus.Unavailable
        };
    }

    internal static double? ResolveNotebookOemFanValue(
        string metricId,
        NotebookOemFanSensorSnapshot sensors,
        int? exactGpuDisplayIndex,
        PlatformGpuSensor? gpuFan)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(metricId);
        ArgumentNullException.ThrowIfNull(sensors);
        if (metricId == "cpu.fanRpm")
        {
            return sensors.CpuFanSpeedRpm;
        }
        if (metricId == "cpu.fanPercent")
        {
            return sensors.CpuFanSpeedPercent;
        }
        if (!TryGpuMetricId(
                metricId,
                out var displayIndex,
                out var metricName)
            || exactGpuDisplayIndex != displayIndex
            || gpuFan is null)
        {
            return null;
        }
        return metricName switch
        {
            "fanRpm" => gpuFan.FanSpeedRpm,
            "fanPercent" => gpuFan.FanSpeedPercent,
            _ => null
        };
    }

    private NativeMetricSnapshotSourceCompletion CollectSystemIo(
        NativeMetricSnapshotCollectionPlan plan,
        NativeMetricSnapshotSourcePlanOutput sourcePlan,
        NativeMetricSnapshotSourceObservationIdentity identity,
        DateTimeOffset capturedAt)
    {
        var selected = SelectedMetricIds(plan, sourcePlan.SourceHandle);
        var includeDisk = selected.Any(static id => id.StartsWith(
            "disk.",
            StringComparison.Ordinal));
        var includeNetwork = selected.Any(static id => id.StartsWith(
            "network.",
            StringComparison.Ordinal));
        var metrics = systemIoZone.Read(new PdhSystemIoReadRequest(
            includeDisk,
            includeNetwork));
        var diskCurrent = includeDisk
            && metrics.DiskAvailability
                == NativePdhProviderAvailability.Available
            && metrics.DiskObservedAt is not null;
        var networkCurrent = includeNetwork
            && metrics.NetworkAvailability
                == NativePdhProviderAvailability.Available
            && metrics.NetworkObservedAt is not null;
        if (!diskCurrent && !networkCurrent)
        {
            return Unavailable(
                sourcePlan.SourceHandle,
                identity,
                capturedAt);
        }

        var sourceObservedAt = new[]
            {
                diskCurrent ? metrics.DiskObservedAt : null,
                networkCurrent ? metrics.NetworkObservedAt : null
            }
            .Where(static value => value is not null)
            .Max()!.Value;
        var observations = MetricsForSource(
                plan,
                sourcePlan.SourceHandle)
            .Select(metric =>
            {
                var binding = plan.Catalog.RuleBindings[metric.RuleHandle];
                var isDisk = binding.MetricId.StartsWith(
                    "disk.",
                    StringComparison.Ordinal);
                var domainCurrent = isDisk
                    ? diskCurrent
                    : networkCurrent;
                var observedAt = isDisk
                    ? metrics.DiskObservedAt ?? sourceObservedAt
                    : metrics.NetworkObservedAt ?? sourceObservedAt;
                double? value = !domainCurrent
                    ? null
                    : binding.MetricId switch
                    {
                        "disk.total.activePercent" =>
                            metrics.Disk.ActivePercent,
                        "disk.total.readBytesPerSec" =>
                            metrics.Disk.ReadBytesPerSecond,
                        "disk.total.writeBytesPerSec" =>
                            metrics.Disk.WriteBytesPerSecond,
                        "disk.total.queueLength" =>
                            metrics.Disk.QueueLength,
                        "network.total.receiveBytesPerSec" =>
                            metrics.Network.ReceiveBytesPerSecond,
                        "network.total.sendBytesPerSec" =>
                            metrics.Network.SendBytesPerSecond,
                        "network.total.utilizationPercent" =>
                            metrics.Network.UtilizationPercent,
                        _ => null
                    };
                return NativeMetricSnapshotObservationProjection
                    .CreateObservation(
                        metric,
                        binding,
                        identity,
                        observedAt,
                        value);
            })
            .ToImmutableArray();
        return Complete(
            sourcePlan.SourceHandle,
            identity,
            sourceObservedAt,
            observations);
    }

    private WindowsGpuAdapterInventoryRead RequireCurrentGpuInventory(
        NativeMetricSnapshotCatalogProjection catalog)
    {
        var inventory = gpuAdapterOrderZone.ReadInventory(requested: true);
        if (inventory.Status != SamplingObservationStatus.Current
            || inventory.Generation == 0
            || inventory.ObservedCount != inventory.Adapters.Count
            || inventory.SkippedCount != 0
            || inventory.OverflowCount != 0)
        {
            throw new InvalidOperationException(
                "The exact Windows GPU inventory is unavailable.");
        }
        if (!MatchesExactGpuInventory(catalog, inventory.Adapters))
        {
            metricSnapshotOwner.RequestTopologyRefresh();
            throw new InvalidOperationException(
                "The Windows GPU topology changed after the native catalog was published.");
        }
        return inventory;
    }

    internal static bool MatchesExactGpuInventory(
        NativeMetricSnapshotCatalogProjection catalog,
        IReadOnlyList<WindowsGpuAdapter> adapters)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(adapters);
        var expected = catalog.GpuScopeBindings.Values
            .Select(static binding => binding.AdapterLuid)
            .Order()
            .ToArray();
        var observed = adapters
            .Where(static adapter => !adapter.IsSoftware)
            .Select(static adapter =>
                NativePdhAdapterIdentity.Pack(adapter.Luid))
            .Order()
            .ToArray();
        return expected.SequenceEqual(observed);
    }

    private static NativeMetricSnapshotSourceCompletion
        CompleteWithValues(
            NativeMetricSnapshotCollectionPlan plan,
            NativeMetricSnapshotSourcePlanOutput sourcePlan,
            NativeMetricSnapshotSourceObservationIdentity identity,
            DateTimeOffset capturedAt,
            Func<string, double?> value,
            NativeMetricSnapshotObservationStatus unavailableStatus =
                NativeMetricSnapshotObservationStatus.Unavailable)
        => CompleteWithValues(
            plan,
            sourcePlan,
            identity,
            capturedAt,
            value,
            _ => unavailableStatus);

    private static NativeMetricSnapshotSourceCompletion
        CompleteWithValues(
            NativeMetricSnapshotCollectionPlan plan,
            NativeMetricSnapshotSourcePlanOutput sourcePlan,
            NativeMetricSnapshotSourceObservationIdentity identity,
            DateTimeOffset capturedAt,
            Func<string, double?> value,
            Func<string, NativeMetricSnapshotObservationStatus>
                unavailableStatus)
    {
        var observations = MetricsForSource(
                plan,
                sourcePlan.SourceHandle)
            .Select(metric =>
            {
                var binding =
                    plan.Catalog.RuleBindings[metric.RuleHandle];
                return NativeMetricSnapshotObservationProjection
                    .CreateObservation(
                        metric,
                        binding,
                        identity,
                        capturedAt,
                        value(binding.MetricId),
                        unavailableStatus(binding.MetricId));
            })
            .ToImmutableArray();
        return Complete(
            sourcePlan.SourceHandle,
            identity,
            capturedAt,
            observations);
    }

    private static NativeMetricSnapshotSourceCompletion Complete(
        ulong sourceHandle,
        NativeMetricSnapshotSourceObservationIdentity identity,
        DateTimeOffset capturedAt,
        ImmutableArray<NativeMetricSnapshotObservationInput> observations,
        NativeMetricSnapshotCpuCounterInput? counter = null,
        ImmutableArray<NativeMetricSnapshotGpuInventoryInput> gpuInventory =
            default,
        uint expectedGpuInventoryCount = 0)
        => new(
            sourceHandle,
            identity.SourceIncarnation,
            identity.SourceObservationSequence,
            capturedAt,
            NativeMetricSnapshotSourceStatus.Complete,
            observations.IsDefault ? [] : observations,
            counter,
            gpuInventory.IsDefault ? [] : gpuInventory,
            expectedGpuInventoryCount,
            0);

    private static NativeMetricSnapshotSourceCompletion Unavailable(
        ulong sourceHandle,
        NativeMetricSnapshotSourceObservationIdentity identity,
        DateTimeOffset capturedAt)
        => new(
            sourceHandle,
            identity.SourceIncarnation,
            identity.SourceObservationSequence,
            capturedAt,
            NativeMetricSnapshotSourceStatus.Unavailable,
            [],
            null,
            [],
            0,
            0);

    private static NativeMetricSnapshotSourceCompletion Unsupported(
        ulong sourceHandle,
        NativeMetricSnapshotSourceObservationIdentity identity,
        DateTimeOffset capturedAt)
        => new(
            sourceHandle,
            identity.SourceIncarnation,
            identity.SourceObservationSequence,
            capturedAt,
            NativeMetricSnapshotSourceStatus.Unsupported,
            [],
            null,
            [],
            0,
            0);

    private static NativeMetricSnapshotMetricPlanOutput
        AssertSingleMetric(
            NativeMetricSnapshotCollectionPlan plan,
            ulong sourceHandle)
        => MetricsForSource(plan, sourceHandle).Single();

    private static NativeMetricSnapshotMetricPlanOutput[]
        MetricsForSource(
            NativeMetricSnapshotCollectionPlan plan,
            ulong sourceHandle)
        => plan.Metrics
            .Where(metric =>
                metric.SourceHandle == sourceHandle
                && (metric.Flags
                    & (uint)NativeMetricSnapshotMetricPlanFlags.Selected)
                    != 0)
            .OrderBy(static metric => metric.RuleHandle)
            .ToArray();

    private static string[] SelectedMetricIds(
        NativeMetricSnapshotCollectionPlan plan,
        ulong sourceHandle)
        => MetricsForSource(plan, sourceHandle)
            .Select(metric =>
                plan.Catalog.RuleBindings[metric.RuleHandle].MetricId)
            .ToArray();

    private static IReadOnlySet<int> GpuIndexes(
        IEnumerable<string> metricIds)
        => metricIds
            .Select(id => TryGpuMetricId(
                id,
                out var index,
                out _)
                ? index
                : -1)
            .Where(static index => index >= 0)
            .ToHashSet();

    private static bool Includes(
        IEnumerable<string> metricIds,
        string metricName)
        => metricIds.Any(id => TryGpuMetricId(
            id,
            out _,
            out var name)
            && string.Equals(
                name,
                metricName,
                StringComparison.Ordinal));

    private static bool IncludesExact(
        IEnumerable<string> metricIds,
        string metricId)
        => metricIds.Contains(metricId, StringComparer.Ordinal);

    private static double? ResolveGpuValue(
        string metricId,
        IReadOnlyDictionary<int, GpuMetrics> byIndex)
    {
        if (!TryGpuMetricId(
                metricId,
                out var index,
                out var name)
            || !byIndex.TryGetValue(index, out var gpu))
        {
            return null;
        }
        return name switch
        {
            "usage" when gpu.IsUsageAvailable => gpu.UsagePercent,
            "graphicsClock" when gpu.GraphicsFrequencyMhz > 0 =>
                gpu.GraphicsFrequencyMhz,
            "graphicsClockPercent"
                when gpu.GraphicsFrequencyPercent > 0 =>
                gpu.GraphicsFrequencyPercent,
            "vram" when gpu.TotalMemoryBytes > 0 =>
                gpu.UsedMemoryBytes,
            "vramTotal" when gpu.TotalMemoryBytes > 0 =>
                gpu.TotalMemoryBytes,
            "vramPercent" when gpu.TotalMemoryBytes > 0 =>
                gpu.MemoryUsagePercent,
            "memoryClock" when gpu.MemoryFrequencyMhz > 0 =>
                gpu.MemoryFrequencyMhz,
            "power" => gpu.Sensors.PowerWatts,
            "powerLimit" => gpu.Sensors.PowerLimitWatts,
            "boardPower" => gpu.Sensors.BoardPowerWatts,
            "temperature" => gpu.Sensors.TemperatureCelsius,
            "hotspotTemperature" =>
                gpu.Sensors.HotspotTemperatureCelsius,
            "intakeTemperature" =>
                gpu.Sensors.IntakeTemperatureCelsius,
            "fanPercent" => gpu.Sensors.FanSpeedPercent,
            "fanRpm" => gpu.Sensors.FanSpeedRpm,
            "coreVoltage" => gpu.Sensors.CoreVoltageVolts,
            "current" => gpu.Sensors.CurrentAmps,
            _ => null
        };
    }

    private static bool TryGpuMetricId(
        string id,
        out int index,
        out string metricName)
    {
        index = -1;
        metricName = string.Empty;
        if (!id.StartsWith("gpu.", StringComparison.Ordinal))
        {
            return false;
        }
        var separator = id.IndexOf('.', 4);
        if (separator <= 4
            || !int.TryParse(id.AsSpan(4, separator - 4), out index))
        {
            return false;
        }
        metricName = id[(separator + 1)..];
        return metricName.Length > 0;
    }

    private static bool TryIndexedMetricId(
        string id,
        string prefix,
        string suffix,
        out int index)
    {
        index = -1;
        return id.StartsWith(prefix, StringComparison.Ordinal)
            && id.EndsWith(suffix, StringComparison.Ordinal)
            && int.TryParse(
                id.AsSpan(
                    prefix.Length,
                    id.Length - prefix.Length - suffix.Length),
                out index);
    }
}
