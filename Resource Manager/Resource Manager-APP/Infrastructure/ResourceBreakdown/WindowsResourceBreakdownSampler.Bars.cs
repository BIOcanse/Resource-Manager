using ResourceManager.App.Application.Metrics;
using ResourceManager.App.Application.ProcessAttribution;
using ResourceManager.App.Application.ResourceBreakdown;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.Monitoring;

namespace ResourceManager.App.Infrastructure.ResourceBreakdown;

public sealed partial class WindowsResourceBreakdownSampler
{
    private ResourceBreakdownBar? CreateBar(
        string metricId,
        string scaleMode,
        ProcessAttributionSnapshot processAttribution,
        CompiledBaseScorePlan baseScorePlan,
        HardwareMetricSnapshot hardwareSnapshot,
        ProcessGpuBreakdownRead gpuAttribution,
        PhysicalDiskIoAttributionSnapshot diskAttribution,
        NetworkAttributionSnapshot networkAttribution)
    {
        if (metricId.Equals("cpu.usage", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryGetCurrentHardwareMetric(hardwareSnapshot, metricId, out var metric))
            {
                return CreateUnavailableBar(
                    metricId,
                    hardwareSnapshot.Items.GetValueOrDefault(metricId)?.Label ?? "CPU 占用率",
                    "%",
                    scaleMode,
                    CpuObservationStatus(hardwareSnapshot.Cpu));
            }

            return CreateBar(
                metricId,
                metric.Label,
                "%",
                scaleMode,
                100,
                hardwareSnapshot.Cpu.UsagePercent,
                processAttribution.Processes
                    .Where(static process => process.HasCpuPercent)
                    .Select(static process =>
                    KeyValuePair.Create(process.ProcessId, process.CpuPercent)),
                processAttribution,
                baseScorePlan,
                residualBreakdownProvider,
                isBytes: false);
        }

        if (metricId.Equals("memory.usage", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryGetCurrentHardwareMetric(hardwareSnapshot, metricId, out var metric))
            {
                return CreateUnavailableBar(
                    metricId,
                    hardwareSnapshot.Items.GetValueOrDefault(metricId)?.Label ?? "内存占用",
                    "B",
                    scaleMode,
                    NormalizeObservationStatus(hardwareSnapshot.Memory.ObservationStatus));
            }

            return CreateBar(
                metricId,
                metric.Label,
                "B",
                scaleMode,
                hardwareSnapshot.Memory.TotalBytes,
                hardwareSnapshot.Memory.UsedBytes,
                processAttribution.Processes
                    .Where(static process => process.HasWorkingSetBytes)
                    .Select(static process =>
                    KeyValuePair.Create(process.ProcessId, (double)Math.Max(0, process.WorkingSetBytes))),
                processAttribution,
                baseScorePlan,
                residualBreakdownProvider,
                isBytes: true);
        }

        if (metricId.Equals("virtualMemory.usage", StringComparison.OrdinalIgnoreCase))
        {
            if (!hardwareSnapshot.VirtualMemory.IsSelectable
                || !TryGetCurrentHardwareMetric(hardwareSnapshot, metricId, out var metric))
            {
                return CreateUnavailableBar(
                    metricId,
                    hardwareSnapshot.Items.GetValueOrDefault(metricId)?.Label ?? "虚拟内存占用",
                    "B",
                    scaleMode,
                    NormalizeObservationStatus(hardwareSnapshot.VirtualMemory.ObservationStatus));
            }

            return CreateBar(
                metricId,
                metric.Label,
                "B",
                scaleMode,
                hardwareSnapshot.VirtualMemory.TotalBytes,
                hardwareSnapshot.VirtualMemory.UsedBytes,
                processAttribution.Processes
                    .Where(static process => process.HasPrivateMemoryBytes)
                    .Select(static process =>
                    KeyValuePair.Create(process.ProcessId, (double)Math.Max(0, process.PrivateMemoryBytes))),
                processAttribution,
                baseScorePlan,
                residualBreakdownProvider,
                isBytes: true,
                scaleProcessValues: false);
        }

        if (metricId.Equals(ResourceBreakdownMetricIds.DiskIo, StringComparison.OrdinalIgnoreCase))
        {
            return CreateDiskThroughputBar(
                metricId,
                "磁盘 I/O",
                diskAttribution,
                processAttribution,
                baseScorePlan,
                residualBreakdownProvider);
        }

        if (metricId.Equals(ResourceBreakdownMetricIds.DiskRead, StringComparison.OrdinalIgnoreCase))
        {
            return CreateDiskThroughputBar(
                metricId,
                "磁盘读取",
                diskAttribution,
                processAttribution,
                baseScorePlan,
                residualBreakdownProvider);
        }

        if (metricId.Equals(ResourceBreakdownMetricIds.DiskWrite, StringComparison.OrdinalIgnoreCase))
        {
            return CreateDiskThroughputBar(
                metricId,
                "磁盘写入",
                diskAttribution,
                processAttribution,
                baseScorePlan,
                residualBreakdownProvider);
        }

        if (metricId.Equals(ResourceBreakdownMetricIds.NetworkTraffic, StringComparison.OrdinalIgnoreCase))
        {
            return CreateNetworkThroughputBar(
                metricId,
                "外部网络流量",
                networkAttribution,
                processAttribution,
                baseScorePlan,
                residualBreakdownProvider);
        }

        if (metricId.Equals(ResourceBreakdownMetricIds.NetworkReceive, StringComparison.OrdinalIgnoreCase))
        {
            return CreateNetworkThroughputBar(
                metricId,
                "外部网络接收",
                networkAttribution,
                processAttribution,
                baseScorePlan,
                residualBreakdownProvider);
        }

        if (metricId.Equals(ResourceBreakdownMetricIds.NetworkSend, StringComparison.OrdinalIgnoreCase))
        {
            return CreateNetworkThroughputBar(
                metricId,
                "外部网络发送",
                networkAttribution,
                processAttribution,
                baseScorePlan,
                residualBreakdownProvider);
        }

        if (metricId.Equals(ResourceBreakdownMetricIds.NetworkRawTraffic, StringComparison.OrdinalIgnoreCase))
        {
            return CreateNetworkThroughputBar(
                metricId,
                "普通网络流量",
                networkAttribution,
                processAttribution,
                baseScorePlan,
                residualBreakdownProvider);
        }

        if (metricId.Equals(ResourceBreakdownMetricIds.NetworkRawReceive, StringComparison.OrdinalIgnoreCase))
        {
            return CreateNetworkThroughputBar(
                metricId,
                "普通网络接收",
                networkAttribution,
                processAttribution,
                baseScorePlan,
                residualBreakdownProvider);
        }

        if (metricId.Equals(ResourceBreakdownMetricIds.NetworkRawSend, StringComparison.OrdinalIgnoreCase))
        {
            return CreateNetworkThroughputBar(
                metricId,
                "普通网络发送",
                networkAttribution,
                processAttribution,
                baseScorePlan,
                residualBreakdownProvider);
        }

        if (!TryParseGpuMetric(metricId, out var gpuIndex, out var gpuMetricName))
        {
            return null;
        }

        if (gpuMetricName.Equals("usage", StringComparison.OrdinalIgnoreCase))
        {
            var gpu = hardwareSnapshot.Gpus.FirstOrDefault(
                gpu => gpu.Index == gpuIndex);
            if (gpu is null
                || !gpu.IsUsageAvailable
                || !TryGetCurrentHardwareMetric(
                    hardwareSnapshot,
                    metricId,
                    out var metric))
            {
                return CreateUnavailableBar(
                    metricId,
                    hardwareSnapshot.Items.GetValueOrDefault(metricId)?.Label
                        ?? $"GPU{gpuIndex} 占用率",
                    "%",
                    scaleMode,
                    GpuObservationStatus(
                        hardwareSnapshot,
                        gpuIndex,
                        usage: true),
                    gpuAttribution.UsageStatus);
            }

            var usageByProcess =
                gpuAttribution.GetUsagePercentByProcess(gpuIndex);
            return CreateBar(
                metricId,
                metric.Label,
                "%",
                scaleMode,
                100,
                Math.Clamp(gpu.UsagePercent, 0, 100),
                usageByProcess,
                processAttribution,
                baseScorePlan,
                residualBreakdownProvider,
                isBytes: false,
                scaleProcessValues: false,
                attributionStatus: gpuAttribution.UsageStatus);
        }

        if (gpuMetricName.Equals("vram", StringComparison.OrdinalIgnoreCase))
        {
            var gpu = hardwareSnapshot.Gpus.FirstOrDefault(gpu => gpu.Index == gpuIndex);
            return new ResourceBreakdownBar(metricId, $"GPU{gpuIndex} 显存占用", "B", scaleMode,
                null, gpu?.TotalMemoryBytes ?? 0, null, [], SamplingObservationStatus.Unavailable,
                SamplingObservationStatus.Unavailable);
        }

        return null;
    }

