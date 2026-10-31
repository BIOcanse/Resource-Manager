using BlockUids = ResourceManager.App.StateItems.BlockUids;
using ResourceManager.App.Application.ProcessAttribution;
using ResourceManager.ReadWrite.Windows.Frames;
using Microsoft.Extensions.Logging;
using ResourceManager.App.Application.Control;
using ResourceManager.App.Application.DiskUsage;
using ResourceManager.App.Application.CpuTopology;
using ResourceManager.App.Application.Metrics;
using ResourceManager.App.Application.Overlay;
using ResourceManager.App.Application.Monitoring;
using ResourceManager.App.Application.NetworkTelemetry;
using ResourceManager.App.Application.ResourceBreakdown;
using ResourceManager.App.Application.ResourceTable;
using ResourceManager.App.Application.SoftwareDiscovery;
using ResourceManager.App.Application.Adaptation;
using ResourceManager.App.Application.RuntimeSpecialization;
using ResourceManager.App.Application.SystemHealth;
using ResourceManager.App.Infrastructure.Control;
using ResourceManager.App.Infrastructure.Control.Writers;
using ResourceManager.App.Infrastructure.DiskUsage;
using ResourceManager.App.Infrastructure.CpuTopology;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Infrastructure.Monitoring.GpuTelemetry;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.NetworkTelemetry;
using ResourceManager.App.Infrastructure.ResourceBreakdown;
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
            new NativePdhCollector(provider.GetRequiredService<HostManagerPdhCollectorRuntime>(),
                provider.GetRequiredService<Infrastructure.Adaptation.ResourceManagerLedgerOwner>()));
        services.AddMonitoringSourceZone<WindowsCpuMonitoringZone>();
        services.AddMonitoringSourceZone<WindowsMemoryMonitoringZone>();
        services.AddMonitoringSourceZone<WindowsVirtualMemoryMonitoringZone>();
        services.AddMonitoringSourceZone<WindowsGpuAdapterOrderMonitoringZone>();
        // 核显的传感路由：哪一块卡的频率/温度/电压走处理器的 SMU。
        // 启动时按 Windows 给的适配器类型认一次，之后不变。
        services.AddSingleton<AmdIntegratedGpuSensorRoute>();
        // 频率这条要知道这颗芯片的加速上限 —— Windows 给不出来，只能问 SMU。
        // 它是芯片的固有属性，只问一次。
        services.AddKernelLeakObservation();
        services.AddSingleton<CpuMaxBoostFrequency>();
        services.AddSingleton<PdhCpuFrequencyMonitoringZone>(static provider =>
            new PdhCpuFrequencyMonitoringZone(
                provider.GetRequiredService<CpuMaxBoostFrequency>()));
        services.AddSingleton<PdhGpuEngineMonitoringZone>(static provider =>
            new PdhGpuEngineMonitoringZone(provider.GetRequiredService<NativePdhCollector>()));
        services.AddSingleton<PdhSystemIoMonitoringZone>(static provider =>
            new PdhSystemIoMonitoringZone(provider.GetRequiredService<NativePdhCollector>()));
        services.AddMonitoringSourceZone<NvidiaNvmlMonitoringZone>();
        services.AddMonitoringSourceZone<NvidiaNvapiMonitoringZone>();
        services.AddMonitoringSourceZone<AmdAdlxMonitoringZone>();
        services.AddMonitoringSourceZone<AmdSmuMonitoringZone>();
        services.AddMonitoringSourceZone<IntelMsrMonitoringZone>();
        services.AddMonitoringSourceZone<IntelGclMonitoringZone>();
        services.AddMonitoringSourceZone<WindowsStorageSensorsMonitoringZone>();
        services.AddMonitoringSourceZone<WindowsBatteryMonitoringZone>();
        services.AddMonitoringSourceZone<WindowsNpuMemoryMonitoringZone>();
        services.AddMonitoringSourceZone<WindowsNpuEngineMonitoringZone>();
        services.AddMonitoringSourceZone<HardwareMonitorWmiMonitoringZone>();
        services.AddMonitoringSourceZone<NotebookOemFanMonitoringZone>();
        services.AddMonitoringSourceZone<IpHelperNetworkMonitoringZone>();
        services.AddMonitoringSourceZone<EtwNetworkTcpIpMonitoringZone>();
        services.AddSingleton<MonitoringSourceZoneRegistry>(static provider => new MonitoringSourceZoneRegistry(GetMonitoringSourceZones(provider)));
        services.AddSingleton<IMonitoringSourceZoneRegistry>(static provider => provider.GetRequiredService<MonitoringSourceZoneRegistry>());
        services.AddSingleton<GpuTelemetryLocalProbe>();
        services.AddSingleton<GpuTelemetryWorkerSupervisor>(static provider => new(
            provider.GetRequiredService<GpuTelemetryLocalProbe>(),
            provider.GetRequiredService<HostManagerSamplingSubscriptionOwner>(),
            provider.GetRequiredService<ILogger<GpuTelemetryWorkerSupervisor>>(),
            provider.GetRequiredService<Infrastructure.Adaptation.ResourceManagerLedgerOwner>()));
        services.AddSingleton<IGpuTelemetryWorkerClient>(static provider => provider.GetRequiredService<GpuTelemetryWorkerSupervisor>());
        services.AddSingleton<IGpuTelemetryPushSource>(static provider => provider.GetRequiredService<GpuTelemetryWorkerSupervisor>());
        services.AddHostedServiceAlias<GpuTelemetryWorkerSupervisor>(BlockUids.GpuTelemetryWorkerSupervisor, static service => service.CaptureSamplingWorkerTask(),
            forwardRunFailureToHost: false);

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
            provider.GetRequiredService<AmdIntegratedGpuSensorRoute>(),
            provider.GetRequiredService<WindowsPlatformSensorReader>(),
            provider.GetRequiredService<HostManagerMetricSnapshotOwner>(),
            provider.GetRequiredService<HardwareMetricCatalogState>(),

            provider.GetRequiredService<HostManagerSamplingSubscriptionOwner>(),
            provider.GetRequiredService<RuntimePlanProvider>(),
            provider.GetRequiredService<ILogger<WindowsHardwareMetricSampler>>(),
            provider.GetRequiredService<Infrastructure.Adaptation.ResourceManagerLedgerOwner>(),
            provider.GetRequiredService<WindowsBatteryMonitoringZone>(),
            provider.GetRequiredService<IntelMsrMonitoringZone>(),
            provider.GetRequiredService<IpHelperNetworkMonitoringZone>(),
            provider.GetRequiredService<IntelGclMonitoringZone>(),
            new ResourceManager.ReadWrite.Windows.Metrics.Writes.WindowsMetricControlResources
            {
                // Resolve only the resources consumed by capture.
                IntelCpuControl = provider.GetRequiredService<IntelCpuControlWriter>().Resource,
                AmdCpuControl = provider.GetRequiredService<AmdCpuControlWriter>().Resource,
                AmdDesktopGpuControl = provider.GetRequiredService<AmdDesktopGpuControlWriter>().Resource,
                IntegratedGpuControl = provider.GetRequiredService<IntegratedGpuControlWriter>().Resource,
                ControlResolveObjectId = (snapshot, family, resource, _, adapterKey) =>
                    provider.GetRequiredService<IControlObjectCatalog>() is WindowsControlObjectCatalog originalCatalog
                        ? Infrastructure.ReadWrite.HostReadWriteBackend.ResolveOriginalControlObjectId(
                            originalCatalog.ReadDeclaredObjects(snapshot, resource), family, resource, adapterKey) : null,
                ControlIsFixedChassis = () => provider.GetRequiredService<WindowsChassisKindReader>().Read()
                    == Domain.Control.ControlChassisKinds.Fixed
            }, provider.GetRequiredService<WindowsNpuMemoryMonitoringZone>(), provider.GetRequiredService<WindowsNpuEngineMonitoringZone>()));
        services.AddSingleton<IMetricSampler>(static provider => provider.GetRequiredService<WindowsHardwareMetricSampler>());
        services.AddSingleton<IMetricSnapshotObservationSource>(static provider => provider.GetRequiredService<WindowsHardwareMetricSampler>());
        services.AddSingleton<IMetricSnapshotPushSource>(static provider => provider.GetRequiredService<WindowsHardwareMetricSampler>());
        services.AddSingleton<Infrastructure.ReadWrite.HostReadWriteBackend>();
        services.AddSingleton<Infrastructure.ReadWrite.HostFieldSubscriptionBackend>();
        services.AddSingleton<Infrastructure.ReadWrite.HostProcessFieldBackend>();
        services.AddSingleton<Infrastructure.ReadWrite.HostPresentFrameFieldQueries>();
        services.AddSingleton<Infrastructure.ReadWrite.HostFieldBackends>();
        services.AddHttpClient("read-write-dependencies");
        services.AddSingleton<ResourceManager.ReadWrite.Dependencies.IReadWriteDependencies>(provider =>
            Infrastructure.ReadWrite.HostReadWriteDependencies.Create(
                provider.GetRequiredService<IHttpClientFactory>().CreateClient("read-write-dependencies"),
                provider.GetRequiredService<Application.Components.IProviderRuntimeProbe>(),
                Infrastructure.Paths.PackagePathResolver.ResolvePackageRoot(provider.GetRequiredService<IHostEnvironment>().ContentRootPath),
                provider.GetRequiredService<HardwareBridgeClient>(),
                provider.GetRequiredService<Application.Management.IManagementInventoryRevisionOwner>(),
                componentFilesChanged: provider.GetRequiredService<Infrastructure.Dependencies.OptionalDependencyProbeResults>().Invalidate));
        services.AddSingleton<ResourceManager.ReadWrite.ReadWriteCore>(provider => new(
            provider.GetRequiredService<Infrastructure.ReadWrite.HostFieldBackends>().Reads,
            provider.GetRequiredService<ResourceManager.ReadWrite.Dependencies.IReadWriteDependencies>()));
        services.AddSingleton<ResourceManager.Subscription.IFieldSubscriptionBackend>(provider =>
            provider.GetRequiredService<Infrastructure.ReadWrite.HostFieldBackends>().Subscriptions);
        services.AddSingleton<ResourceManager.Subscription.FieldSubscriptionCore>();
        services.AddHostedServiceAlias<WindowsHardwareMetricSampler>(BlockUids.WindowsHardwareMetricSampler, static service => service.CaptureSamplingWorkerTask(),
            forwardRunFailureToHost: false);
        services.AddSingleton<ICpuCorePerformanceOverrideStore, JsonCpuCorePerformanceOverrideStore>();
        services.AddSingleton<ICpuTopologySampler, WindowsCpuTopologyReader>();
        services.AddSingleton<CpuTopologySnapshotProvider>(static provider => new(
            provider.GetRequiredService<ICpuTopologySampler>(),
            provider.GetRequiredService<HostManagerSamplingSubscriptionOwner>(),
            provider.GetRequiredService<ILogger<CpuTopologySnapshotProvider>>(),
            provider.GetRequiredService<Infrastructure.Adaptation.ResourceManagerLedgerOwner>()));
        services.AddSingleton<ICpuTopologyReader>(static provider => provider.GetRequiredService<CpuTopologySnapshotProvider>());
        services.AddHostedServiceAlias<CpuTopologySnapshotProvider>(BlockUids.CpuTopologySnapshotProvider, static service => service.ExecuteTask,
            forwardRunFailureToHost: false);

        // 磁盘占用：卷清单只读，随叫随取。
        services.AddSingleton<IDiskUsageVolumeCatalog, WindowsDiskUsageVolumeCatalog>();
        services.AddSingleton<Infrastructure.Adaptation.ResourceManagerLedgerOwner>();
        services.AddSingleton<IDiskUsageTreePayloadAllocator, Infrastructure.Adaptation.LedgerDiskUsageTreePayloadAllocator>();
        services.AddSingleton<DiskUsageTreeStore>(static provider =>
            new DiskUsageTreeStore(provider.GetRequiredService<IDiskUsageTreePayloadAllocator>()));
        services.AddSingleton<IDiskUsageTreeStore>(static provider => provider.GetRequiredService<DiskUsageTreeStore>());
        services.AddSingleton<IOwnedDiskUsageTreeStore>(static provider => provider.GetRequiredService<DiskUsageTreeStore>());
        // 控制写入器。注册在这里是因为目录要问它们"这一项能不能调" ——
        // 探测本身只读（问驱动要范围），真正的写入要等调用方明确下达。
        services.AddSingleton<IControlWriter, NvidiaGpuControlWriter>();
        // 笔记本的显卡功率预算走厂商固件的 ACPI WMI 接口，不是显卡接口。
        services.AddSingleton<IControlWriter, UniwillGpuPowerWriter>();
        // 处理器和核显的写入都经过辅助进程：它链接 GPL 的 ZenStates-Core，
        // 单独一个程序、单独一个许可证，主程序只跟它说话。
        services.AddSingleton<HardwareBridgeClient>();
        services.AddSingleton<HardwareCommandQueue>(provider => new(
            provider.GetRequiredService<ResourceManager.Shared.BusinessBlocks.State.StateAuthority>(),
            provider.GetRequiredService<HardwareBridgeClient>().Owner));
        services.AddSingleton(new Infrastructure.RuntimeSpecialization.BusinessBlocks.BusinessBlockRegistration(
            BlockUids.HardwareBridgeProcessOwner, provider => new HardwareBridgeRunningBlock(
                provider.GetRequiredService<HardwareBridgeClient>(), provider.GetRequiredService<HardwareCommandQueue>())));
        services.AddSingleton<AmdCpuControlWriter>();
        services.AddSingleton<IControlWriter>(static provider => provider.GetRequiredService<AmdCpuControlWriter>());
        services.AddSingleton<IntelCpuControlWriter>();
        services.AddSingleton<IControlWriter>(static provider => provider.GetRequiredService<IntelCpuControlWriter>());
        services.AddSingleton<AmdDesktopGpuControlWriter>();
        services.AddSingleton<IControlWriter>(static provider => provider.GetRequiredService<AmdDesktopGpuControlWriter>());
        // 核显和独显不是一条路（AMD 走 SMU、Intel 走显卡驱动的控制库），按接法分流。
        services.AddSingleton<IntegratedGpuControlWriter>();
        services.AddSingleton<IControlWriter>(static provider => provider.GetRequiredService<IntegratedGpuControlWriter>());
        // 风扇由后端内的风扇控制核心处理，按控制通道分类而不按品牌；
        // 而且自带一条规矩：退出时把风扇交还固件。
        services.AddSingleton<FanControlCoreClient>();
        services.AddBusinessBlockLifetime<FanControlCoreLifetimeService>(BlockUids.FanControlCoreLifetimeService);
        // Host starts aliases serially. The feed can become ready only after its parent core runs;
        // reverse host shutdown also gives the feed its stop turn before the core waits for it.
        if (startupCapabilities.Allows(StartupCapability.RuntimeEffectOwners))
            services.AddHostedServiceAlias<FanCurveTemperatureFeed>(static service => service.ExecuteTask, forwardRunFailureToHost: false);
        services.AddSingleton<IControlWriter, FanControlWriter>();
        // 控制对象目录：只读，从已有的监控快照里认对象，不新开采集。
        // 机箱形态读一次就够，所以做成单例 —— 见 WindowsChassisKindReader。
        services.AddSingleton<WindowsChassisKindReader>();
        /*
         * 「重新检测」要清的那些缓存。
         *
         * 都注册到同一个接口上，端点遍历一遍就够 —— 以后谁记了探测缓存，
         * 只要实现这个接口就自动被清到，不用回头改端点。
         */
        services.AddSingleton<IControlDetectionCache>(
            provider => provider.GetRequiredService<WindowsChassisKindReader>());
        services.AddSingleton<IControlDetectionCache>(
            provider => provider.GetRequiredService<FanControlCoreClient>());
        services.AddSingleton<IControlDetectionCache>(
            provider => provider.GetServices<IControlWriter>()
                .OfType<NvidiaGpuControlWriter>()
                .First());
        services.AddSingleton<IControlObjectCatalog, WindowsControlObjectCatalog>();
        // 快速扫描读主文件表，全扫描逐级遍历。进来的请求由前者按模式分派，
        // 不匹配就把请求原样交给后者 —— 模式是用户选的，程序不替他改。
        services.AddSingleton<DirectoryWalkDiskUsageScanner>();
        services.AddSingleton<IDiskUsageScanner>(static provider =>
            new WindowsMftDiskUsageScanner(
                provider.GetRequiredService<IDiskUsageVolumeCatalog>(),
                provider.GetRequiredService<DirectoryWalkDiskUsageScanner>()));

        services.AddSingleton<KernelEtwConsumerThreadDiagnostics>();
        services.AddSingleton<KernelEtwSessionBroker>(provider =>
        {
            var logger = provider.GetRequiredService<ILogger<KernelEtwSessionBroker>>();
            var regions = new NativeKernelEtwLedgerRegionProvider(
                provider.GetRequiredService<Infrastructure.Adaptation.ResourceManagerLedgerOwner>());
            var factory = new NativeKernelEtwSessionRuntimeFactory(regions, () =>
            {
                var plan = provider.GetRequiredService<IRuntimePlanProvider>().Current.HostManager.RequirePublished();
                return KernelEtwNativeSettings.CreateConfiguration(
                    plan.KernelEtwLimits ?? throw new InvalidOperationException("Kernel ETW limits are unpublished."),
                    plan.CpuCoreResidency,
                    checked((uint)plan.CpuCoreResidency.PhysicalCoreByLogicalProcessor.Count));
            }, () => KernelEtwNativeSettings.CreateSessionBuffers(
                provider.GetRequiredService<IRuntimePlanProvider>().Current.HostManager.RequirePublished().KernelEtwLimits
                    ?? throw new InvalidOperationException("Kernel ETW limits are unpublished.")),
            sessionName => ProcessOwnedEtwSessionOrphanReconciler.CreateDefault(logger).Reconcile(
                ResourceManagerEtwSessionNames.KernelPrefix, ResourceManagerEtwSessionNames.CurrentOwner, sessionName),
                provider.GetRequiredService<KernelEtwConsumerThreadDiagnostics>());
            return new KernelEtwSessionBroker(factory, logger);
        });
        services.AddSingleton<IKernelEtwSessionBroker>(static provider => provider.GetRequiredService<KernelEtwSessionBroker>());

        services.AddSingleton<PortableSoftwareDiscoveryService>();
        services.AddSingleton<IPortableSoftwareDiscovery>(static provider => provider.GetRequiredService<PortableSoftwareDiscoveryService>());
        if (startupCapabilities.Allows(StartupCapability.MutablePersistence))
        {
            services.AddHostedServiceAlias<PortableSoftwareDiscoveryService>(BlockUids.PortableSoftwareDiscoveryService,
                static service => service.ExecutionTask, forwardRunFailureToHost: false,
                resourceFailureHandled: static service => service.CatalogFailureHandled);
        }
        else
        {
            // The explicit discovery API still publishes diagnostics in a read-only host.
            services.AddSingleton(new Infrastructure.RuntimeSpecialization.BusinessBlocks.BusinessBlockDiagnosticRegistration(BlockUids.PortableSoftwareDiscoveryService));
        }

        services.AddSingleton<EtwCpuCoreResidencyReader>(static provider => new(
            provider.GetRequiredService<IKernelEtwSessionBroker>(),
            provider.GetRequiredService<IRuntimePlanProvider>(),
            provider.GetRequiredService<HostManagerSamplingSubscriptionOwner>(),
            provider.GetRequiredService<ILogger<EtwCpuCoreResidencyReader>>(),
            provider.GetRequiredService<Infrastructure.Adaptation.ResourceManagerLedgerOwner>()));
        services.AddSingleton<ICpuCoreResidencyReader>(static provider => provider.GetRequiredService<EtwCpuCoreResidencyReader>());
        services.AddSingleton<IResourceTableProviderStateSource>(static provider => provider.GetRequiredService<EtwCpuCoreResidencyReader>());
        services.AddHostedServiceAlias<EtwCpuCoreResidencyReader>(BlockUids.EtwCpuCoreResidencyReader, static service => service.ExecuteTask,
            forwardRunFailureToHost: false);

        services.AddSingleton<EtwSystemInterruptSnapshotSource>(static provider => new(
            provider.GetRequiredService<IKernelEtwSessionBroker>(),
            provider.GetRequiredService<HostManagerSamplingSubscriptionOwner>(),
            provider.GetRequiredService<ILogger<EtwSystemInterruptSnapshotSource>>(),
            provider.GetRequiredService<Infrastructure.Adaptation.ResourceManagerLedgerOwner>()));
        services.AddSingleton<ISystemInterruptSnapshotSource>(static provider => provider.GetRequiredService<EtwSystemInterruptSnapshotSource>());
        services.AddHostedServiceAlias<EtwSystemInterruptSnapshotSource>(BlockUids.EtwSystemInterruptSnapshotSource, static service => service.ExecuteTask,
            forwardRunFailureToHost: false);

        services.AddSingleton<EtwPhysicalDiskIoAttributionReader>(static provider => new(
            provider.GetRequiredService<IKernelEtwSessionBroker>(),
            provider.GetRequiredService<ILogger<EtwPhysicalDiskIoAttributionReader>>()));
        services.AddSingleton<IPhysicalDiskIoAttributionReader>(static provider => provider.GetRequiredService<EtwPhysicalDiskIoAttributionReader>());
        services.AddSingleton<IResourceTableProviderStateSource>(static provider => provider.GetRequiredService<EtwPhysicalDiskIoAttributionReader>());
        services.AddHostedServiceAlias<EtwPhysicalDiskIoAttributionReader>(BlockUids.EtwPhysicalDiskIoAttributionReader, static service => service.ExecuteTask, forwardRunFailureToHost: false);

        // The three user-mode ETW owners read their declared buffer pools when they create a session.
        services.AddSingleton<DxgkrnlVidMmEtwTelemetryZone>(static provider => new(
            provider.GetRequiredService<ILogger<DxgkrnlVidMmEtwTelemetryZone>>(),
            provider.GetRequiredService<Infrastructure.RuntimeSpecialization.BusinessBlocks.BusinessBlockScheduler>(),
            provider.GetRequiredService<Infrastructure.RuntimeSpecialization.BusinessBlocks.BusinessBlockDiagnosticsRegistry>(),
            provider.GetRequiredService<NativeEtwSessionAvailability>(),
            () => ReadPublishedMonitoring(provider).VidMmSessionBuffers,
            provider.GetService<ResourceManager.ReadWrite.Windows.Processes.Etw.IEtwConsumerThreadObserver>()));
        services.AddSingleton<PresentFrameEtwSource>(static provider => new(
            provider.GetRequiredService<ILogger<PresentFrameEtwSource>>(),
            provider.GetRequiredService<Infrastructure.RuntimeSpecialization.BusinessBlocks.BusinessBlockScheduler>(),
            provider.GetRequiredService<Infrastructure.RuntimeSpecialization.BusinessBlocks.BusinessBlockDiagnosticsRegistry>(),
            provider.GetRequiredService<NativeEtwSessionAvailability>(),
            () => ReadPublishedMonitoring(provider).PresentSessionBuffers,
            () => ReadPublishedMonitoring(provider).FramePipeline,
            provider.GetService<ResourceManager.ReadWrite.Windows.Processes.Etw.IEtwConsumerThreadObserver>()));
        services.AddSingleton<IFrameTimingObservationSource>(static provider => provider.GetRequiredService<PresentFrameEtwSource>());
        services.AddSingleton<DxgkrnlVidMmEtwResidualBreakdownProvider>();
        services.AddSingleton<WindowsResourceResidualBreakdownProvider>();
        services.AddSingleton<IResourceResidualBreakdownProvider>(static provider => provider.GetRequiredService<WindowsResourceResidualBreakdownProvider>());
        services.AddSingleton<IResourceTableProviderStateSource>(static provider => provider.GetRequiredService<DxgkrnlVidMmEtwResidualBreakdownProvider>());

        services.AddSingleton<IKnownNetworkProxyCatalog, KnownNetworkProxyCatalog>();
        services.AddSingleton<EtwNetworkTcpIpAttributionReader>();
        services.AddSingleton<IEtwNetworkAttributionReader>(static provider => provider.GetRequiredService<EtwNetworkTcpIpAttributionReader>());
        services.AddSingleton<IResourceTableProviderStateSource>(static provider => provider.GetRequiredService<EtwNetworkTcpIpAttributionReader>());
        services.AddHostedServiceAlias<EtwNetworkTcpIpAttributionReader>(BlockUids.EtwNetworkTcpIpAttributionReader, static service => service.ExecuteTask, forwardRunFailureToHost: false);
        services.AddSingleton<IpHelperNetworkAttributionReader>();
        services.AddSingleton<INetworkAttributionReader>(static provider => provider.GetRequiredService<IpHelperNetworkAttributionReader>());
        services.AddSingleton<IResourceTableProviderStateSource>(static provider => provider.GetRequiredService<IpHelperNetworkAttributionReader>());

        services.AddSingleton<PdhProcessGpuReader>(static provider =>
            new PdhProcessGpuReader(provider.GetRequiredService<NativePdhCollector>()));
        services.AddSingleton<WindowsResourceBreakdownSampler>(static provider => new(
            provider.GetRequiredService<IMetricSampler>(),
            provider.GetRequiredService<IRuntimeProcessAttributionCatalogProvider>(),
            provider.GetRequiredService<IResourceResidualBreakdownProvider>(),
            provider.GetRequiredService<IPhysicalDiskIoAttributionReader>(),
            provider.GetRequiredService<INetworkAttributionReader>(),
            provider.GetRequiredService<IRuntimePlanProvider>(),
            provider.GetRequiredService<HostManagerSamplingSubscriptionOwner>(),
            provider.GetRequiredService<PdhProcessGpuReader>(),
            ledgerOwner: provider.GetRequiredService<Infrastructure.Adaptation.ResourceManagerLedgerOwner>(),
            logger: provider.GetService<ILogger<WindowsResourceBreakdownSampler>>(),
            gpuAllocations: provider.GetService<GpuAllocationCollector>(),
            interactiveDesktops: provider.GetService<InteractiveDesktopObservationStore>(),
            stateAuthority: provider.GetService<ResourceManager.Shared.BusinessBlocks.State.StateAuthority>()));
        services.AddSingleton<GpuAllocationCollector>(static provider => new(
            provider.GetRequiredService<ILogger<GpuAllocationCollector>>(),
            provider.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>(),
            provider.GetRequiredService<Infrastructure.RuntimeSpecialization.BusinessBlocks.BusinessBlockDiagnosticsRegistry>(),
            provider.GetRequiredService<NativeEtwSessionAvailability>(),
            () => ReadPublishedMonitoring(provider).GpuAllocationSessionBuffers,
            provider.GetService<ResourceManager.ReadWrite.Windows.Processes.Etw.IEtwConsumerThreadObserver>()));
        services.AddSingleton<IResourceBreakdownSampler>(static provider => provider.GetRequiredService<WindowsResourceBreakdownSampler>());
        services.AddSingleton<IResourceBreakdownObservationSource>(static provider => provider.GetRequiredService<WindowsResourceBreakdownSampler>());
        services.AddSingleton<IResourceBreakdownPushSource>(static provider => provider.GetRequiredService<WindowsResourceBreakdownSampler>());
        services.AddSingleton<ISchedulingProcessFactSource>(static provider => provider.GetRequiredService<WindowsResourceBreakdownSampler>());
        services.AddSingleton<ISchedulingProcessFactObservationSource>(static provider => provider.GetRequiredService<WindowsResourceBreakdownSampler>());
        services.AddSingleton<ResourceManager.App.Application.ProcessAttribution.IRuntimeSoftwareObservationSource>(static provider => provider.GetRequiredService<WindowsResourceBreakdownSampler>());
        services.AddHostedServiceAlias<WindowsResourceBreakdownSampler>(BlockUids.WindowsResourceBreakdownSampler, static service => service.CaptureSamplingWorkerTask(),
            forwardRunFailureToHost: false);
        return services;
    }

    private static Domain.RuntimeSpecialization.CompiledMonitoringSettings ReadPublishedMonitoring(IServiceProvider provider)
        => provider.GetRequiredService<IRuntimePlanProvider>().Current.HostManager.RequirePublished().Monitoring
            ?? throw new InvalidOperationException("Monitoring settings are unpublished.");

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
            provider.GetRequiredService<IntelMsrMonitoringZone>(),
            provider.GetRequiredService<IntelGclMonitoringZone>(),
            provider.GetRequiredService<WindowsStorageSensorsMonitoringZone>(),
            provider.GetRequiredService<WindowsBatteryMonitoringZone>(),
            provider.GetRequiredService<WindowsNpuMemoryMonitoringZone>(),
            provider.GetRequiredService<WindowsNpuEngineMonitoringZone>(),
            provider.GetRequiredService<HardwareMonitorWmiMonitoringZone>(),
            provider.GetRequiredService<NotebookOemFanMonitoringZone>(),
            provider.GetRequiredService<IpHelperNetworkMonitoringZone>(),
            provider.GetRequiredService<EtwNetworkTcpIpMonitoringZone>()
        ];
    }
}
