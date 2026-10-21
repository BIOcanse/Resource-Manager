using ResourceManager.App.Application.Monitoring;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.ReadWrite.Vendors.Nvidia;

namespace ResourceManager.App.Infrastructure.Monitoring;

public sealed class NvidiaNvmlMonitoringZone : MonitoringSourceZone
{
    private readonly Func<NvidiaNvmlReadRequest, WindowsGpuAdapterInventoryRead?, CancellationToken,
        Task<IReadOnlyList<GpuMetrics>>> read;
    private readonly Func<WindowsGpuAdapterInventoryRead,IReadOnlyList<DeviceTopologyNvidiaGpuInformation>?>? readDeclarations;
    private readonly Func<WindowsGpuAdapterInventoryRead, IReadOnlyList<NvidiaNvmlFanIdentity>?>? readFanIdentities;
    private readonly Func<IReadOnlyList<NvidiaNvmlFanReadTarget>, WindowsGpuAdapterInventoryRead, CancellationToken,
        Task<IReadOnlyList<NvidiaNvmlFanReading>>>? readFans;

    public NvidiaNvmlMonitoringZone()
        : this(new NvidiaNvmlReader(GpuProviderMessages.Describe))
    {
    }

    private NvidiaNvmlMonitoringZone(NvidiaNvmlReader reader)
        : this(reader.ReadAsync, reader.ReadDeclarations, reader.ReadFanIdentities, reader.ReadFansAsync) { }

    internal NvidiaNvmlMonitoringZone(Func<NvidiaNvmlReadRequest, WindowsGpuAdapterInventoryRead?, CancellationToken,
        Task<IReadOnlyList<GpuMetrics>>> read,
        Func<WindowsGpuAdapterInventoryRead,IReadOnlyList<DeviceTopologyNvidiaGpuInformation>?>? readDeclarations = null,
        Func<WindowsGpuAdapterInventoryRead, IReadOnlyList<NvidiaNvmlFanIdentity>?>? readFanIdentities = null,
        Func<IReadOnlyList<NvidiaNvmlFanReadTarget>, WindowsGpuAdapterInventoryRead, CancellationToken,
            Task<IReadOnlyList<NvidiaNvmlFanReading>>>? readFans = null)
        : base(MonitoringSourceZoneIds.VendorNvidiaNvml)
    {
        this.read = read ?? throw new ArgumentNullException(nameof(read));
        this.readDeclarations = readDeclarations;
        this.readFanIdentities = readFanIdentities;
        this.readFans = readFans;
    }

    internal IReadOnlyList<DeviceTopologyNvidiaGpuInformation>? ReadDeclarations(WindowsGpuAdapterInventoryRead inventory)
        => CanRead ? readDeclarations?.Invoke(inventory) : null;

    internal IReadOnlyList<NvidiaNvmlFanIdentity>? ReadFanIdentities(WindowsGpuAdapterInventoryRead inventory)
        => CanRead ? readFanIdentities?.Invoke(inventory) : null;

    internal Task<IReadOnlyList<NvidiaNvmlFanReading>> ReadFansAsync(IReadOnlyList<NvidiaNvmlFanReadTarget> targets,
        WindowsGpuAdapterInventoryRead inventory, CancellationToken cancellationToken)
        => CanRead && readFans is not null ? readFans(targets, inventory, cancellationToken)
            : Task.FromResult<IReadOnlyList<NvidiaNvmlFanReading>>([]);

    internal Task<IReadOnlyList<GpuMetrics>> ReadAsync(
        NvidiaNvmlReadRequest request,
        WindowsGpuAdapterInventoryRead windowsInventory,
        CancellationToken cancellationToken)
    {
        return CanRead
            ? read(request, windowsInventory, cancellationToken)
            : Task.FromResult<IReadOnlyList<GpuMetrics>>([]);
    }
}
