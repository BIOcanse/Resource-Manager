using Microsoft.Extensions.Logging;
using ResourceManager.App.Application.DiskUsage;
using ResourceManager.App.Application.CpuTopology;
using ResourceManager.App.Application.Metrics;
using ResourceManager.App.Application.Monitoring;
using ResourceManager.App.Application.NetworkTelemetry;
using ResourceManager.App.Application.ResourceBreakdown;
using ResourceManager.App.Application.ResourceTable;
using ResourceManager.App.Application.SoftwareDiscovery;
using ResourceManager.App.Application.Adaptation;
using ResourceManager.App.Application.RuntimeSpecialization;
using ResourceManager.App.Application.SystemHealth;
using ResourceManager.App.Infrastructure.DiskUsage;
using ResourceManager.App.Infrastructure.CpuTopology;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Infrastructure.Monitoring.GpuTelemetry;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.NetworkTelemetry;
using ResourceManager.App.Infrastructure.ResourceBreakdown;
using ResourceManager.App.Infrastructure.ResourceTable;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.SoftwareDiscovery;
using ResourceManager.App.Infrastructure.SystemHealth.Interrupts;
using ResourceManager.App.Infrastructure.Telemetry.Etw;
using ResourceManager.App.Hosting.StartupCapabilities;

namespace ResourceManager.App.Hosting;

