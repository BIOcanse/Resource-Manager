using ResourceManager.App.Application.Monitoring;
using ResourceManager.App.Application.RuntimeSpecialization;
using ResourceManager.App.Domain.RuntimeSpecialization;

namespace ResourceManager.App.Infrastructure.Monitoring;

public sealed partial class WindowsStorageSensorsMonitoringZone : MonitoringSourceZone
{
    public WindowsStorageSensorsMonitoringZone()
        : base(MonitoringSourceZoneIds.WindowsStorageSensors)
    {
    }
}

public sealed partial class HardwareMonitorWmiMonitoringZone : MonitoringSourceZone
{
    public HardwareMonitorWmiMonitoringZone()
        : base(MonitoringSourceZoneIds.HardwareMonitorWmi)
    {
    }
}

public sealed class NotebookOemFanMonitoringZone : MonitoringSourceZone
{
    private readonly NotebookOemFanSensorReader reader = new();
    private readonly Func<CompiledFirmwareProviderPlan> getFirmwareProviderPlan;

    public NotebookOemFanMonitoringZone()
        : this(CompiledFirmwareProviderPlan.Default)
    {
    }

    public NotebookOemFanMonitoringZone(IRuntimePlanProvider runtimePlanProvider)
        : base(MonitoringSourceZoneIds.NotebookOemFan)
    {
        ArgumentNullException.ThrowIfNull(runtimePlanProvider);
        getFirmwareProviderPlan = () => runtimePlanProvider.Current.Monitoring.FirmwareProviders;
    }

    internal NotebookOemFanMonitoringZone(CompiledFirmwareProviderPlan firmwareProviderPlan)
        : base(MonitoringSourceZoneIds.NotebookOemFan)
    {
        getFirmwareProviderPlan = () => firmwareProviderPlan;
    }

    internal NotebookOemFanSensorSnapshot ReadSensors()
    {
        return CanRead ? reader.Read(getFirmwareProviderPlan()) : NotebookOemFanSensorReader.Frozen();
    }
}

public sealed class IpHelperNetworkMonitoringZone : MonitoringSourceZone
{
    public IpHelperNetworkMonitoringZone()
        : base(MonitoringSourceZoneIds.IpHelperNetwork)
    {
    }
}

public sealed class EtwNetworkTcpIpMonitoringZone : MonitoringSourceZone
{
    public EtwNetworkTcpIpMonitoringZone()
        : base(MonitoringSourceZoneIds.EtwNetworkTcpIp)
    {
    }
}
