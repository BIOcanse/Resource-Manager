using ResourceManager.App.Application.Monitoring;
using ResourceManager.ReadWrite.Windows.Metrics.Resources;
namespace ResourceManager.App.Infrastructure.Monitoring;
public sealed class PdhCpuFrequencyMonitoringZone : MonitoringSourceZone, IDisposable
{
    private readonly WindowsCpuFrequencyResource resource;
    public PdhCpuFrequencyMonitoringZone(CpuMaxBoostFrequency? maxBoost = null) : base(MonitoringSourceZoneIds.PdhCpuFrequency)
        => resource = new(maxBoost is null ? null : () => maxBoost.Megahertz);
    internal CpuFrequency ReadFrequency() => CanRead ? resource.ReadFrequency() : new(0, 0, 0, CpuFrequencySourceStates.Frozen);
    public void Dispose() => resource.Dispose();
}
