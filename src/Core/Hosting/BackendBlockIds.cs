namespace ResourceManager.App.Hosting;

// Stable wire/configuration IDs; renaming a service class must not rename a block.
internal static class BackendBlockIds
{
    public const string NativeEtwLifetimeService = "NativeEtwLifetimeService";
    public const string TrustedAccessRequestOwner = "TrustedAccessRequestOwner";
    public const string TrustedAccessInvocationHostedService = "TrustedAccessInvocationHostedService";
    public const string AutomaticProductUpdateService = "AutomaticProductUpdateService";
    public const string ControlActualStateReader = "ControlActualStateReader";
    public const string ControlDesiredStateReassertion = "ControlDesiredStateReassertion";
    public const string CpuTopologySnapshotProvider = "CpuTopologySnapshotProvider";
    public const string DeviceTopologySnapshotProvider = "DeviceTopologySnapshotProvider";
    public const string EtwCpuCoreResidencyReader = "EtwCpuCoreResidencyReader";
    public const string EtwNetworkTcpIpAttributionReader = "EtwNetworkTcpIpAttributionReader";
    public const string EtwPhysicalDiskIoAttributionReader = "EtwPhysicalDiskIoAttributionReader";
    public const string EtwSystemInterruptSnapshotSource = "EtwSystemInterruptSnapshotSource";
    public const string FanControlCoreLifetimeService = "FanControlCoreLifetimeService";
    public const string FanCurveTemperatureFeed = "FanCurveTemperatureFeed";
    public const string GpuLaunchInterceptionReconciler = "GpuLaunchInterceptionReconciler";
    public const string GpuPlacementProcessHistoryObserver = "GpuPlacementProcessHistoryObserver";
    public const string GpuTelemetryWorkerSupervisor = "GpuTelemetryWorkerSupervisor";
    public const string HostManagerDisplayCoordinatorOwner = "HostManagerDisplayCoordinatorOwner";
    public const string HostManagerFileQueryOwner = "HostManagerFileQueryOwner";
    public const string HostManagerMetricSnapshotOwner = "HostManagerMetricSnapshotOwner";
    public const string HostManagerOperationCoordinatorOwner = "HostManagerOperationCoordinatorOwner";
    public const string HostManagerPublicServiceCoordinatorOwner = "HostManagerPublicServiceCoordinatorOwner";
    public const string HostManagerReportCoordinatorOwner = "HostManagerReportCoordinatorOwner";
    public const string HostManagerSamplingSubscriptionOwner = "HostManagerSamplingSubscriptionOwner";
    public const string HostManagerSmartCoordinator = "HostManagerSmartCoordinator";
    public const string HostManagerSoftwareIdentityOwner = "HostManagerSoftwareIdentityOwner";
    public const string JsonDebugDiagnosticLogWriter = "JsonDebugDiagnosticLogWriter";
    public const string KernelEtwSessionBroker = "KernelEtwSessionBroker";
    public const string PortableSoftwareDiscoveryService = "PortableSoftwareDiscoveryService";
    public const string RuntimeSpecializationCoordinator = "RuntimeSpecializationCoordinator";
    public const string ServiceAutoStartSettings = "ServiceAutoStartSettings";
    public const string ServiceRecoverySettings = "ServiceRecoverySettings";
    public const string SharedResourceSubscriptionMaintenanceService = "SharedResourceSubscriptionMaintenanceService";
    public const string SqlitePortableSoftwareRegistry = "SqlitePortableSoftwareRegistry";
    public const string WindowsHardwareMetricSampler = "WindowsHardwareMetricSampler";
    public const string WindowsResourceBreakdownSampler = "WindowsResourceBreakdownSampler";
}
