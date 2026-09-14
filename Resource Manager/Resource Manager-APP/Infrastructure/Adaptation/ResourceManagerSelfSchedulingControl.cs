using ResourceManager.App.Application.Adaptation;
using ResourceManager.App.Application.Monitoring;
using ResourceManager.App.Application.Optimization.SmartControl;

namespace ResourceManager.App.Infrastructure.Adaptation;

public sealed partial class ResourceManagerSelfSchedulingControl : IResourceManagerSelfSchedulingControl
{
    private readonly IMonitoringSourceZoneRegistry? monitoringSourceZones;
    private readonly IHostManagerSmartControlZoneRegistry? hostManagerSmartControlZones;
    private readonly IReadOnlyList<IResourceManagerSelfComputeZone> standaloneComputeZones;

    public ResourceManagerSelfSchedulingControl(
        IMonitoringSourceZoneRegistry? monitoringSourceZones = null,
        IHostManagerSmartControlZoneRegistry? hostManagerSmartControlZones = null,
        IEnumerable<IResourceManagerSelfComputeZone>? standaloneComputeZones = null)
    {
        this.monitoringSourceZones = monitoringSourceZones;
        this.hostManagerSmartControlZones = hostManagerSmartControlZones;
        this.standaloneComputeZones = standaloneComputeZones?.ToArray() ?? [];
    }
}
