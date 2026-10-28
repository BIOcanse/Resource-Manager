using ResourceManager.App.StateItems;

namespace Resource_Manager_APP.Tests;

/// <summary>Independent embedded expectations for the three production startup profiles.</summary>
internal static class BackendExpectedProductionTree
{
    internal static readonly int[] Wrappers =
    [
        BlockUids.Backend, BlockUids.ServiceHost, BlockUids.Monitoring, BlockUids.HardwareControl,
        BlockUids.Optimization, BlockUids.Reports, BlockUids.SoftwareManagement,
        BlockUids.TrustedAccess, BlockUids.PublicServices
    ];

    internal static IReadOnlyDictionary<int, int> Parents(uint profile)
    {
        if (profile is not (1 or 2 or 3)) throw new ArgumentOutOfRangeException(nameof(profile));
        var parents = new Dictionary<int, int>();
        void Group(int parent, params int[] children)
        {
            foreach (var child in children) parents.Add(child, parent);
        }

        Group(BlockUids.Program, BlockUids.Backend);
        Group(BlockUids.Backend, BlockUids.ServiceHost, BlockUids.Monitoring, BlockUids.HardwareControl,
            BlockUids.TrustedAccess, BlockUids.Reports);
        Group(BlockUids.Reports, BlockUids.KernelLeakObservation);
        Group(BlockUids.ServiceHost, BlockUids.RuntimeSpecializationCoordinator,
            BlockUids.ServiceAutoStartSettings, BlockUids.ServiceRecoverySettings);
        Group(BlockUids.Monitoring, BlockUids.HostManagerSamplingSubscriptionOwner,
            BlockUids.NativeEtwLifetimeService, BlockUids.EtwPhysicalDiskIoAttributionReader, BlockUids.EtwNetworkTcpIpAttributionReader);
        Group(BlockUids.HostManagerSamplingSubscriptionOwner, BlockUids.GpuTelemetryWorkerSupervisor,
            BlockUids.WindowsHardwareMetricSampler, BlockUids.CpuTopologySnapshotProvider,
            BlockUids.EtwCpuCoreResidencyReader, BlockUids.EtwSystemInterruptSnapshotSource,
            BlockUids.WindowsResourceBreakdownSampler, BlockUids.DeviceTopologySnapshotProvider);
        Group(BlockUids.NativeEtwLifetimeService, BlockUids.PresentFrameEtwSource,
            BlockUids.GpuAllocationCollector, BlockUids.DxgkrnlVidMmEtwTelemetryZone);
        Group(BlockUids.HardwareControl, BlockUids.FanControlCoreLifetimeService);
        Group(BlockUids.TrustedAccess, BlockUids.TrustedAccessRequestOwner, BlockUids.TrustedAccessInvocationHostedService);
        if (profile != 3)
        {
            Group(BlockUids.Backend, BlockUids.SoftwareManagement, BlockUids.Optimization, BlockUids.HostMemoryProtection,
                BlockUids.PublicServices, BlockUids.HostManagerOperationCoordinatorOwner);
            Group(BlockUids.ServiceHost, BlockUids.JsonDebugDiagnosticLogWriter);
            Group(BlockUids.HardwareControl, BlockUids.ControlDesiredStateReassertion, BlockUids.ControlActualStateReader);
            Group(BlockUids.FanControlCoreLifetimeService, BlockUids.FanCurveTemperatureFeed);
            Group(BlockUids.Optimization, BlockUids.HostManagerSmartCoordinator, BlockUids.GpuPlacementProcessHistoryObserver);
            Group(BlockUids.Reports, BlockUids.HostManagerReportCoordinatorOwner);
            Group(BlockUids.SoftwareManagement, BlockUids.SqlitePortableSoftwareRegistry, BlockUids.PortableSoftwareDiscoveryService);
            Group(BlockUids.PublicServices, BlockUids.HostManagerPublicServiceCoordinatorOwner,
                BlockUids.SharedResourceSubscriptionMaintenanceService);
        }
        if (profile == 2) Group(BlockUids.ServiceHost, BlockUids.AutomaticProductUpdateService);
        return parents;
    }
}
