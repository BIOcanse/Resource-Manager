using ResourceManager.App.Application.Monitoring;
using ResourceManager.ReadWrite.Windows.Metrics.Resources;
namespace ResourceManager.App.Infrastructure.Monitoring;
public sealed class PdhCpuFrequencyMonitoringZone : MonitoringSourceZone, IDisposable
{
    private readonly object gate = new();
    private readonly Func<CpuFrequency> read;
    private readonly Action release;
    public PdhCpuFrequencyMonitoringZone(CpuMaxBoostFrequency? maxBoost = null)
        : this(new WindowsCpuFrequencyResource(maxBoost is null ? null : () => maxBoost.Megahertz)) { }
    private PdhCpuFrequencyMonitoringZone(WindowsCpuFrequencyResource resource)
        : this(resource.ReadFrequency, resource.ReleaseSession) { }
    internal PdhCpuFrequencyMonitoringZone(Func<CpuFrequency> read, Action release)
        : base(MonitoringSourceZoneIds.PdhCpuFrequency)
        => (this.read, this.release) = (read, release);
    internal CpuFrequency ReadFrequency()
    {
        lock (gate) return read();
    }

    public void Dispose()
    {
        lock (gate) release();
    }
}
