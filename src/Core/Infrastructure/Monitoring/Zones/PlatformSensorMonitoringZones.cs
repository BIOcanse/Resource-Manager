using ResourceManager.App.Application.Monitoring;
using ResourceManager.App.Application.RuntimeSpecialization;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Domain.Adaptation;

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
            return CanRead ? reader.Read(getFirmwareProviderPlan()) : NotebookOemFanSensorReader.Frozen();
    }

    internal void SynchronizeDemand(bool hasDemand)
    {
        lock (gate)
        {
            if (!hasDemand || !CanRead
                || getFirmwareProviderPlan().NotebookOemFanProvider != NotebookOemFanProviderKind.MechrevoUwAcpi)
                reader.ReleaseSession();
        }
    }

    public override void ApplyMode(ResourceManagerComputeZoneMode mode)
    {
        lock (gate)
        {
            base.ApplyMode(mode);
            if (mode == ResourceManagerComputeZoneMode.Freeze) reader.ReleaseSession();
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
