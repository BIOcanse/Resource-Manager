using System.Globalization;
using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.ReadWrite.Windows.Gpu;

namespace ResourceManager.ReadWrite.Information;

public static class WindowsGpuInformationProjection
{
    public static IReadOnlyList<DeviceTopologyGpuInformation>? Create(WindowsGpuAdapterInventoryRead inventory)
    {
        if (inventory.Status != SamplingObservationStatus.Current || inventory.Generation == 0
            || inventory.ObservedCount != inventory.Adapters.Count || inventory.SkippedCount != 0 || inventory.OverflowCount != 0)
            return null;
        var devices = new Dictionary<string, DeviceTopologyGpuInformation>(StringComparer.Ordinal);
        var luids = new HashSet<ulong>();
        foreach (var adapter in inventory.Adapters)
        {
            if (adapter.IsSoftware) continue;
            var luid = WindowsGpuAdapterIdentity.Pack(adapter.Luid);
            if (luid == 0 || !luids.Add(luid) || adapter.PhysicalIdentityStatus != SamplingObservationStatus.Current
                || adapter.PhysicalIdentities.Count == 0) return null;
            foreach (var physical in adapter.PhysicalIdentities)
            {
                if (physical.Status is not (WindowsGpuPhysicalIdentityStatus.Bound or WindowsGpuPhysicalIdentityStatus.NonPci)
                    || string.IsNullOrWhiteSpace(physical.PnpInstanceId)) return null;
                var pnp = physical.PnpInstanceId.Trim().ToUpperInvariant();
                var pci = physical.PnpKind == WindowsGpuPnpKind.Pci && physical.Status == WindowsGpuPhysicalIdentityStatus.Bound;
                var information = new DeviceTopologyGpuInformation(pnp, luid.ToString("X16", CultureInfo.InvariantCulture),
                    adapter.PhysicalIdentities.Count == 1 ? adapter.Name : null,
                    pci && physical.VendorId <= ushort.MaxValue ? physical.VendorId.ToString("X4", CultureInfo.InvariantCulture) : null,
                    pci && physical.DeviceId <= ushort.MaxValue ? physical.DeviceId.ToString("X4", CultureInfo.InvariantCulture) : null,
                    physical.DriverVersion);
                if (!devices.TryAdd(pnp, information)) return null;
            }
        }
        return devices.Values.ToArray();
    }
}
