using System.Collections.Immutable;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite.Native;
using ResourceManager.ReadWrite.Native.Pdh;
using ResourceManager.ReadWrite.Windows.Pdh;
using ResourceManager.ReadWrite.Windows.Gpu;
using ResourceManager.ReadWrite.Windows.Platform;
using ResourceManager.ReadWrite.Windows.Devices;
using ResourceManager.ReadWrite.Windows.Devices.Models;
using ResourceManager.ReadWrite.Windows.Devices.Battery;
using ResourceManager.ReadWrite.Windows.Cpu;
using ResourceManager.ReadWrite.Windows.Cpu.Models;
using ResourceManager.ReadWrite.Vendors.Nvidia;
using ResourceManager.ReadWrite.Vendors.Amd;
using ResourceManager.ReadWrite.Vendors.Amd.Smu;
using ResourceManager.ReadWrite.Vendors.Intel;
using ResourceManager.ReadWrite.Vendors.Intel.Msr;
using ResourceManager.ReadWrite.Windows.Metrics.Resources;
namespace ResourceManager.ReadWrite.Windows.Metrics.Capture;

internal sealed partial class WindowsHardwareMetricCapture
{
    private async Task<NativeMetricSnapshotSourceCompletion>
        CollectNativeSourceAsync(
            NativeMetricSnapshotCollectionPlan plan,
            NativeMetricSnapshotSourcePlanOutput sourcePlan,
            CancellationToken cancellationToken,
            NativeMetricSnapshotCatalogTopologySnapshot? topology)
    {
        var sourceId = plan.Catalog.SourceIds[sourcePlan.SourceHandle];
        var identity =
            sourceRuntimeFacts.BeginObservation(sourcePlan.SourceHandle);
        var capturedAt = DateTimeOffset.UtcNow;
        try
        {
            if (topology?.GpuStatus == NativeMetricSnapshotCatalogTopologyStatus.Unavailable
                && sourceId == NativeMetricSnapshotSources.WindowsGpuAdapterOrder)
                return Unavailable(sourcePlan.SourceHandle, identity, capturedAt);
            var completion = sourceId switch
            {
                NativeMetricSnapshotSources.WindowsCpu =>
                    CollectCpu(plan, sourcePlan, identity),
                NativeMetricSnapshotSources.WindowsMemory =>
                    CollectMemory(plan, sourcePlan, identity, capturedAt),
                NativeMetricSnapshotSources.WindowsVirtualMemory =>
                    CollectVirtualMemory(
                        plan,
                        sourcePlan,
                        identity,
                        capturedAt),
                NativeMetricSnapshotSources.WindowsGpuAdapterOrder =>
                    CollectGpuInventory(
                        plan,
                        sourcePlan,
                        identity,
                        capturedAt),
                NativeMetricSnapshotSources.PdhCpuFrequency =>
                    CollectCpuFrequency(
                        plan,
                        sourcePlan,
                        identity,
                        capturedAt),
                NativeMetricSnapshotSources.PdhGpuEngine =>
                    CollectPdhGpu(
                        plan,
                        sourcePlan,
                        identity,
                        capturedAt),
                NativeMetricSnapshotSources.NvidiaNvml =>
                    await CollectNvmlAsync(
                        plan,
                        sourcePlan,
                        identity,
                        capturedAt,
                        cancellationToken),
                NativeMetricSnapshotSources.NvidiaNvapi =>
                    await CollectNvapiAsync(
                        plan,
                        sourcePlan,
                        identity,
                        capturedAt,
                        cancellationToken),
                NativeMetricSnapshotSources.AmdAdlx =>
                    await CollectAdlxAsync(
                        plan,
                        sourcePlan,
                        identity,
                        capturedAt,
                        cancellationToken),
                NativeMetricSnapshotSources.WindowsNetworkInterface =>
                    CollectNetworkInterfaces(plan, sourcePlan, identity, capturedAt),
                NativeMetricSnapshotSources.IntelGcl =>
                    CollectIntelGpu(plan, sourcePlan, identity, capturedAt, cancellationToken),
                NativeMetricSnapshotSources.WindowsNpuEngine =>
                    CollectNpuEngine(plan, sourcePlan, identity, capturedAt, topology, cancellationToken),
                NativeMetricSnapshotSources.WindowsNpuMemory =>
                    CollectNpuMemory(plan, sourcePlan, identity, capturedAt, topology, cancellationToken),
                NativeMetricSnapshotSources.IntelMsr =>
                    CollectIntelMsr(plan, sourcePlan, identity, capturedAt, topology),
                NativeMetricSnapshotSources.AmdSmu =>
                    CollectAmdSmu(
                        plan,
                        sourcePlan,
                        identity,
                        capturedAt),
                NativeMetricSnapshotSources.AmdSmn =>
                    CollectAmdCcd(plan, sourcePlan, identity, capturedAt, topology),
                NativeMetricSnapshotSources.WindowsStorageSensors =>
                    CollectStorageSensors(
                        plan,
                        sourcePlan,
                        identity,
                        capturedAt),
                NativeMetricSnapshotSources.WindowsNvmeHealth =>
                    CollectNvmeHealth(plan, sourcePlan, identity, capturedAt),
                NativeMetricSnapshotSources.WindowsAtaSmart =>
                    CollectAtaSmart(plan, sourcePlan, identity, capturedAt),
                NativeMetricSnapshotSources.WindowsDiskPerformance =>
                    await CollectDiskPerformanceAsync(plan, sourcePlan, identity, cancellationToken),
                NativeMetricSnapshotSources.WindowsBattery =>
                    CollectBattery(plan, sourcePlan, identity, capturedAt),
                NativeMetricSnapshotSources.HardwareMonitorWmi =>
                    CollectHardwareMonitor(
                        plan,
                        sourcePlan,
                        identity,
                        capturedAt),
                NativeMetricSnapshotSources.NotebookOemFan =>
                    CollectNotebookFans(
                        plan,
                        sourcePlan,
                        identity,
                        capturedAt),
                NativeMetricSnapshotSources.PdhSystemIo =>
                    CollectSystemIo(
                        plan,
                        sourcePlan,
                        identity,
                        capturedAt),
                _ => throw new InvalidOperationException(
                    $"Unknown native metric source '{sourceId}'.")
            };
            if (topology?.GpuStatus == NativeMetricSnapshotCatalogTopologyStatus.Unavailable
                && completion.Status == NativeMetricSnapshotSourceStatus.Complete)
            {
                // A shared source can also contain healthy CPU/fan values. Empty only GPU observations.
                var gpuMetrics = MetricsForSource(plan, sourcePlan.SourceHandle)
                    .Where(metric => plan.Catalog.RuleBindings[metric.RuleHandle].Field?.Resource == MetricResource.Gpu)
                    .ToArray();
                if (gpuMetrics.Length != 0)
                    completion = completion with
                    {
                        Observations = completion.Observations
                            .Where(observation => plan.Catalog.RuleBindings[observation.RuleHandle].Field?.Resource != MetricResource.Gpu)
                            .Concat(gpuMetrics.Select(metric => NativeMetricSnapshotObservationProjection.CreateObservation(
                                metric, plan.Catalog.RuleBindings[metric.RuleHandle], identity, capturedAt, null)))
                            .ToImmutableArray(),
                        PdhGpuEngineUsage = null
                    };
            }
            return completion;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logFailure?.Invoke(ex, sourceId);
            var unavailable = Unavailable(
                sourcePlan.SourceHandle,
                identity,
                capturedAt);
            return sourceId == NativeMetricSnapshotSources.PdhGpuEngine
                ? unavailable with
                {
                    PdhGpuEngineUsage = new PdhGpuEngineUsageRead(
                        new Dictionary<int, double>(),
                        NativePdhProviderAvailability.Unavailable,
                        null)
                }
                : unavailable;
        }
    }

