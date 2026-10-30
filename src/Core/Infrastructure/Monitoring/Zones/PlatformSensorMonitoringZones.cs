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

public sealed partial class HardwareMonitorWmiMonitoringZone : MonitoringSourceZone, IDisposable
{
    private readonly object gate = new();
    private readonly ResourceManager.ReadWrite.Windows.Platform.HardwareMonitorCoreReader coreReader;
    public void Dispose()
    {
        lock (gate) coreReader.Dispose();
    }
    public HardwareMonitorWmiMonitoringZone()
        : this(new ResourceManager.ReadWrite.Windows.Platform.HardwareMonitorCoreReader()) { }
    internal HardwareMonitorWmiMonitoringZone(ResourceManager.ReadWrite.Windows.Platform.HardwareMonitorCoreReader coreReader)
        : base(MonitoringSourceZoneIds.HardwareMonitorWmi)
    {
        this.coreReader = coreReader ?? throw new ArgumentNullException(nameof(coreReader));
    }

}

public sealed class NotebookOemFanMonitoringZone : MonitoringSourceZone, IDisposable
{
    private readonly object gate = new();
    private readonly NotebookOemFanSensorReader reader;
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
        reader = new NotebookOemFanSensorReader();
    }

    internal NotebookOemFanMonitoringZone(CompiledFirmwareProviderPlan firmwareProviderPlan)
        : this(() => firmwareProviderPlan, new NotebookOemFanSensorReader()) { }

    internal NotebookOemFanMonitoringZone(Func<CompiledFirmwareProviderPlan> getFirmwareProviderPlan,
        NotebookOemFanSensorReader reader)
        : base(MonitoringSourceZoneIds.NotebookOemFan)
    {
        this.getFirmwareProviderPlan = getFirmwareProviderPlan ?? throw new ArgumentNullException(nameof(getFirmwareProviderPlan));
        this.reader = reader ?? throw new ArgumentNullException(nameof(reader));
    }

    internal NotebookOemFanSensorSnapshot ReadSensors()
    {
        lock (gate)
            return reader.Read(getFirmwareProviderPlan());
    }

    internal void SynchronizeDemand(bool hasDemand)
    {
        lock (gate)
        {
            if (!hasDemand
                || getFirmwareProviderPlan().NotebookOemFanProvider != NotebookOemFanProviderKind.MechrevoUwAcpi)
                reader.ReleaseSession();
        }
    }

    public void Dispose()
    {
        lock (gate) reader.Dispose();
    }
}

public sealed partial class IpHelperNetworkMonitoringZone : MonitoringSourceZone
{
    public IpHelperNetworkMonitoringZone()
        : this(new ResourceManager.ReadWrite.Windows.Devices.WindowsNetworkInterfaceCounterApi())
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
