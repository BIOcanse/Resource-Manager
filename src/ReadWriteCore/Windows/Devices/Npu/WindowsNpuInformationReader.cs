using System.Runtime.InteropServices;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.ReadWrite.Windows.Devices.Models;
using ResourceManager.ReadWrite.Windows.Gpu;

namespace ResourceManager.ReadWrite.Windows.Devices.Npu;

/// <summary>Reads declared NPU hardware and binds it to the caller's same-capture PnP facts.
/// Construction starts no collection; the read releases all native resources before returning.</summary>
public sealed class WindowsNpuInformationReader
{
    internal const uint MaximumPhysicalAdapterCount = 64;
    private readonly IWindowsNpuDxCoreApi _api;
    private readonly Func<AdapterLuid, uint, uint, uint, WindowsGpuIdentityRead> _readIdentity;

    public WindowsNpuInformationReader()
        : this(new WindowsNpuDxCoreNativeApi(), new WindowsGpuIdentityBridge().Read) { }

    internal WindowsNpuInformationReader(IWindowsNpuDxCoreApi api,
        Func<AdapterLuid, uint, uint, uint, WindowsGpuIdentityRead> readIdentity)
    {
        _api = api;
        _readIdentity = readIdentity;
    }

    /// <returns>Null for an unavailable or incomplete directory; an empty list only after successful enumeration.</returns>
    public IReadOnlyList<DeviceTopologyNpuInformation>? Read(DeviceTopologyNativeSnapshot native)
    {
        ArgumentNullException.ThrowIfNull(native);
        if (native.Error is not null) return null;
        try
        {
            var adapters = WindowsNpuDxCoreEnumeration.Read(_api);
            if (adapters is null) return null;
            var devices = new Dictionary<string, DeviceTopologyNativeDevice>(StringComparer.OrdinalIgnoreCase);
            var conflicts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var device in native.Devices.Values)
            {
                if (string.IsNullOrWhiteSpace(device.DeviceId)) continue;
                var id = device.DeviceId.Trim();
                if (!devices.TryAdd(id, device)) conflicts.Add(id);
            }

            var result = new List<DeviceTopologyNpuInformation>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var adapter in adapters)
            {
                var read = _readIdentity(adapter.Luid, adapter.VendorId, adapter.DeviceId, MaximumPhysicalAdapterCount);
                if (read.Status != SamplingObservationStatus.Current || read.SkippedCount != 0
                    || read.Identities.Count == 0 || read.Identities.Count > MaximumPhysicalAdapterCount) return null;
                var physicalIndexes = new HashSet<uint>();
                foreach (var identity in read.Identities)
                {
                    if (identity.Status is not (WindowsGpuPhysicalIdentityStatus.Bound or WindowsGpuPhysicalIdentityStatus.NonPci)
                        || identity.PhysicalAdapterIndex >= read.Identities.Count
                        || !physicalIndexes.Add(identity.PhysicalAdapterIndex)
                        || string.IsNullOrWhiteSpace(identity.PnpInstanceId)) return null;
                    var id = identity.PnpInstanceId.Trim();
                    if (!seen.Add(id) || conflicts.Contains(id) || !devices.TryGetValue(id, out var device)) return null;
                    var driver = device.DriverInformation;
                    var version = driver is not null && string.Equals(driver.DeviceInstanceId.Trim(), id,
                        StringComparison.OrdinalIgnoreCase) ? driver.VersionDeclared : null;
                    var pnp = device.DeviceId.Trim().ToUpperInvariant();
                    result.Add(new DeviceTopologyNpuInformation("windows-npu|" + pnp, pnp,
                        string.IsNullOrWhiteSpace(device.FriendlyName) ? device.DeviceDescription : device.FriendlyName,
                        version, device.LocationInfo, device.LocationPathsAvailable ? device.LocationPaths.ToArray() : null));
                }
            }
            return result.OrderBy(static value => value.DeviceIdentity, StringComparer.OrdinalIgnoreCase).ToArray();
        }
        catch (Exception exception) when (exception is ExternalException or DllNotFoundException
            or EntryPointNotFoundException or BadImageFormatException or OverflowException)
        {
            return null;
        }
    }
}
