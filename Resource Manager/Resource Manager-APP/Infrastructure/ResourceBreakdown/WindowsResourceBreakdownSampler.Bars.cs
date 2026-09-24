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
        NetworkAttributionSnapshot networkAttribution,
        ResourceManager.App.Infrastructure.Telemetry.Etw.GpuAllocationReading? residentReading)
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

            // Private commit is not the process's actual page-file occupancy.
            return CreateUnattributedMemoryUsageBar(metricId, metric.Label, scaleMode,
                hardwareSnapshot.VirtualMemory.UsedBytes, hardwareSnapshot.VirtualMemory.TotalBytes);
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

            /*
             * **量取最准的读数，比例取 PDH。**
             *
             * 这两件事先前混在一起，于是界面上出现过"某个软件 8.6%、总和只有 5.0%"：
             * 总和走的是 gpu.UsagePercent（N 卡上是 NVML 的 SM utilization，
             * 问的是"卡上有没有核在跑"），分项走的是 PDH 的 GPU Engine 计数器
             * 按进程把各引擎求和（问的是"这个进程在各引擎上忙了多久"）。
             * 两个量定义不同、采样窗口也不同，所以部分大于整体是必然会发生的；
             * 而聚合层算残差时那个负数被静默丢掉，谁也没吭声。
             *
             * 分工定死：
             *   - **量**用设备自己的读数（有 NVML 就是 NVML，它更准，不该被顶掉）。
             *   - **比例**用 PDH：每个进程占整卡的份额 = 它的引擎和 / 整卡引擎和。
             *     PDH 的绝对值和 NVML 不同口径，但它的相对份额是可信的，
             *     而且这是唯一的每进程来源。
             *
             * 于是分项 = 设备读数 × 该进程的 PDH 份额。分项之和 = 设备读数 ×
             * 已归属份额 ≤ 设备读数，"任一分项 ≤ 总和"恒成立；剩下的那块
             * （归不到进程头上的引擎行）仍然是残差，语义没有丢。
             *
             * 分母是**整卡的 PDH 总和**。它有三种情况，各自的处理是确定的：
             *
             *   - 大于 0：正常，按份额把设备读数摊下去。
             *   - 等于 0：卡闲着。归属本身是好的，只是没有东西可分 ——
             *     照常发布这条，分项自然为空。**0 是一个正常读数，不是"读不到"。**
             *   - 读不到（这块卡根本没有 PDH 引擎行）：归属不可用。
             *     但设备读数仍然是准的，所以这条照常发布，只是没有分项。
             *
             * **三种情况都不会把这条指标判成不可用。** 设备读数在，这一项就是可读的；
             * 归属拿不到是归属的事，由 attributionStatus 如实说，不牵连读数本身。
             */
            var attributionTotal = gpuAttribution.GetUsagePercentTotal(gpuIndex);
            var canScale = attributionTotal is { } total && total > 0;

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
                scaleProcessValues: canScale,
                attributionTotalValue: attributionTotal,
                attributionStatus: attributionTotal is null
                    ? SamplingObservationStatus.Unavailable
                    : gpuAttribution.UsageStatus);
        }

        if (gpuMetricName.Equals("vram", StringComparison.OrdinalIgnoreCase))
        {
            var gpu = hardwareSnapshot.Gpus.FirstOrDefault(gpu => gpu.Index == gpuIndex);
            var used = TryGetCurrentHardwareMetric(hardwareSnapshot, metricId, out var reading)
                ? reading.NumericValue : null;
            var bar = CreateUnattributedMemoryUsageBar(metricId, $"GPU{gpuIndex} 显存占用", scaleMode,
                used, gpu?.TotalMemoryBytes);
            var adapter = hardwareSnapshot.GpuInventory.Adapters.FirstOrDefault(item => item.Index == gpuIndex);
            return CreateGpuResidentPartition(bar,
                adapter is null ? null : residentReading?.Adapters.GetValueOrDefault(adapter.AdapterKey),
                processAttribution, baseScorePlan);
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
