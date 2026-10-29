using ResourceManager.App.Application.Monitoring;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Adaptation;
using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.ReadWrite.Vendors.Nvidia;

namespace ResourceManager.App.Infrastructure.Monitoring;

public sealed class NvidiaNvmlMonitoringZone : MonitoringSourceZone, IDisposable
{
    private readonly object gate = new();
    private readonly NvidiaNvmlReader? ownedReader;
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

    internal NvidiaNvmlMonitoringZone(NvidiaNvmlReader reader)
        : this(reader.ReadAsync, reader.ReadDeclarations, reader.ReadFanIdentities, reader.ReadFansAsync)
        => ownedReader = reader;

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
    {
        lock (gate) return CanRead ? readDeclarations?.Invoke(inventory) : null;
    }

    internal IReadOnlyList<NvidiaNvmlFanIdentity>? ReadFanIdentities(WindowsGpuAdapterInventoryRead inventory)
    {
        lock (gate) return CanRead ? readFanIdentities?.Invoke(inventory) : null;
    }

    internal Task<IReadOnlyList<NvidiaNvmlFanReading>> ReadFansAsync(IReadOnlyList<NvidiaNvmlFanReadTarget> targets,
        WindowsGpuAdapterInventoryRead inventory, CancellationToken cancellationToken)
    {
        lock (gate)
            return CanRead && readFans is not null ? readFans(targets, inventory, cancellationToken)
                : Task.FromResult<IReadOnlyList<NvidiaNvmlFanReading>>([]);
    }

    internal Task<IReadOnlyList<GpuMetrics>> ReadAsync(
        NvidiaNvmlReadRequest request,
        WindowsGpuAdapterInventoryRead windowsInventory,
        CancellationToken cancellationToken)
    {
        lock (gate)
            return CanRead ? read(request, windowsInventory, cancellationToken)
                : Task.FromResult<IReadOnlyList<GpuMetrics>>([]);
    }

    public override void ApplyMode(ResourceManagerComputeZoneMode mode)
    {
        lock (gate)
        {
            base.ApplyMode(mode);
            if (mode == ResourceManagerComputeZoneMode.Freeze) ownedReader?.ReleaseSession();
        }
    }

    public void Dispose()
    {
        lock (gate) ownedReader?.Dispose();
    }
}
