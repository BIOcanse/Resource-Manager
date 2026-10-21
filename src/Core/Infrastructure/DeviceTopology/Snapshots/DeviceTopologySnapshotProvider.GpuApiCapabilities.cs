using System.Runtime.Versioning;
using ResourceManager.ReadWrite.Windows.Devices.Gpu;

namespace ResourceManager.App.Infrastructure.DeviceTopology.Snapshots;

public sealed partial class DeviceTopologySnapshotProvider
{
    /// <summary>Creates a Vulkan capability view over this provider's existing topology capture owner.</summary>
    [SupportedOSPlatform("windows")]
    public WindowsVulkanCapabilityAccess CreateVulkanCapabilityInformation(WindowsVulkanCapabilityScope scope)
        => captureOwner.CreateVulkanCapabilityInformation(scope);

    /// <summary>Creates a Vulkan view using a caller-supplied source over this provider's existing owner.</summary>
    [SupportedOSPlatform("windows")]
    public WindowsVulkanCapabilityAccess CreateVulkanCapabilityInformation(
        WindowsVulkanCapabilityScope scope, IWindowsVulkanCapabilityReader reader)
        => captureOwner.CreateVulkanCapabilityInformation(scope, reader);

    /// <summary>Creates an OpenGL view using the caller's current-context executor and this provider's existing owner.</summary>
    [SupportedOSPlatform("windows")]
    public WindowsOpenGlCapabilityAccess CreateOpenGlCapabilityInformation(
        WindowsOpenGlCapabilityScope scope, IWindowsOpenGlCurrentContextExecutor contextExecutor)
        => captureOwner.CreateOpenGlCapabilityInformation(scope, contextExecutor);

    /// <summary>Creates an OpenGL view using a caller-supplied source over this provider's existing owner.</summary>
    [SupportedOSPlatform("windows")]
    public WindowsOpenGlCapabilityAccess CreateOpenGlCapabilityInformation(
        WindowsOpenGlCapabilityScope scope, IWindowsOpenGlCapabilityReader reader)
        => captureOwner.CreateOpenGlCapabilityInformation(scope, reader);
}