public static partial class ResourceManagerServiceCollectionExtensions
{
    private static IServiceCollection AddResourceManagerMonitoring(
        this IServiceCollection services,
        StartupCapabilitySet startupCapabilities)
    {
        services.AddSingleton<NativePdhCollector>(static provider =>
            new NativePdhCollector(provider.GetRequiredService<HostManagerPdhCollectorRuntime>()));
        services.AddMonitoringSourceZone<WindowsCpuMonitoringZone>();
        services.AddMonitoringSourceZone<WindowsMemoryMonitoringZone>();
        services.AddMonitoringSourceZone<WindowsVirtualMemoryMonitoringZone>();
        services.AddMonitoringSourceZone<WindowsGpuAdapterOrderMonitoringZone>();
        services.AddMonitoringSourceZone<PdhCpuFrequencyMonitoringZone>();
        services.AddSingleton<PdhGpuEngineMonitoringZone>(static provider =>
            new PdhGpuEngineMonitoringZone(provider.GetRequiredService<NativePdhCollector>()));
        services.AddSingleton<PdhSystemIoMonitoringZone>(static provider =>
            new PdhSystemIoMonitoringZone(provider.GetRequiredService<NativePdhCollector>()));
        services.AddMonitoringSourceZone<NvidiaNvmlMonitoringZone>();
        services.AddMonitoringSourceZone<NvidiaNvapiMonitoringZone>();
        services.AddMonitoringSourceZone<AmdAdlxMonitoringZone>();
        services.AddMonitoringSourceZone<AmdSmuMonitoringZone>();
        services.AddMonitoringSourceZone<WindowsStorageSensorsMonitoringZone>();
        services.AddMonitoringSourceZone<HardwareMonitorWmiMonitoringZone>();
        services.AddMonitoringSourceZone<NotebookOemFanMonitoringZone>();
        services.AddMonitoringSourceZone<IpHelperNetworkMonitoringZone>();
        services.AddMonitoringSourceZone<EtwNetworkTcpIpMonitoringZone>();
        services.AddSingleton<MonitoringSourceZoneRegistry>(static provider => new MonitoringSourceZoneRegistry(GetMonitoringSourceZones(provider)));
        services.AddSingleton<IMonitoringSourceZoneRegistry>(static provider => provider.GetRequiredService<MonitoringSourceZoneRegistry>());
        services.AddSingleton<GpuTelemetryLocalProbe>();
        services.AddSingleton<GpuTelemetryWorkerSupervisor>();
        services.AddSingleton<IGpuTelemetryWorkerClient>(static provider => provider.GetRequiredService<GpuTelemetryWorkerSupervisor>());
        services.AddSingleton<IGpuTelemetryPushSource>(static provider => provider.GetRequiredService<GpuTelemetryWorkerSupervisor>());
        services.AddHostedServiceAlias<GpuTelemetryWorkerSupervisor>();

        services.AddSingleton<FirmwareIdentityReader>();
        services.AddSingleton<FirmwareProviderPlanCompiler>();
        services.AddSingleton<WindowsPlatformSensorReader>();
        services.AddSingleton(static provider => new WindowsHardwareMetricSampler(
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
            provider.GetRequiredService<WindowsPlatformSensorReader>(),
            provider.GetRequiredService<HostManagerMetricSnapshotOwner>(),
            provider.GetRequiredService<DashboardMonitoringCatalogState>(),
            provider.GetRequiredService<IMonitoringSourceZoneRegistry>(),
            provider.GetRequiredService<HostManagerSamplingSubscriptionOwner>(),
            provider.GetRequiredService<RuntimePlanProvider>(),
            provider.GetRequiredService<ILogger<WindowsHardwareMetricSampler>>()));
        services.AddSingleton<IMetricSampler>(static provider => provider.GetRequiredService<WindowsHardwareMetricSampler>());
        services.AddSingleton<IMetricSnapshotObservationSource>(static provider => provider.GetRequiredService<WindowsHardwareMetricSampler>());
        services.AddSingleton<IMetricSnapshotPushSource>(static provider => provider.GetRequiredService<WindowsHardwareMetricSampler>());
        services.AddHostedServiceAlias<WindowsHardwareMetricSampler>();
        services.AddSingleton<DashboardMonitoringPlanSynchronizer>();
        services.AddHostedServiceAlias<DashboardMonitoringPlanSynchronizer>();
        services.AddSingleton<ICpuCorePerformanceOverrideStore, JsonCpuCorePerformanceOverrideStore>();
        services.AddSingleton<ICpuTopologySampler, WindowsCpuTopologyReader>();
        services.AddSingleton<CpuTopologySnapshotProvider>();
        services.AddSingleton<ICpuTopologyReader>(static provider => provider.GetRequiredService<CpuTopologySnapshotProvider>());
        services.AddHostedServiceAlias<CpuTopologySnapshotProvider>();

        // 磁盘占用：卷清单只读，随叫随取。
        services.AddSingleton<IDiskUsageVolumeCatalog, WindowsDiskUsageVolumeCatalog>();
        services.AddSingleton<IDiskUsageTreeStore, DiskUsageTreeStore>();
        // 快速扫描读主文件表，全扫描逐级遍历。进来的请求由前者按模式分派，
        // 不匹配就把请求原样交给后者 —— 模式是用户选的，程序不替他改。
        services.AddSingleton<DirectoryWalkDiskUsageScanner>();
        services.AddSingleton<IDiskUsageScanner>(static provider =>
            new WindowsMftDiskUsageScanner(
                provider.GetRequiredService<IDiskUsageVolumeCatalog>(),
                provider.GetRequiredService<DirectoryWalkDiskUsageScanner>()));

        services.AddSingleton<KernelEtwSessionBroker>();
        services.AddSingleton<IKernelEtwSessionBroker>(static provider => provider.GetRequiredService<KernelEtwSessionBroker>());
        services.AddHostedServiceAlias<KernelEtwSessionBroker>();

        services.AddSingleton<PortableSoftwareDiscoveryService>();
        services.AddSingleton<IPortableSoftwareDiscovery>(static provider => provider.GetRequiredService<PortableSoftwareDiscoveryService>());
        if (startupCapabilities.Allows(StartupCapability.MutablePersistence))
        {
            services.AddHostedServiceAlias<PortableSoftwareDiscoveryService>();
        }

        services.AddSingleton<EtwCpuCoreResidencyReader>();
        services.AddSingleton<ICpuCoreResidencyReader>(static provider => provider.GetRequiredService<EtwCpuCoreResidencyReader>());
        services.AddSingleton<IResourceTableProviderStateSource>(static provider => provider.GetRequiredService<EtwCpuCoreResidencyReader>());
        services.AddSingleton<IResourceManagerSelfComputeZone>(static provider => provider.GetRequiredService<EtwCpuCoreResidencyReader>());
        services.AddHostedServiceAlias<EtwCpuCoreResidencyReader>();

        services.AddSingleton<EtwSystemInterruptSnapshotSource>();
        services.AddSingleton<ISystemInterruptSnapshotSource>(static provider => provider.GetRequiredService<EtwSystemInterruptSnapshotSource>());
        services.AddSingleton<IResourceManagerSelfComputeZone>(static provider => provider.GetRequiredService<EtwSystemInterruptSnapshotSource>());
        services.AddHostedServiceAlias<EtwSystemInterruptSnapshotSource>();

        services.AddSingleton<EtwPhysicalDiskIoAttributionReader>();
        services.AddSingleton<IPhysicalDiskIoAttributionReader>(static provider => provider.GetRequiredService<EtwPhysicalDiskIoAttributionReader>());
        services.AddSingleton<IResourceTableProviderStateSource>(static provider => provider.GetRequiredService<EtwPhysicalDiskIoAttributionReader>());
        services.AddSingleton<IResourceManagerSelfComputeZone>(static provider => provider.GetRequiredService<EtwPhysicalDiskIoAttributionReader>());
        services.AddHostedServiceAlias<EtwPhysicalDiskIoAttributionReader>();

        services.AddSingleton<DxgkrnlVidMmEtwTelemetryZone>();
        services.AddSingleton<IResourceManagerSelfComputeZone>(static provider => provider.GetRequiredService<DxgkrnlVidMmEtwTelemetryZone>());
        services.AddHostedServiceAlias<DxgkrnlVidMmEtwTelemetryZone>();
        services.AddSingleton<DxgkrnlVidMmEtwResidualBreakdownProvider>();
        services.AddSingleton<IResourceResidualBreakdownProvider>(static provider => provider.GetRequiredService<DxgkrnlVidMmEtwResidualBreakdownProvider>());
        services.AddSingleton<IResourceTableProviderStateSource>(static provider => provider.GetRequiredService<DxgkrnlVidMmEtwResidualBreakdownProvider>());

        services.AddSingleton<IKnownNetworkProxyCatalog, KnownNetworkProxyCatalog>();
        services.AddSingleton<EtwNetworkTcpIpAttributionReader>();
        services.AddSingleton<IEtwNetworkAttributionReader>(static provider => provider.GetRequiredService<EtwNetworkTcpIpAttributionReader>());
        services.AddSingleton<IResourceTableProviderStateSource>(static provider => provider.GetRequiredService<EtwNetworkTcpIpAttributionReader>());
        services.AddHostedServiceAlias<EtwNetworkTcpIpAttributionReader>();
        services.AddSingleton<IpHelperNetworkAttributionReader>();
        services.AddSingleton<INetworkAttributionReader>(static provider => provider.GetRequiredService<IpHelperNetworkAttributionReader>());
        services.AddSingleton<IResourceTableProviderStateSource>(static provider => provider.GetRequiredService<IpHelperNetworkAttributionReader>());

        services.AddSingleton<PdhProcessGpuReader>(static provider =>
            new PdhProcessGpuReader(provider.GetRequiredService<NativePdhCollector>()));
        services.AddSingleton<WindowsResourceBreakdownSampler>();
        services.AddSingleton<GpuAllocationCollector>();
        services.AddSingleton<IResourceBreakdownSampler>(static provider => provider.GetRequiredService<WindowsResourceBreakdownSampler>());
        services.AddSingleton<IResourceBreakdownObservationSource>(static provider => provider.GetRequiredService<WindowsResourceBreakdownSampler>());
        services.AddSingleton<IResourceBreakdownPushSource>(static provider => provider.GetRequiredService<WindowsResourceBreakdownSampler>());
        services.AddSingleton<ISchedulingProcessFactSource>(static provider => provider.GetRequiredService<WindowsResourceBreakdownSampler>());
        services.AddSingleton<ISchedulingProcessFactObservationSource>(static provider => provider.GetRequiredService<WindowsResourceBreakdownSampler>());
        services.AddHostedServiceAlias<WindowsResourceBreakdownSampler>();
        services.AddSingleton<IResourceTableProjector, ResourceTableProjector>();
        return services;
    }

    private static IServiceCollection AddMonitoringSourceZone<TZone>(this IServiceCollection services)
        where TZone : MonitoringSourceZone
    {
        services.AddSingleton<TZone>();
        return services;
    }

    private static IReadOnlyList<MonitoringSourceZone> GetMonitoringSourceZones(IServiceProvider provider)
    {
        return
        [
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
            provider.GetRequiredService<WindowsStorageSensorsMonitoringZone>(),
            provider.GetRequiredService<HardwareMonitorWmiMonitoringZone>(),
            provider.GetRequiredService<NotebookOemFanMonitoringZone>(),
            provider.GetRequiredService<IpHelperNetworkMonitoringZone>(),
            provider.GetRequiredService<EtwNetworkTcpIpMonitoringZone>()
        ];
    }
}