    private static bool TryGetCurrentHardwareMetric(
        HardwareMetricSnapshot snapshot,
        string metricId,
        out MetricValue metric)
    {
        var datasetId = SamplingDatasetIds.ForSystemMetric(metricId);
        if (snapshot.Datasets.TryGetValue(datasetId, out var observation)
            && observation.Status == SamplingObservationStatus.Current
            && snapshot.Items.TryGetValue(metricId, out metric!)
            // 可用与否看读数本身，不看显示串：容量类指标已经不带显示串了，
            // 拿字符串判空会把内存和显存整条判成不可用。
            && metric.NumericValue is double reading
            && double.IsFinite(reading))
        {
            return true;
        }

        metric = null!;
        return false;
    }

    private static ResourceBreakdownBar CreateUnavailableBar(
        string metricId,
        string label,
        string unit,
        string scaleMode,
        SamplingObservationStatus observationStatus,
        SamplingObservationStatus attributionStatus = SamplingObservationStatus.NotRequested)
    {
        return new ResourceBreakdownBar(
            metricId,
            label,
            unit,
            scaleMode,
            null,
            null,
            null,
            [],
            NormalizeObservationStatus(observationStatus),
            NormalizeObservationStatus(attributionStatus));
    }

    private static SamplingObservationStatus CpuObservationStatus(CpuMetrics cpu)
    {
        return cpu.ObservationStatus switch
        {
            CpuMetricObservationStatus.Complete when cpu.IsUsageAvailable =>
                SamplingObservationStatus.Current,
            CpuMetricObservationStatus.NotRequested =>
                SamplingObservationStatus.NotRequested,
            _ => SamplingObservationStatus.Unavailable
        };
    }

    private static SamplingObservationStatus GpuObservationStatus(
        HardwareMetricSnapshot snapshot,
        int gpuIndex,
        bool usage)
    {
        var adapter = snapshot.GpuInventory.Adapters.FirstOrDefault(
            adapter => adapter.Index == gpuIndex);
        if (adapter is null)
        {
            return SamplingObservationStatus.Unavailable;
        }

        return NormalizeObservationStatus(
            usage ? adapter.UsageStatus : adapter.CapacityStatus);
    }

    private static SamplingObservationStatus NormalizeObservationStatus(
        SamplingObservationStatus status)
    {
        return status == SamplingObservationStatus.Invalid
            ? SamplingObservationStatus.Unavailable
            : status;
    }
}
