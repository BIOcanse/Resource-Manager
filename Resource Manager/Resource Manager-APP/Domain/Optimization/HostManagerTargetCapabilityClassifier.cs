using ResourceManager.App.Domain.ProcessAttribution;
using ResourceManager.App.Domain.Software;

namespace ResourceManager.App.Domain.Optimization;

public static class HostManagerTargetCapabilityClassifier
{
    public static bool CanDispatchAdapter(string softwareId, string softwareKind)
    {
        if (IsNonTargetIdentity(softwareId))
        {
            return false;
        }
        return softwareKind == SoftwareKinds.Adapted;
    }

    public static bool CanWriteProcessPolicy(string softwareId, string softwareKind)
    {
        if (string.IsNullOrWhiteSpace(softwareId)
            || softwareId.Equals(RuntimeAttributionIds.ResourceManagerSelf, StringComparison.OrdinalIgnoreCase)
            || IsNonTargetIdentity(softwareId))
        {
            return false;
        }

        return softwareKind switch
        {
            SoftwareKinds.Adapted => false,
            SoftwareKinds.WindowsSystem or SoftwareKinds.WindowsComponent or SoftwareKinds.WindowsService => false,
            SoftwareKinds.RuntimePackage or SoftwareKinds.RuntimeProduct or SoftwareKinds.RuntimeRoot => false,
            SoftwareKinds.Unattributed => false,
            _ => true
        };
    }

    public static bool CanWritePhysicalPlacement(string softwareId, string softwareKind)
    {
        if (string.IsNullOrWhiteSpace(softwareId)
            || softwareId.Equals(RuntimeAttributionIds.ResourceManagerSelf, StringComparison.OrdinalIgnoreCase)
            || IsNonTargetIdentity(softwareId))
        {
            return false;
        }

        return softwareKind switch
        {
            SoftwareKinds.Adapted => true,
            SoftwareKinds.WindowsSystem or SoftwareKinds.WindowsComponent or SoftwareKinds.WindowsService => false,
            SoftwareKinds.RuntimePackage or SoftwareKinds.RuntimeProduct or SoftwareKinds.RuntimeRoot => false,
            SoftwareKinds.Unattributed => false,
            _ => true
        };
    }

    private static bool IsNonTargetIdentity(string softwareId)
        => string.IsNullOrWhiteSpace(softwareId)
            || softwareId.Equals(RuntimeAttributionIds.WindowsSystem, StringComparison.OrdinalIgnoreCase)
            || softwareId.Equals(RuntimeAttributionIds.WindowsComponent, StringComparison.OrdinalIgnoreCase)
            || softwareId.Equals(RuntimeAttributionIds.Unattributed, StringComparison.OrdinalIgnoreCase);
}
