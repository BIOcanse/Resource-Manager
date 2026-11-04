using ResourceManager.App.Application.Monitoring;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.ReadWrite.Windows.Metrics.Resources;
using ResourceManager.ReadWrite.Windows.Gpu;
namespace ResourceManager.App.Infrastructure.Monitoring;
public sealed class WindowsGpuAdapterOrderMonitoringZone : MonitoringSourceZone
{
    private readonly WindowsGpuInventoryOwner owner;
    public WindowsGpuAdapterOrderMonitoringZone() : this(new WindowsGpuAdapterOrderReader().ReadInventory) { }
    internal WindowsGpuAdapterOrderMonitoringZone(Func<WindowsGpuAdapterInventoryRead> read) : base(MonitoringSourceZoneIds.WindowsGpuAdapterOrder)
        => owner = new(read);
    internal WindowsGpuAdapterInventoryRead ReadInventory(bool requested) => owner.ReadInventory(requested);
    internal WindowsGpuAdapterInventoryRead ReadCurrentInventory() => owner.ReadCurrentInventory();
    internal Task WaitForInventoryChangeAsync(CancellationToken token) => owner.WaitForInventoryChangeAsync(token);
}
