using Microsoft.Extensions.DependencyInjection.Extensions;
using ResourceManager.App.Application.RuntimeSpecialization;
using ResourceManager.App.Application.RuntimeSpecialization.BusinessBlocks;
using ResourceManager.App.Infrastructure.RuntimeSpecialization.BusinessBlocks;
using ResourceManager.ReadWrite.Windows.Processes.Etw;

namespace ResourceManager.App.Hosting;

public static partial class ResourceManagerServiceCollectionExtensions
{
    // Policy can be overridden through BusinessBlocks in host configuration or Configure<BusinessBlockOptions>.
    internal static IServiceCollection AddBusinessBlockController(this IServiceCollection services)
    {
        if (services.Any(value => value.ServiceType == typeof(BusinessBlockController))) return services;
        services.AddOptions<BusinessBlockOptions>().BindConfiguration(BusinessBlockOptions.SectionName);
        services.TryAddSingleton<BusinessBlockController>();
        services.TryAddSingleton<IBusinessBlockQuery>(provider => provider.GetRequiredService<BusinessBlockController>());
        services.TryAddSingleton<IBusinessBlockDiagnostics>(provider => provider.GetRequiredService<BusinessBlockController>());
        services.TryAddSingleton<IEtwConsumerThreadObserver, BusinessBlockEtwThreadObserver>();
        return services;
    }

    internal static IServiceCollection AddBusinessBlockHostedService<TService>(this IServiceCollection services)
        where TService : class, IHostedService
    {
        services.TryAddSingleton<TService>();
        return services.AddHostedServiceAlias<TService>(constructService: true);
    }

    internal static IServiceCollection AddBusinessBlockHostedService<TService>(this IServiceCollection services, Func<IServiceProvider, TService> factory)
        where TService : class, IHostedService
    {
        services.TryAddSingleton(factory);
        return services.AddHostedServiceAlias<TService>(constructService: true);
    }

    internal static void RegisterHostedBusinessBlock<TService>(IServiceCollection services)
        where TService : class, IHostedService
    {
        services.AddBusinessBlockController();
        var id = typeof(TService).Name;
        for (var index = 0; index < services.Count; index++)
            if (services[index].ImplementationInstance is BusinessBlockRegistration existing && existing.Id == id)
            {
                services[index] = ServiceDescriptor.Singleton(existing with { HostedService = true });
                return;
            }
        string[] dependencies = [];
        services.AddSingleton(new BusinessBlockRegistration(id, dependencies,
            provider => new HostedBusinessBlock(() => provider.GetRequiredService<TService>())) { HostedService = true });
    }

    private static IServiceCollection AddObservedEtwBusinessBlocks(this IServiceCollection services)
    {
        services.AddSingleton(new BusinessBlockRegistration("present-etw", [],
            _ => throw new InvalidOperationException("Present activation remains inside the legacy producer in step 1.")) { ObservationOnly = true });
        services.AddSingleton(new BusinessBlockRegistration("gpu-allocation-etw", [],
            _ => throw new InvalidOperationException("GPU allocation activation remains inside the legacy producer in step 1.")) { ObservationOnly = true });
        // This method is the complete production composition, after all capability-gated declarations.
        // Standalone aliases intentionally declare no production prerequisites.
        for (var index = 0; index < services.Count; index++)
        {
            if (services[index].ImplementationInstance is not BusinessBlockRegistration registration) continue;
            string[] prerequisites = registration.Id switch
            {
                "RuntimeSpecializationCoordinator" or "present-etw" or "gpu-allocation-etw" => [],
                "WindowsHardwareMetricSampler" or "GpuTelemetryWorkerSupervisor" or "CpuTopologySnapshotProvider" or "DeviceTopologySnapshotProvider"
                    => ["HostManagerSamplingSubscriptionOwner"],
                "WindowsResourceBreakdownSampler" => ["WindowsHardwareMetricSampler", "HostManagerSamplingSubscriptionOwner", "KernelEtwSessionBroker"],
                "DashboardMonitoringPlanSynchronizer" => ["WindowsHardwareMetricSampler"],
                "PortableSoftwareDiscoveryService" => ["KernelEtwSessionBroker", "SqlitePortableSoftwareRegistry"],
                "EtwCpuCoreResidencyReader" or "EtwSystemInterruptSnapshotSource" => ["KernelEtwSessionBroker", "HostManagerSamplingSubscriptionOwner"],
                "EtwPhysicalDiskIoAttributionReader" or "EtwNetworkTcpIpAttributionReader" => ["KernelEtwSessionBroker"],
                "HostManagerSmartCoordinator" or "HostManagerReportCoordinatorOwner" => ["WindowsResourceBreakdownSampler"],
                "SharedResourceSubscriptionMaintenanceService" => ["SharedMemoryPublicResourceBroker"],
                "SqliteLegacyDataImportHostedService" => ["ResourceManagerDatabaseInitializer", "SqlitePortableSoftwareRegistry"],
                _ => []
            };
            if (registration.Id is not ("RuntimeSpecializationCoordinator" or "present-etw" or "gpu-allocation-etw"))
                prerequisites = ["RuntimeSpecializationCoordinator", .. prerequisites];
            services[index] = ServiceDescriptor.Singleton(registration with { Dependencies = prerequisites });
        }
        // The discovery API can also be explicitly invoked in a read-only host without its hosted worker.
        if (!services.Any(value => value.ImplementationInstance is BusinessBlockRegistration { Id: "PortableSoftwareDiscoveryService" }))
            services.AddSingleton(new BusinessBlockRegistration("PortableSoftwareDiscoveryService", [],
                _ => throw new InvalidOperationException("The discovery worker is disabled by startup configuration.")) { ObservationOnly = true });
        return services;
    }
}

internal sealed class BusinessBlockEtwThreadObserver(BusinessBlockController controller) : IEtwConsumerThreadObserver
{
    public IDisposable RegisterCurrentThread(string blockId, string sessionName)
        => controller.RegisterConsumerThread(Map(blockId), sessionName);

    public void ReportFailure(string blockId, Exception error)
        => controller.ReportFailure(Map(blockId), error, "etw-consumer");

    private static string Map(string blockId) => blockId switch
        {
            "kernel-etw" => "KernelEtwSessionBroker",
            "vidmm-etw" => "DxgkrnlVidMmEtwTelemetryZone",
            "present-etw" => "present-etw",
            "gpu-allocation-etw" => "gpu-allocation-etw",
            _ => throw new ArgumentOutOfRangeException(nameof(blockId), blockId, "Undeclared ETW business block.")
        };
}
