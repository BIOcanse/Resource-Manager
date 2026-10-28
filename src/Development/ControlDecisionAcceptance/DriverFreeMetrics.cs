using Microsoft.Extensions.DependencyInjection.Extensions;
using ResourceManager.App.Application.Monitoring;
using ResourceManager.App.Infrastructure.Adaptation;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using ResourceManager.ReadWrite.Native;
using ResourceManager.ReadWrite.Windows.Metrics.Resources;
using ResourceManager.ReadWrite.Windows.Metrics.Writes;

namespace ResourceManager.ControlDecisionAcceptance;

internal static class DriverFreeMetrics
{
    // Borrow the very same production DI zones/read delegates as CreateBorrowedResources.
    // The omitted fan/OEM delegates are never replaced with a successful/current publication.
    internal static void Apply(IServiceCollection services)
    {
        services.Replace(ServiceDescriptor.Singleton<WindowsHardwareMetricSampler>(provider => new(
            provider.GetRequiredService<WindowsCpuMonitoringZone>(),
            provider.GetRequiredService<WindowsMemoryMonitoringZone>(),
            provider.GetRequiredService<WindowsVirtualMemoryMonitoringZone>(),
            provider.GetRequiredService<WindowsGpuAdapterOrderMonitoringZone>(),
            provider.GetRequiredService<PdhCpuFrequencyMonitoringZone>(),
            provider.GetRequiredService<PdhGpuEngineMonitoringZone>(),
            provider.GetRequiredService<PdhSystemIoMonitoringZone>(),
            provider.GetRequiredService<NvidiaNvmlMonitoringZone>(),
            provider.GetRequiredService<NvidiaNvapiMonitoringZone>(),
            provider.GetRequiredService<AmdAdlxMonitoringZone>(),
            provider.GetRequiredService<AmdSmuMonitoringZone>(),
            provider.GetRequiredService<AmdIntegratedGpuSensorRoute>(),
            provider.GetRequiredService<WindowsPlatformSensorReader>(),
            provider.GetRequiredService<HostManagerMetricSnapshotOwner>(),
            provider.GetRequiredService<HardwareMetricCatalogState>(),
            provider.GetRequiredService<IMonitoringSourceZoneRegistry>(),
            provider.GetRequiredService<HostManagerSamplingSubscriptionOwner>(),
            provider.GetRequiredService<RuntimePlanProvider>(),
            provider.GetRequiredService<ILogger<WindowsHardwareMetricSampler>>(),
            batteryZone: provider.GetRequiredService<WindowsBatteryMonitoringZone>(),
            intelMsrZone: provider.GetRequiredService<IntelMsrMonitoringZone>(),
            networkZone: provider.GetRequiredService<IpHelperNetworkMonitoringZone>(),
            intelGclZone: provider.GetRequiredService<IntelGclMonitoringZone>(),
            npuMemoryZone: provider.GetRequiredService<WindowsNpuMemoryMonitoringZone>(),
            npuEngineZone: provider.GetRequiredService<WindowsNpuEngineMonitoringZone>(),
            borrowedResources: CreateBorrowedResources(provider))));
    }

