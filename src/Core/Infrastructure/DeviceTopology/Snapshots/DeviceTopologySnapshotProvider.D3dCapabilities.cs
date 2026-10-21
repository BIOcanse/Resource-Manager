using System.Runtime.Versioning;
using ResourceManager.ReadWrite.Windows.Devices.Gpu;

namespace ResourceManager.App.Infrastructure.DeviceTopology.Snapshots;

public sealed partial class DeviceTopologySnapshotProvider
{
    /// <summary>Creates an explicit D3D capability view over this provider's existing capture owner.</summary>
    [SupportedOSPlatform("windows")]
    public WindowsD3dCapabilityAccess CreateD3dCapabilityInformation(WindowsD3dCapabilityScope scope)
        => captureOwner.CreateD3dCapabilityInformation(scope);

    /// <summary>Creates an explicit D3D capability view using a caller-supplied source and this provider's existing owner.</summary>
    [SupportedOSPlatform("windows")]
    public WindowsD3dCapabilityAccess CreateD3dCapabilityInformation(
        WindowsD3dCapabilityScope scope, IWindowsD3dCapabilityReader reader)
        => captureOwner.CreateD3dCapabilityInformation(scope, reader);
}