    private NativeMetricSnapshotSourceCompletion CollectCpu(
        NativeMetricSnapshotCollectionPlan plan,
        NativeMetricSnapshotSourcePlanOutput sourcePlan,
        NativeMetricSnapshotSourceObservationIdentity identity)
    {
        var counter = resources.ReadCpuCounters();
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
                new(counter.IsAvailable, counter.IdleTicks, counter.KernelTicks, counter.UserTicks,
                    counter.MonotonicTicks, counter.MonotonicTicksPerSecond, counter.ObservedAt));
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
        var selected = SelectedBindings(plan, sourcePlan.SourceHandle).ToArray();
        var metrics = selected.Any(static binding => binding.FieldCode is MetricFieldCode.MemoryUsed
            or MetricFieldCode.MemoryTotal or MetricFieldCode.MemoryPercent or MetricFieldCode.MemoryAvailable)
            ? resources.ReadMemory() : null;
        var requestedNodes = selected.Where(static binding => binding.FieldCode == MetricFieldCode.NumaNodeAvailableBytes)
            .Select(static binding => binding.DeviceIdentity).ToHashSet(StringComparer.Ordinal);
        var values = resources.ReadNumaAvailable(plan.Catalog.NumaIdentities, requestedNodes);
        var requestedCapacityNodes = selected.Where(static binding => binding.FieldCode == MetricFieldCode.NumaNodeCapacityMiB)
            .Select(static binding => binding.DeviceIdentity).ToHashSet(StringComparer.Ordinal);
        var capacityValues = resources.ReadNumaCapacityMiB(plan.Catalog.NumaIdentities, requestedCapacityNodes);
        return CreateMemoryCompletion(plan, sourcePlan, identity, capturedAt, metrics, values, capacityValues);
    }

    private NativeMetricSnapshotSourceCompletion CollectVirtualMemory(
        NativeMetricSnapshotCollectionPlan plan,
        NativeMetricSnapshotSourcePlanOutput sourcePlan,
        NativeMetricSnapshotSourceObservationIdentity identity,
        DateTimeOffset capturedAt)
    {
        var metrics = resources.ReadVirtualMemory();
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
            id => id.FieldCode switch
            {
                MetricFieldCode.CommitUsed => metrics.UsedBytes,
                MetricFieldCode.CommitLimit => metrics.TotalBytes,
                MetricFieldCode.CommitPercent => metrics.UsagePercent,
                MetricFieldCode.CommitPeak => metrics.CommitPeakBytes,
                MetricFieldCode.SystemCache => metrics.SystemCacheBytes,
                MetricFieldCode.KernelPaged => metrics.KernelPagedBytes,
                MetricFieldCode.KernelNonpaged => metrics.KernelNonpagedBytes,
                MetricFieldCode.ProcessCount => metrics.ProcessCount,
                MetricFieldCode.ThreadCount => metrics.ThreadCount,
                MetricFieldCode.HandleCount => metrics.HandleCount,
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
        var frequency = resources.ReadCpuFrequency();
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
            id => id.FieldCode switch
            {
                MetricFieldCode.CpuFrequency => frequency.CurrentMhz,
                MetricFieldCode.CpuFrequencyPercent => frequency.Percent,
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
            resources.ReadGpuUsage(
                inventory.Adapters);
        if (usage.ProviderAvailability
                != NativePdhProviderAvailability.Available
            || usage.ObservedAt is not { } observedAt)
        {
            return Unavailable(
                sourcePlan.SourceHandle,
                identity,
                capturedAt) with { PdhGpuEngineUsage = usage };
        }
        return CompleteWithValues(
            plan,
            sourcePlan,
            identity,
            observedAt,
            id => id.FieldCode == MetricFieldCode.GpuUsage
                && usage.UsageByAdapterIndex.TryGetValue(id.DisplayIndex, out var value)
                    ? value : null) with { PdhGpuEngineUsage = usage };
    }

    private async Task<NativeMetricSnapshotSourceCompletion>
        CollectNvmlAsync(
            NativeMetricSnapshotCollectionPlan plan,
            NativeMetricSnapshotSourcePlanOutput sourcePlan,
            NativeMetricSnapshotSourceObservationIdentity identity,
            DateTimeOffset capturedAt,
            CancellationToken cancellationToken)
    {
        var selected = SelectedBindings(plan, sourcePlan.SourceHandle);
        var gpuSelected = selected.Where(static binding => binding.NvmlFan is null).ToArray();
        var indexes = GpuIndexes(gpuSelected);
        var request = new NvidiaNvmlReadRequest(
            indexes,
            Includes(selected, MetricFieldCode.GpuUsage),
            Includes(selected, MetricFieldCode.GpuGraphicsClock)
                || Includes(selected, MetricFieldCode.GpuGraphicsClockPercent),
            Includes(selected, MetricFieldCode.GpuMemoryUsed)
                || Includes(selected, MetricFieldCode.GpuMemoryPercent)
                || Includes(selected, MetricFieldCode.GpuMemoryTotal),
            Includes(selected, MetricFieldCode.GpuMemoryClock),
            Includes(selected, MetricFieldCode.GpuPower),
            Includes(selected, MetricFieldCode.GpuPowerLimit),
            Includes(selected, MetricFieldCode.GpuTemperature),
            Includes(selected, MetricFieldCode.GpuFanPercent),
            IncludeElectricalState: false)
        {
            AdditionalMetrics = selected.Where(static binding => NvidiaNvmlReader.IsAdditionalMetric(binding.FieldCode))
                .Select(static binding => binding.FieldCode).ToHashSet()
        };
        var inventory = RequireCurrentGpuInventory(plan.Catalog);
        IReadOnlyList<GpuMetrics> values = [];
        if (indexes.Count != 0)
            values = await resources.ReadNvmlAsync(request, inventory, cancellationToken);
        var fanTargets = selected.Where(static binding => binding.NvmlFan is not null)
            .GroupBy(static binding => binding.NvmlFan!.ExactIdentityKey, StringComparer.Ordinal)
            .Select(static group => new NvidiaNvmlFanReadTarget(group.First().NvmlFan!,
                group.Any(static binding => binding.FieldCode == MetricFieldCode.NvmlFanSpeedPercentOfMax),
                group.Any(static binding => binding.FieldCode == MetricFieldCode.NvmlFanSpeedRpm))).ToArray();
        IReadOnlyList<NvidiaNvmlFanReading> fans = [];
        if (fanTargets.Length != 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            fans = await resources.ReadNvmlFansAsync(fanTargets, inventory, cancellationToken);
        }
        return CreateNvmlCompletion(plan, sourcePlan, identity, capturedAt, values, fans);
    }

    internal static NativeMetricSnapshotSourceCompletion CreateNvmlCompletion(
        NativeMetricSnapshotCollectionPlan plan,
        NativeMetricSnapshotSourcePlanOutput sourcePlan,
        NativeMetricSnapshotSourceObservationIdentity identity,
        DateTimeOffset capturedAt,
        IReadOnlyList<GpuMetrics> values,
        IReadOnlyList<NvidiaNvmlFanReading>? fans = null)
    {
        if (values.Count == 0 && fans is not { Count: > 0 })
        {
            return Unavailable(
                sourcePlan.SourceHandle,
                identity,
                capturedAt);
        }
        var byIndex = values.ToDictionary(static gpu => gpu.Index);
        var byFan = (fans ?? []).ToDictionary(static reading => reading.Fan.ExactIdentityKey, StringComparer.Ordinal);
        return CompleteWithValues(
            plan,
            sourcePlan,
            identity,
            capturedAt,
            id => id.NvmlFan is { } fan
                ? byFan.TryGetValue(fan.ExactIdentityKey, out var reading) ? id.FieldCode switch
                    {
                        MetricFieldCode.NvmlFanSpeedPercentOfMax => reading.PercentOfMaxNoiseTolerance,
                        MetricFieldCode.NvmlFanSpeedRpm => reading.IntendedRpm,
                        _ => null
                    } : null
                : ResolveGpuValue(id, byIndex),
            unsignedValue: binding => binding.NvmlFan is null && byIndex.TryGetValue(binding.DisplayIndex, out var gpu)
                && gpu.AdditionalIntegerReadings.TryGetValue(binding.FieldCode, out var raw) ? raw : null,
            observedAt: binding => binding.NvmlFan is null && byIndex.TryGetValue(binding.DisplayIndex, out var gpu)
                && gpu.AdditionalObservedAt.TryGetValue(binding.FieldCode, out var timestamp) ? timestamp : null);
    }

    private async Task<NativeMetricSnapshotSourceCompletion>
        CollectNvapiAsync(
            NativeMetricSnapshotCollectionPlan plan,
            NativeMetricSnapshotSourcePlanOutput sourcePlan,
            NativeMetricSnapshotSourceObservationIdentity identity,
            DateTimeOffset capturedAt,
            CancellationToken cancellationToken)
    {
        var selected = SelectedBindings(plan, sourcePlan.SourceHandle);
        var request = new NvidiaNvapiReadRequest(
            GpuIndexes(selected),
            Includes(selected, MetricFieldCode.GpuFanRpm),
            Includes(selected, MetricFieldCode.GpuVoltage),
            Includes(selected, MetricFieldCode.GpuCurrent));
        var values = await resources.ReadNvapiAsync(
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
                if (!byIndex.TryGetValue(id.DisplayIndex, out var sensor))
                {
                    return null;
                }
                return id.FieldCode switch
                {
                    MetricFieldCode.GpuFanRpm => sensor.Sensors.FanSpeedRpm,
                    MetricFieldCode.GpuVoltage =>
                        sensor.Sensors.CoreVoltageVolts,
                    MetricFieldCode.GpuCurrent => sensor.Sensors.CurrentAmps,
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
        var selected = SelectedBindings(plan, sourcePlan.SourceHandle);
        var request = CreateAdlxReadRequest(selected);
        var values = await resources.ReadAdlxAsync(
            request,
            RequireCurrentGpuInventory(plan.Catalog),
            cancellationToken);
        return CreateAdlxCompletion(plan, sourcePlan, identity, capturedAt, values);
    }

    internal static NativeMetricSnapshotSourceCompletion CreateAdlxCompletion(NativeMetricSnapshotCollectionPlan plan,
        NativeMetricSnapshotSourcePlanOutput sourcePlan, NativeMetricSnapshotSourceObservationIdentity identity,
        DateTimeOffset capturedAt, IReadOnlyList<GpuMetrics> values)
    {
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
            id => ResolveGpuValue(id, byIndex),
            unsignedValue: binding => byIndex.TryGetValue(binding.DisplayIndex, out var gpu)
                && gpu.AdditionalIntegerReadings.TryGetValue(binding.FieldCode, out var raw) ? raw : null);
    }

    internal static AmdAdlxReadRequest CreateAdlxReadRequest(IReadOnlyList<NativeMetricSnapshotRuleCatalogBinding> selected)
        => CreateAdlxDeviceReadRequest(selected) with { NativeFlagsByDisplayIndex = selected.GroupBy(static binding => binding.DisplayIndex)
            .ToDictionary(static group => group.Key, group => AmdAdlxBridgeReader.BuildNativeFlags(CreateAdlxDeviceReadRequest(group.ToArray()))) };

    private static AmdAdlxReadRequest CreateAdlxDeviceReadRequest(IReadOnlyList<NativeMetricSnapshotRuleCatalogBinding> selected)
        => new(
            GpuIndexes(selected),
            Includes(selected, MetricFieldCode.GpuUsage) || Includes(selected, MetricFieldCode.AdlxActivity),
            Includes(selected, MetricFieldCode.GpuGraphicsClock)
                || Includes(selected, MetricFieldCode.GpuGraphicsClockPercent),
            Includes(selected, MetricFieldCode.GpuMemoryUsed)
                || Includes(selected, MetricFieldCode.GpuMemoryPercent)
                || Includes(selected, MetricFieldCode.GpuMemoryTotal)
                || Includes(selected, MetricFieldCode.AdlxDedicatedMemoryMb),
            Includes(selected, MetricFieldCode.GpuMemoryClock),
            Includes(selected, MetricFieldCode.GpuPower),
            Includes(selected, MetricFieldCode.GpuBoardPower),
            Includes(selected, MetricFieldCode.GpuTemperature),
            Includes(selected, MetricFieldCode.GpuHotspotTemperature),
            Includes(selected, MetricFieldCode.GpuIntakeTemperature),
            Includes(selected, MetricFieldCode.GpuFanRpm),
            Includes(selected, MetricFieldCode.GpuVoltage) || Includes(selected, MetricFieldCode.AdlxVoltage),
            Includes(selected, MetricFieldCode.GpuMemoryTemperature),
            Includes(selected, MetricFieldCode.AdlxSampleTimestamp),
            Includes(selected, MetricFieldCode.AdlxNpuActivityPercent),
            Includes(selected, MetricFieldCode.AdlxNpuFrequencyMhz));

    private NativeMetricSnapshotSourceCompletion CollectAmdSmu(
        NativeMetricSnapshotCollectionPlan plan,
        NativeMetricSnapshotSourcePlanOutput sourcePlan,
        NativeMetricSnapshotSourceObservationIdentity identity,
        DateTimeOffset capturedAt)
    {
        var selected = SelectedBindings(plan, sourcePlan.SourceHandle);

        // 核显的频率/温度/电压也从这里出。核显没有自己的传感库（ADLX 服务的是独显），
        // 但那几个量一直躺在处理器的 PM table 里 —— 因为核显就是这颗处理器的一部分。
        // 哪一块是核显，由启动时认定的路由回答，不按索引猜。
        // CPU readings do not depend on GPU enumeration. A routing failure
        // empties only the requested GPU fields from this shared SMU source.
        WindowsGpuAdapterInventoryRead? gpuInventory = null;
        if (selected.Any(binding => binding.Field?.Resource == MetricResource.Gpu))
        {
            try { gpuInventory = resources.ReadGpuInventory(); }
            catch (Exception error) when (error is not (OperationCanceledException or OutOfMemoryException))
            {
                logFailure?.Invoke(error, NativeMetricSnapshotSources.WindowsGpuAdapterOrder);
            }
        }
        var gpuInventoryIsCurrent = gpuInventory is not null && gpuInventory.Status == SamplingObservationStatus.Current
            && gpuInventory.Generation != 0
            && gpuInventory.ObservedCount == gpuInventory.Adapters.Count
            && gpuInventory.SkippedCount == 0
            && gpuInventory.OverflowCount == 0;
        IReadOnlyList<WindowsGpuAdapter> routeAdapters = gpuInventoryIsCurrent
            ? gpuInventory!.Adapters
            : Array.Empty<WindowsGpuAdapter>();
        var integratedGpuIndex = integratedGpuSensorRoute.Resolve(routeAdapters);
        if (selected.Length > 0 && selected.All(binding => binding.Field?.Resource == MetricResource.Gpu
                && binding.DisplayIndex != integratedGpuIndex))
            return CreateUnroutedAmdSmuCompletion(plan, sourcePlan, identity, capturedAt,
                integratedGpuIndex, gpuInventoryIsCurrent);
        bool Integrated(MetricFieldCode code) => integratedGpuIndex is { } index
            && selected.Any(binding => binding.DisplayIndex == index && binding.FieldCode == code);

        var metrics = resources.ReadAmdSmu(new AmdSmuCpuSensorReadRequest(
            Includes(selected, MetricFieldCode.CpuPackagePower),
            Includes(selected, MetricFieldCode.CpuCoreVoltage),
            Includes(selected, MetricFieldCode.CpuPackageCurrent),
            Includes(selected, MetricFieldCode.CpuTemperature),
            Includes(selected, MetricFieldCode.CpuStapmPower),
            Includes(selected, MetricFieldCode.CpuActualPower),
            Includes(selected, MetricFieldCode.CpuAveragePower),
            Includes(selected, MetricFieldCode.CpuTdcCurrent),
            Includes(selected, MetricFieldCode.CpuEdcCurrent),
            Includes(selected, MetricFieldCode.CpuSocPower),
            Includes(selected, MetricFieldCode.CpuSocVoltage),
            Includes(selected, MetricFieldCode.CpuIgpuFrequency)
                || Integrated(MetricFieldCode.GpuGraphicsClock),
            Includes(selected, MetricFieldCode.CpuIgpuVoltage)
                || Integrated(MetricFieldCode.GpuVoltage),
            Includes(selected, MetricFieldCode.CpuIgpuTemperature)
                || Integrated(MetricFieldCode.GpuTemperature),
            Includes(selected, MetricFieldCode.CpuFrequency)
                || Includes(selected, MetricFieldCode.CpuSmuFrequency)));
        return CreateAmdSmuCompletion(
            plan,
            sourcePlan,
            identity,
            capturedAt,
            metrics,
            integratedGpuIndex,
            gpuInventoryIsCurrent);
    }

    internal static NativeMetricSnapshotSourceCompletion CreateUnroutedAmdSmuCompletion(
        NativeMetricSnapshotCollectionPlan plan, NativeMetricSnapshotSourcePlanOutput sourcePlan,
        NativeMetricSnapshotSourceObservationIdentity identity, DateTimeOffset capturedAt,
        int? integratedGpuIndex, bool gpuInventoryIsCurrent)
        => CompleteWithValues(plan, sourcePlan, identity, capturedAt, _ => null,
            id => AmdSmuMissingValueStatus(id.FieldCode, id.DisplayIndex,
                integratedGpuIndex, gpuInventoryIsCurrent));

    internal static NativeMetricSnapshotSourceCompletion CreateAmdSmuCompletion(
        NativeMetricSnapshotCollectionPlan plan,
        NativeMetricSnapshotSourcePlanOutput sourcePlan,
        NativeMetricSnapshotSourceObservationIdentity identity,
        DateTimeOffset capturedAt,
        CpuSensorMetrics metrics,
        int? integratedGpuIndex,
        bool gpuInventoryIsCurrent)
    {
        // Provider failure must reach source availability so the existing cooldown
        // lets the planner consider alternate rules; an active provider's missing sensor stays per-field.
        if (string.Equals(
            metrics.ProviderState.State,
            HardwareSensorProviderStates.Unavailable,
            StringComparison.OrdinalIgnoreCase))
        {
            return Unavailable(sourcePlan.SourceHandle, identity, capturedAt);
        }

        var selected = SelectedBindings(plan, sourcePlan.SourceHandle);
        var completion = CompleteWithValues(
            plan,
            sourcePlan,
            identity,
            capturedAt,
            id => id.FieldCode switch
            {
                MetricFieldCode.CpuFrequency => metrics.SmuFrequencyMhz,
                MetricFieldCode.CpuFrequencyPercent => null,
                MetricFieldCode.CpuPackagePower => metrics.PackagePowerWatts,
                MetricFieldCode.CpuCoreVoltage => metrics.CoreVoltageVolts,
                MetricFieldCode.CpuPackageCurrent =>
                    metrics.PackageCurrentAmps,
                MetricFieldCode.CpuTemperature => metrics.TemperatureCelsius,
                MetricFieldCode.CpuStapmPower => metrics.StapmPowerWatts,
                MetricFieldCode.CpuActualPower => metrics.ActualPowerWatts,
                MetricFieldCode.CpuAveragePower => metrics.AveragePowerWatts,
                MetricFieldCode.CpuTdcCurrent => metrics.TdcCurrentAmps,
                MetricFieldCode.CpuEdcCurrent => metrics.EdcCurrentAmps,
                MetricFieldCode.CpuSocPower => metrics.SocPowerWatts,
                MetricFieldCode.CpuSocVoltage => metrics.SocVoltageVolts,
                MetricFieldCode.CpuIgpuFrequency => metrics.ApuFrequencyMhz,
                MetricFieldCode.CpuIgpuVoltage => metrics.ApuVoltageVolts,
                MetricFieldCode.CpuIgpuTemperature =>
                    metrics.ApuTemperatureCelsius,
                MetricFieldCode.CpuSmuFrequency => metrics.SmuFrequencyMhz,
                // 核显那三项：认出是核显之后就从 PM table 里出，
                // 和 CPU 的那几个传感量同一次读、同一条路。
                _ => integratedGpuIndex == id.DisplayIndex ? id.FieldCode switch
                {
                    MetricFieldCode.GpuGraphicsClock => metrics.ApuFrequencyMhz,
                    MetricFieldCode.GpuTemperature => metrics.ApuTemperatureCelsius,
                    MetricFieldCode.GpuVoltage => metrics.ApuVoltageVolts,
                    _ => null
                } : null
            },
            id => AmdSmuMissingValueStatus(
                id.FieldCode,
                id.DisplayIndex,
                integratedGpuIndex,
                gpuInventoryIsCurrent));
        return completion with
        {
            IntegratedGpuDeviceIdentity = gpuInventoryIsCurrent && integratedGpuIndex is { } index
                ? plan.Catalog.MetricBindings.Values.Where(binding => binding.SourceHandle == sourcePlan.SourceHandle
                        && binding.FieldCode == MetricFieldCode.GpuTemperature && binding.DisplayIndex == index)
                    .Select(static binding => binding.DeviceIdentity).Distinct(StringComparer.Ordinal).SingleOrDefault()
                : null
        };
    }

    internal static NativeMetricSnapshotObservationStatus AmdSmuMissingValueStatus(
        MetricFieldCode fieldCode,
        int displayIndex,
        int? integratedGpuIndex,
        bool gpuInventoryIsCurrent)
    {
        if (fieldCode == MetricFieldCode.CpuFrequencyPercent)
        {
            return NativeMetricSnapshotObservationStatus.Unsupported;
        }

        var isGpuSensor = fieldCode is MetricFieldCode.GpuGraphicsClock
            or MetricFieldCode.GpuTemperature
            or MetricFieldCode.GpuVoltage;
        return isGpuSensor && gpuInventoryIsCurrent
            && displayIndex >= 0 && displayIndex != integratedGpuIndex
            ? NativeMetricSnapshotObservationStatus.Unsupported
            : NativeMetricSnapshotObservationStatus.Unavailable;
    }

    private NativeMetricSnapshotSourceCompletion CollectStorageSensors(
        NativeMetricSnapshotCollectionPlan plan,
        NativeMetricSnapshotSourcePlanOutput sourcePlan,
        NativeMetricSnapshotSourceObservationIdentity identity,
        DateTimeOffset capturedAt)
    {
        var sensors = resources.ReadStorageSensors();
        if (!MatchesStorageInventory(plan.Catalog, sensors)) requestTopologyRefresh();
        return CreateStorageCompletion(plan, sourcePlan, identity, capturedAt, sensors);
    }

    private NativeMetricSnapshotSourceCompletion CollectNvmeHealth(
        NativeMetricSnapshotCollectionPlan plan, NativeMetricSnapshotSourcePlanOutput sourcePlan,
        NativeMetricSnapshotSourceObservationIdentity identity, DateTimeOffset capturedAt)
    {
        var devices = SelectedBindings(plan, sourcePlan.SourceHandle)
            .GroupBy(static binding => binding.DisplayIndex)
            .ToDictionary(static group => group.Key, static group => group.First().DeviceIdentity);
        var sensors = resources.ReadNvmeSensors(new(devices));
        if (!MatchesStorageInventory(plan.Catalog, sensors)) requestTopologyRefresh();
        return CreateStorageCompletion(plan, sourcePlan, identity, capturedAt, sensors);
    }

    private NativeMetricSnapshotSourceCompletion CollectAtaSmart(
        NativeMetricSnapshotCollectionPlan plan, NativeMetricSnapshotSourcePlanOutput sourcePlan,
        NativeMetricSnapshotSourceObservationIdentity identity, DateTimeOffset capturedAt)
    {
        var sensors = resources.ReadAtaSensors(CreateAtaSmartReadRequest(plan, sourcePlan.SourceHandle));
        if (!MatchesStorageInventory(plan.Catalog, sensors)) requestTopologyRefresh();
        return CreateStorageCompletion(plan, sourcePlan, identity, capturedAt, sensors);
    }

    internal static AtaSmartSensorReadRequest CreateAtaSmartReadRequest(NativeMetricSnapshotCollectionPlan plan, ulong sourceHandle)
        => new(SelectedBindings(plan, sourceHandle).GroupBy(static binding => binding.DisplayIndex)
            .ToDictionary(static group => group.Key, static group => new AtaSmartDeviceReadRequest(
                group.First().DeviceIdentity,
                group.Aggregate(AtaSmartPages.None, static (pages, binding) => pages | WindowsAtaSmartReader.PageFor(binding.FieldCode)))));

    private async Task<NativeMetricSnapshotSourceCompletion> CollectDiskPerformanceAsync(
        NativeMetricSnapshotCollectionPlan plan, NativeMetricSnapshotSourcePlanOutput sourcePlan,
        NativeMetricSnapshotSourceObservationIdentity identity, CancellationToken cancellationToken)
    {
        var bindings = SelectedBindings(plan, sourcePlan.SourceHandle);
        var catalogByIndex = plan.Catalog.StorageIdentities.GroupBy(static item => item.DisplayIndex)
            .Where(static group => group.Count() == 1).ToDictionary(static group => group.Key, static group => group.Single());
        var targets = new Dictionary<(int DisplayIndex, string IdentityKey), WindowsDiskPerformanceTarget>();
        foreach (var binding in bindings)
        {
            if (!catalogByIndex.TryGetValue(binding.DisplayIndex, out var current)
                || !string.Equals(current.ExactIdentityKey, binding.DeviceIdentity, StringComparison.Ordinal))
            {
                requestTopologyRefresh();
                continue;
            }
            targets[(binding.DisplayIndex, binding.DeviceIdentity)] = new(binding.DisplayIndex, binding.DeviceIdentity,
                current.SupportsNvmeHealth, current.SupportsAtaSmart);
        }

        var supportedTargets = targets.Values.Where(static target => target.SupportsExactAtaNvmeIdentity).ToArray();
        var read = supportedTargets.Length == 0
            ? WindowsDiskPerformanceCaptureResult.Empty([])
            : await resources.ReadDiskPerformanceAsync(supportedTargets, cancellationToken).ConfigureAwait(false);
        var sourceObservedAt = DateTimeOffset.UtcNow;
        return CompleteWithValues(
            plan, sourcePlan, identity, sourceObservedAt,
            binding => targets.TryGetValue((binding.DisplayIndex, binding.DeviceIdentity), out var target)
                && read.Readings.TryGetValue(target, out var metrics) ? metrics?.ValueFor(binding.FieldCode) : null,
            binding => catalogByIndex.TryGetValue(binding.DisplayIndex, out var current)
                && string.Equals(current.ExactIdentityKey, binding.DeviceIdentity, StringComparison.Ordinal)
                && !current.SupportsAtaSmart && !current.SupportsNvmeHealth
                    ? NativeMetricSnapshotObservationStatus.Unsupported
                    : NativeMetricSnapshotObservationStatus.Unavailable,
            observedAt: binding => targets.TryGetValue((binding.DisplayIndex, binding.DeviceIdentity), out var target)
                && read.Readings.TryGetValue(target, out var metrics) ? metrics?.ObservedAt : null);
    }

    internal static NativeMetricSnapshotSourceCompletion CreateStorageCompletion(
        NativeMetricSnapshotCollectionPlan plan, NativeMetricSnapshotSourcePlanOutput sourcePlan,
        NativeMetricSnapshotSourceObservationIdentity identity, DateTimeOffset capturedAt, StorageSensorSnapshot sensors)
    {
        var byIndex = sensors.Disks.ToDictionary(
            static disk => disk.Index);
        return CompleteWithValues(
            plan,
            sourcePlan,
            identity,
            capturedAt,
            id => byIndex.TryGetValue(id.DisplayIndex, out var sensor)
                && string.Equals(sensor.IdentityKey, id.DeviceIdentity, StringComparison.Ordinal)
                    ? id.FieldCode == MetricFieldCode.DiskTemperature ? sensor.TemperatureCelsius
                        : sensor.Values.TryGetValue(id.FieldCode, out var value) ? value : null
                    : null,
            unsignedValue: id => byIndex.TryGetValue(id.DisplayIndex, out var sensor)
                && string.Equals(sensor.IdentityKey, id.DeviceIdentity, StringComparison.Ordinal)
                && sensor.IntegerValues.TryGetValue(id.FieldCode, out var value) ? value : null,
            wideUnsignedValue: id => byIndex.TryGetValue(id.DisplayIndex, out var sensor)
                && sensor.IdentityKey == id.DeviceIdentity
                && sensor.WideIntegerValues.TryGetValue(id.FieldCode, out var value) ? value : null,
            bytesValue: id => byIndex.TryGetValue(id.DisplayIndex, out var sensor)
                && sensor.IdentityKey == id.DeviceIdentity
                && sensor.BytesValues.TryGetValue(id.FieldCode, out var value) ? value : null);
    }

    internal static bool MatchesStorageInventory(NativeMetricSnapshotCatalogProjection catalog, StorageSensorSnapshot sensors)
        => sensors.Identities is null || catalog.StorageIdentities
            .Select(static disk => (disk.DisplayIndex, disk.ExactIdentityKey, disk.SupportsNvmeHealth, disk.SupportsAtaSmart)).Order()
            .SequenceEqual(sensors.Identities.Select(static disk => (disk.DisplayIndex, disk.ExactIdentityKey, disk.SupportsNvmeHealth, disk.SupportsAtaSmart)).Order());

    private NativeMetricSnapshotSourceCompletion CollectHardwareMonitor(
        NativeMetricSnapshotCollectionPlan plan,
        NativeMetricSnapshotSourcePlanOutput sourcePlan,
        NativeMetricSnapshotSourceObservationIdentity identity,
        DateTimeOffset capturedAt)
    {
        var sensors = resources.ReadHardwareMonitor();
        if (sensors.CatalogComplete == false) return Unavailable(sourcePlan.SourceHandle, identity, capturedAt);
        var completion = CompleteWithValues(
            plan,
            sourcePlan,
            identity,
            capturedAt,
            id => id.FieldCode switch
            {
                MetricFieldCode.MemoryTemperature =>
                    sensors.MemoryTemperatureCelsius,
                MetricFieldCode.CpuTemperature => sensors.CpuTemperatureCelsius,
                MetricFieldCode.MotherboardTemperature =>
                    sensors.MotherboardTemperature?.Value,
                MetricFieldCode.VrmTemperature =>
                    sensors.VrmTemperature?.Value,
                MetricFieldCode.ChipsetTemperature =>
                    sensors.ChipsetTemperature?.Value,
                MetricFieldCode.MotherboardVoltage =>
                    sensors.MotherboardVoltage?.Value,
                MetricFieldCode.CpuFanRpm => sensors.CpuFanSpeedRpm,
                MetricFieldCode.CpuFanPercent => sensors.CpuFanSpeedPercent,
                MetricFieldCode.FanRpm => sensors.SystemFans.FirstOrDefault(fan => fan.IdentityKey == id.DeviceIdentity)?.SpeedRpm,
                MetricFieldCode.FanPercent => sensors.SystemFans.FirstOrDefault(fan => fan.IdentityKey == id.DeviceIdentity)?.SpeedPercent,
                _ => null
            },
            id => id.Field?.Resource == MetricResource.Gpu
                ? NativeMetricSnapshotObservationStatus.Unsupported
                : NativeMetricSnapshotObservationStatus.Unavailable);
        var selected = SelectedBindings(plan, sourcePlan.SourceHandle);
        return completion with
        {
            SupportedMetricHandles = sensors.CatalogComplete == true
                ? selected.Where(binding => binding.FieldCode == MetricFieldCode.CpuTemperature
                        && sensors.CpuTemperatureSensorIdentifier is not null
                    || binding.FieldCode == MetricFieldCode.FanRpm
                        && sensors.SystemFans.Any(fan => fan.IdentityKey == binding.DeviceIdentity))
                    .Select(static binding => binding.MetricHandle).ToImmutableArray() : [],
            ReadingQuantities = sensors.CatalogComplete == true && sensors.CpuTemperatureReadingKind is { } kind
                ? selected.Where(static binding => binding.FieldCode == MetricFieldCode.CpuTemperature)
                    .ToImmutableDictionary(static binding => binding.MetricHandle, _ => kind)
                : ImmutableDictionary<ulong, string>.Empty
        };
    }

    private NativeMetricSnapshotSourceCompletion CollectNotebookFans(
        NativeMetricSnapshotCollectionPlan plan,
        NativeMetricSnapshotSourcePlanOutput sourcePlan,
        NativeMetricSnapshotSourceObservationIdentity identity,
        DateTimeOffset capturedAt)
    {
        var sensors = resources.ReadNotebookFans();
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
            id => id.FieldCode is MetricFieldCode.CpuFanRpm or MetricFieldCode.CpuFanPercent
                || IsNotebookPhysicalFanField(id, sensors)
                || exactGpuDisplayIndex == id.DisplayIndex
                    && id.FieldCode is MetricFieldCode.GpuFanRpm or MetricFieldCode.GpuFanPercent
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
            HardwareSensorProviderStates.Active => NativeMetricSnapshotSourceStatus.Complete,
            HardwareSensorProviderStates.UnsupportedFirmware =>
                NativeMetricSnapshotSourceStatus.Unsupported,
            _ => NativeMetricSnapshotSourceStatus.Unavailable
        };
    }

    internal static double? ResolveNotebookOemFanValue(
        NativeMetricSnapshotRuleCatalogBinding binding,
        NotebookOemFanSensorSnapshot sensors,
        int? exactGpuDisplayIndex,
        PlatformGpuSensor? gpuFan)
    {
        ArgumentNullException.ThrowIfNull(sensors);
        if (binding.FieldCode == MetricFieldCode.CpuFanRpm) return sensors.CpuFanSpeedRpm;
        if (binding.FieldCode == MetricFieldCode.CpuFanPercent) return sensors.CpuFanSpeedPercent;
        var physicalFan = sensors.PhysicalFans.FirstOrDefault(fan =>
            fan.IdentityKey.Equals(binding.DeviceIdentity, StringComparison.Ordinal));
        if (physicalFan is not null)
        {
            return binding.FieldCode switch
            {
                MetricFieldCode.FanRpm => physicalFan.SpeedRpm,
                MetricFieldCode.FanReportedPwmDuty => physicalFan.ReportedPwmDutyPercent,
                _ => null
            };
        }
        if (exactGpuDisplayIndex != binding.DisplayIndex || gpuFan is null) return null;
        return binding.FieldCode switch
        {
            MetricFieldCode.GpuFanRpm => gpuFan.FanSpeedRpm,
            MetricFieldCode.GpuFanPercent => gpuFan.FanSpeedPercent,
            _ => null
        };
    }

    private static bool IsNotebookPhysicalFanField(
        NativeMetricSnapshotRuleCatalogBinding binding,
        NotebookOemFanSensorSnapshot sensors)
        => binding.FieldCode is MetricFieldCode.FanRpm or MetricFieldCode.FanReportedPwmDuty
            && sensors.PhysicalFans.Any(fan => fan.IdentityKey.Equals(binding.DeviceIdentity, StringComparison.Ordinal));

    private NativeMetricSnapshotSourceCompletion CollectSystemIo(
        NativeMetricSnapshotCollectionPlan plan,
        NativeMetricSnapshotSourcePlanOutput sourcePlan,
        NativeMetricSnapshotSourceObservationIdentity identity,
        DateTimeOffset capturedAt)
    {
        var selected = SelectedBindings(plan, sourcePlan.SourceHandle);
        var includeDisk = selected.Any(static id => id.Field?.Resource == MetricResource.Disk);
        var includeNetwork = selected.Any(static id => id.Field?.Resource == MetricResource.Network);
        var metrics = resources.ReadSystemIo(new PdhSystemIoReadRequest(
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
                var isDisk = binding.Field?.Resource == MetricResource.Disk;
                var domainCurrent = isDisk
                    ? diskCurrent
                    : networkCurrent;
                var observedAt = isDisk
                    ? metrics.DiskObservedAt ?? sourceObservedAt
                    : metrics.NetworkObservedAt ?? sourceObservedAt;
                double? value = !domainCurrent
                    ? null
                    : binding.FieldCode switch
                    {
                        MetricFieldCode.DiskActive =>
                            ReadDiskMetricValue(metrics.Disk, MetricFieldCode.DiskActive),
                        MetricFieldCode.DiskReadRate =>
                            ReadDiskMetricValue(metrics.Disk, MetricFieldCode.DiskReadRate),
                        MetricFieldCode.DiskWriteRate =>
                            ReadDiskMetricValue(metrics.Disk, MetricFieldCode.DiskWriteRate),
                        MetricFieldCode.DiskQueue =>
                            ReadDiskMetricValue(metrics.Disk, MetricFieldCode.DiskQueue),
                        MetricFieldCode.NetworkReceiveRate =>
                            metrics.Network.ReceiveBytesPerSecond,
                        MetricFieldCode.NetworkSendRate =>
                            metrics.Network.SendBytesPerSecond,
                        MetricFieldCode.NetworkUtilization =>
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

    internal static double? ReadDiskMetricValue(
        PdhDiskMetrics metrics,
        MetricFieldCode fieldCode)
        => fieldCode switch
        {
            MetricFieldCode.DiskActive when metrics.HasValue(PdhDiskMetricValidity.ActivePercent)
                => metrics.ActivePercent,
            MetricFieldCode.DiskReadRate when metrics.HasValue(PdhDiskMetricValidity.ReadBytesPerSecond)
                => metrics.ReadBytesPerSecond,
            MetricFieldCode.DiskWriteRate when metrics.HasValue(PdhDiskMetricValidity.WriteBytesPerSecond)
                => metrics.WriteBytesPerSecond,
            MetricFieldCode.DiskQueue when metrics.HasValue(PdhDiskMetricValidity.QueueLength)
                => metrics.QueueLength,
            _ => null
        };

    private WindowsGpuAdapterInventoryRead RequireCurrentGpuInventory(
        NativeMetricSnapshotCatalogProjection catalog)
    {
        var inventory = resources.ReadGpuInventory();
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
            requestTopologyRefresh();
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
                WindowsGpuAdapterIdentity.Pack(adapter.Luid))
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
            Func<NativeMetricSnapshotRuleCatalogBinding, double?> value,
            NativeMetricSnapshotObservationStatus unavailableStatus =
                NativeMetricSnapshotObservationStatus.Unavailable,
            Func<NativeMetricSnapshotRuleCatalogBinding, ulong?>? unsignedValue = null,
            Func<NativeMetricSnapshotRuleCatalogBinding, DateTimeOffset?>? observedAt = null,
            Func<NativeMetricSnapshotRuleCatalogBinding, UInt128?>? wideUnsignedValue = null,
            Func<NativeMetricSnapshotRuleCatalogBinding, byte[]?>? bytesValue = null,
            Func<NativeMetricSnapshotRuleCatalogBinding, NativeMetricSnapshotCounterPair?>? counterPairValue = null,
            Func<NativeMetricSnapshotRuleCatalogBinding, bool?>? booleanValue = null,
            Func<NativeMetricSnapshotRuleCatalogBinding, NativeMetricSnapshotTimedCounter?>? timedCounterValue = null,
            Func<NativeMetricSnapshotRuleCatalogBinding, NativeMetricSnapshotFloatingCounterPair?>? floatingCounterPairValue = null)
        => CompleteWithValues(
            plan,
            sourcePlan,
            identity,
            capturedAt,
            value,
            _ => unavailableStatus,
            unsignedValue,
            observedAt,
            wideUnsignedValue,
            bytesValue,
            counterPairValue,
            booleanValue,
            timedCounterValue,
            floatingCounterPairValue);

    private static NativeMetricSnapshotSourceCompletion
        CompleteWithValues(
            NativeMetricSnapshotCollectionPlan plan,
            NativeMetricSnapshotSourcePlanOutput sourcePlan,
            NativeMetricSnapshotSourceObservationIdentity identity,
            DateTimeOffset capturedAt,
            Func<NativeMetricSnapshotRuleCatalogBinding, double?> value,
            Func<NativeMetricSnapshotRuleCatalogBinding, NativeMetricSnapshotObservationStatus>
                unavailableStatus,
            Func<NativeMetricSnapshotRuleCatalogBinding, ulong?>? unsignedValue = null,
            Func<NativeMetricSnapshotRuleCatalogBinding, DateTimeOffset?>? observedAt = null,
            Func<NativeMetricSnapshotRuleCatalogBinding, UInt128?>? wideUnsignedValue = null,
            Func<NativeMetricSnapshotRuleCatalogBinding, byte[]?>? bytesValue = null,
            Func<NativeMetricSnapshotRuleCatalogBinding, NativeMetricSnapshotCounterPair?>? counterPairValue = null,
            Func<NativeMetricSnapshotRuleCatalogBinding, bool?>? booleanValue = null,
            Func<NativeMetricSnapshotRuleCatalogBinding, NativeMetricSnapshotTimedCounter?>? timedCounterValue = null,
            Func<NativeMetricSnapshotRuleCatalogBinding, NativeMetricSnapshotFloatingCounterPair?>? floatingCounterPairValue = null)
    {
        var observations = MetricsForSource(
                plan,
                sourcePlan.SourceHandle)
            .Select(metric =>
            {
                var binding =
                    plan.Catalog.RuleBindings[metric.RuleHandle];
                var valueTimestamp = observedAt?.Invoke(binding) ?? capturedAt;
                if (binding.MetricKind is (uint)NativeMetricSnapshotMetricKind.FloatingCounterRatio or (uint)NativeMetricSnapshotMetricKind.FloatingCounterPercentage)
                    return NativeMetricSnapshotObservationProjection.CreateFloatingCounterPairObservation(
                        metric, binding, identity, valueTimestamp, floatingCounterPairValue?.Invoke(binding), unavailableStatus(binding));
                if (binding.MetricKind is (uint)NativeMetricSnapshotMetricKind.TimedCounterRate or (uint)NativeMetricSnapshotMetricKind.TimedMicrosecondCounterPercentage)
                    return NativeMetricSnapshotObservationProjection.CreateTimedCounterObservation(
                        metric, binding, identity, valueTimestamp, timedCounterValue?.Invoke(binding), unavailableStatus(binding));
                if (binding.MetricKind is (uint)NativeMetricSnapshotMetricKind.CounterRatio or (uint)NativeMetricSnapshotMetricKind.CounterFrequency)
                    return NativeMetricSnapshotObservationProjection.CreateCounterPairObservation(
                        metric, binding, identity, valueTimestamp, counterPairValue?.Invoke(binding), unavailableStatus(binding));
                if (binding.ValueKind == (uint)NativeMetricSnapshotValueKind.Boolean)
                    return NativeMetricSnapshotObservationProjection.CreateBooleanObservation(
                        metric, binding, identity, valueTimestamp, booleanValue?.Invoke(binding));
                if (binding.ValueKind == (uint)NativeMetricSnapshotValueKind.Unsigned64 && unsignedValue is not null)
                    return NativeMetricSnapshotObservationProjection.CreateUnsignedObservation(
                        metric, binding, identity, valueTimestamp, unsignedValue(binding));
                if (binding.ValueKind == (uint)NativeMetricSnapshotValueKind.Unsigned128 && wideUnsignedValue is not null)
                    return NativeMetricSnapshotObservationProjection.CreateWideUnsignedObservation(
                        metric, binding, identity, valueTimestamp, wideUnsignedValue(binding));
                if (binding.ValueKind == (uint)NativeMetricSnapshotValueKind.Bytes && bytesValue is not null)
                    return NativeMetricSnapshotObservationProjection.CreateBytesObservation(
                        metric, binding, identity, valueTimestamp, bytesValue(binding));
                return NativeMetricSnapshotObservationProjection
                    .CreateObservation(
                        metric,
                        binding,
                        identity,
                        valueTimestamp,
                        value(binding),
                        unavailableStatus(binding));
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

    private static NativeMetricSnapshotRuleCatalogBinding[] SelectedBindings(
        NativeMetricSnapshotCollectionPlan plan, ulong sourceHandle)
        => MetricsForSource(plan, sourceHandle)
            .Select(metric => plan.Catalog.RuleBindings[metric.RuleHandle]).ToArray();

    private static IReadOnlySet<int> GpuIndexes(IEnumerable<NativeMetricSnapshotRuleCatalogBinding> bindings)
        => bindings.Where(static binding => binding.Field?.Resource == MetricResource.Gpu)
            .Select(static binding => binding.DisplayIndex).Where(static index => index >= 0).ToHashSet();

    private static bool Includes(IEnumerable<NativeMetricSnapshotRuleCatalogBinding> bindings, MetricFieldCode field)
        => bindings.Any(binding => binding.FieldCode == field);

    private static double? ResolveGpuValue(
        NativeMetricSnapshotRuleCatalogBinding binding,
        IReadOnlyDictionary<int, GpuMetrics> byIndex)
    {
        if (!byIndex.TryGetValue(binding.DisplayIndex, out var gpu)) return null;
        if (gpu.AdditionalReadings.TryGetValue(binding.FieldCode, out var reading)) return reading;
        return binding.FieldCode switch
        {
            MetricFieldCode.GpuUsage or MetricFieldCode.AdlxActivity when gpu.IsUsageAvailable => gpu.UsagePercent,
            MetricFieldCode.GpuGraphicsClock when gpu.GraphicsFrequencyMhz > 0 => gpu.GraphicsFrequencyMhz,
            MetricFieldCode.GpuGraphicsClockPercent when gpu.GraphicsFrequencyPercent > 0 => gpu.GraphicsFrequencyPercent,
            MetricFieldCode.GpuMemoryUsed when gpu.TotalMemoryBytes > 0 => gpu.UsedMemoryBytes,
            MetricFieldCode.GpuMemoryTotal when gpu.TotalMemoryBytes > 0 => gpu.TotalMemoryBytes,
            MetricFieldCode.GpuMemoryPercent when gpu.TotalMemoryBytes > 0 => gpu.MemoryUsagePercent,
            MetricFieldCode.GpuMemoryClock when gpu.MemoryFrequencyMhz > 0 => gpu.MemoryFrequencyMhz,
            MetricFieldCode.GpuPower => gpu.Sensors.PowerWatts,
            MetricFieldCode.GpuPowerLimit => gpu.Sensors.PowerLimitWatts,
            MetricFieldCode.GpuBoardPower => gpu.Sensors.BoardPowerWatts,
            MetricFieldCode.GpuTemperature => gpu.Sensors.TemperatureCelsius,
            MetricFieldCode.GpuHotspotTemperature => gpu.Sensors.HotspotTemperatureCelsius,
            MetricFieldCode.GpuIntakeTemperature => gpu.Sensors.IntakeTemperatureCelsius,
            MetricFieldCode.GpuFanPercent => gpu.Sensors.FanSpeedPercent,
            MetricFieldCode.GpuFanRpm => gpu.Sensors.FanSpeedRpm,
            MetricFieldCode.GpuVoltage or MetricFieldCode.AdlxVoltage => gpu.Sensors.CoreVoltageVolts,
            MetricFieldCode.GpuCurrent => gpu.Sensors.CurrentAmps,
            _ => gpu.AdditionalReadings.GetValueOrDefault(binding.FieldCode)
        };
    }
}