    private static WindowsHardwareMetricResources CreateBorrowedResources(IServiceProvider provider) => new()
    {
        // No write resource is supplied. This cannot activate a driver through control-field reads.
        Controls = new WindowsMetricControlResources(),
        ReadCpuCounters = () => { var v = provider.GetRequiredService<WindowsCpuMonitoringZone>().ReadCounters();
            return new(v.IsAvailable, v.IdleTicks, v.KernelTicks, v.UserTicks, v.MonotonicTicks, v.MonotonicTicksPerSecond, v.ObservedAt); },
        ReadMemory = () => provider.GetRequiredService<WindowsMemoryMonitoringZone>().ReadMetrics(),
        ReadVirtualMemory = () => provider.GetRequiredService<WindowsVirtualMemoryMonitoringZone>().ReadMetrics(),
        ReadNumaAvailable = (nodes, devices) => provider.GetRequiredService<WindowsMemoryMonitoringZone>().ReadNumaAvailable(nodes, devices),
        ReadNumaCapacityMiB = (nodes, devices) => provider.GetRequiredService<WindowsMemoryMonitoringZone>().ReadNumaCapacityMiB(nodes, devices),
        ReadNumaIdentities = WindowsMemoryMonitoringZone.ReadNumaIdentities,
        ReadGpuInventory = () => provider.GetRequiredService<WindowsGpuAdapterOrderMonitoringZone>().ReadInventory(requested: true),
        ReadCpuFrequency = () => provider.GetRequiredService<PdhCpuFrequencyMonitoringZone>().ReadFrequency(),
        ReadGpuUsage = adapters => provider.GetRequiredService<PdhGpuEngineMonitoringZone>().ReadUsageSnapshotByAdapterIndex(adapters),
        ReadGpuSharedMemory = adapters => provider.GetRequiredService<PdhGpuEngineMonitoringZone>().ReadSharedMemoryByAdapterIndex(adapters),
        ReadNvmlAsync = (request, inventory, token) => provider.GetRequiredService<NvidiaNvmlMonitoringZone>().ReadAsync(request, inventory, token),
        ReadNvmlFanIdentities = inventory => provider.GetRequiredService<NvidiaNvmlMonitoringZone>().ReadFanIdentities(inventory),
        ReadNvmlFansAsync = (targets, inventory, token) => provider.GetRequiredService<NvidiaNvmlMonitoringZone>().ReadFansAsync(targets, inventory, token),
        ReadNvapiAsync = (request, inventory, token) => provider.GetRequiredService<NvidiaNvapiMonitoringZone>().ReadAsync(request, inventory, token),
        ReadNvapiDrs = (profiles, limits, token) => provider.GetRequiredService<NvidiaNvapiMonitoringZone>().ReadDrs(profiles, limits, token),
        DiscoverNvapiDrs = (limits, token) => provider.GetRequiredService<NvidiaNvapiMonitoringZone>().DiscoverDrs(limits, token),
        ReadAdlxAsync = (request, inventory, token) => provider.GetRequiredService<AmdAdlxMonitoringZone>().ReadAsync(request, inventory, token),
        // All PawnIO paths are explicitly unavailable rather than relying on absence of a module file.
        ReadAmdSmu = _ => throw new FileNotFoundException(EffectBoundaries.Reason + ":AMD-SMU-PawnIO"),
        ReadAmdCcdIdentities = (_, _) => new(NativeMetricSnapshotCatalogTopologyStatus.Unavailable, []),
        ReadAmdCcd = (_, _) => throw new FileNotFoundException(EffectBoundaries.Reason + ":AMD-SMN-PawnIO"),
        ReadIntelGpu = (inventory, request, token) => provider.GetRequiredService<IntelGclMonitoringZone>().Read(inventory, request, token),
        OpenIgclFanDirectory = provider.GetRequiredService<IntelGclMonitoringZone>().OpenFanTopology,
        ReadIgclFans = provider.GetRequiredService<IntelGclMonitoringZone>().ReadFans,
        ReadIntelMsr = (_, _) => throw new FileNotFoundException(EffectBoundaries.Reason + ":Intel-MSR-PawnIO"),
        ReadNpuMemoryIdentities = provider.GetRequiredService<WindowsNpuMemoryMonitoringZone>().ReadIdentities,
        ReadNpuMemory = provider.GetRequiredService<WindowsNpuMemoryMonitoringZone>().Read,
        OpenNpuEngineDirectory = provider.GetRequiredService<WindowsNpuEngineMonitoringZone>().OpenDirectory,
        ReadNpuEngines = provider.GetRequiredService<WindowsNpuEngineMonitoringZone>().Read,
        ReadBatteryIdentities = () => provider.GetRequiredService<WindowsBatteryMonitoringZone>().ReadIdentities(),
        ReadBattery = (devices, request) => provider.GetRequiredService<WindowsBatteryMonitoringZone>().Read(devices, request),
        ReadNetworkIdentities = () => provider.GetRequiredService<IpHelperNetworkMonitoringZone>().ReadInterfaceIdentities(),
        ReadNetworkInterfaces = (devices, request) => provider.GetRequiredService<IpHelperNetworkMonitoringZone>().ReadInterfaces(devices, request),
        ReadSystemIo = request => provider.GetRequiredService<PdhSystemIoMonitoringZone>().Read(request),
        ReadStorageIdentities = generation => WindowsStorageSensorsMonitoringZone.ReadIdentities(generation),
        ReadDiskPerformanceAsync = (targets, token) => provider.GetRequiredService<WindowsPlatformSensorReader>().ReadDiskPerformanceAsync(targets, token),
        ReadFanIdentities = _ => new(NativeMetricSnapshotCatalogTopologyStatus.Unavailable, []),
        ReadStorageSensors = () => provider.GetRequiredService<WindowsPlatformSensorReader>().ReadStorageSensors(),
        ReadNvmeSensors = request => provider.GetRequiredService<WindowsPlatformSensorReader>().ReadNvmeSensors(request),
        ReadAtaSensors = request => provider.GetRequiredService<WindowsPlatformSensorReader>().ReadAtaSensors(request),
        ReadHardwareMonitor = () => throw new FileNotFoundException(EffectBoundaries.Reason + ":LibreHardwareMonitor-PawnIO"),
        ReadNotebookFans = () => throw new FileNotFoundException(EffectBoundaries.Reason + ":OEM-EC-ACPI"),
        SynchronizeNotebookDemand = _ => { }
    };
}

