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
    internal static NativeMetricSnapshotSourceCompletion CreateMemoryCompletion(NativeMetricSnapshotCollectionPlan plan,
        NativeMetricSnapshotSourcePlanOutput sourcePlan, NativeMetricSnapshotSourceObservationIdentity identity,
        DateTimeOffset capturedAt, MemoryMetrics? metrics, IReadOnlyDictionary<string, ulong> numaValues,
        IReadOnlyDictionary<string, ulong>? numaCapacityMiBValues = null)
        => CompleteWithValues(plan, sourcePlan, identity, capturedAt,
            binding => metrics is { IsUsageAvailable: true, TotalBytes: > 0 } ? binding.FieldCode switch
            {
                MetricFieldCode.MemoryUsed => metrics.UsedBytes,
                MetricFieldCode.MemoryTotal => metrics.TotalBytes,
                MetricFieldCode.MemoryPercent => metrics.UsagePercent,
                MetricFieldCode.MemoryAvailable => metrics.AvailableBytes,
                _ => null
            } : null,
            unsignedValue: binding => binding.FieldCode switch
            {
                MetricFieldCode.NumaNodeAvailableBytes when numaValues.TryGetValue(binding.DeviceIdentity, out var available) => available,
                MetricFieldCode.NumaNodeCapacityMiB when numaCapacityMiBValues?.TryGetValue(binding.DeviceIdentity, out var capacity) == true => capacity,
                _ => null
            });
}


