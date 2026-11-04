using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.App.Domain.Messages;
using ResourceManager.ReadWrite.Windows.Devices;

namespace ResourceManager.App.Infrastructure.DeviceTopology;

// Host adapter: one SDK cache owns the original complete WMI/DriveInfo capture.
internal static class WindowsStorageDeviceCapabilityReader
{
    private static readonly WindowsStorageOwner Owner = new(TimeSpan.FromSeconds(15), TimeProvider.System,
        hasMsft => BackendMessage.Create(BackendMessageDomains.DeviceTopology, hasMsft
            ? BackendMessageCodes.DeviceTopology.SourceMsftDiskAssociations
            : BackendMessageCodes.DeviceTopology.SourceWin32DiskAssociations));
    public static DeviceTopologyStorageSnapshot ReadSnapshot() => Owner.ReadSnapshot();
    public static DeviceTopologyStorageDevice? Resolve(
        DeviceTopologyStorageSnapshot snapshot,
        string deviceId,
        IReadOnlyDictionary<string, DeviceTopologyNativeDevice> nativeDevices)
    {
        var normalized = NormalizeDeviceId(deviceId);
        if (normalized.Length == 0)
        {
            return null;
        }

        var exact = snapshot.Devices.FirstOrDefault(binding =>
            NormalizeDeviceId(binding.PnpDeviceId).Equals(normalized, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
        {
            return exact.Device;
        }

        if (!CanReceiveRelatedStorage(normalized))
        {
            return null;
        }

        var matches = snapshot.Devices
            .Select(binding => new
            {
                Binding = binding,
                Distance = RelationshipDistance(normalized, NormalizeDeviceId(binding.PnpDeviceId), nativeDevices)
            })
            .Where(static candidate => candidate.Distance is >= 1 and <= 4)
            .OrderBy(static candidate => candidate.Distance)
            .ToArray();
        return matches.Length > 0
            && (matches.Length == 1 || matches[0].Distance < matches[1].Distance)
                ? matches[0].Binding.Device
                : null;
    }

    internal static bool CanReceiveRelatedStorage(string deviceId)
    {
        var normalized = NormalizeDeviceId(deviceId);
        return normalized.StartsWith("USB\\VID_", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("USBSTOR\\", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("UASPSTOR\\", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("1394\\", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("SBP2\\", StringComparison.OrdinalIgnoreCase);
    }

    internal static string? ReadReferenceDeviceId(string? reference) => WindowsStorageOwner.ReadReferenceDeviceId(reference);
    private static int? RelationshipDistance(
        string left,
        string right,
        IReadOnlyDictionary<string, DeviceTopologyNativeDevice> nativeDevices)
    {
        var distance = DistanceToAncestor(left, right, nativeDevices);
        var reverse = DistanceToAncestor(right, left, nativeDevices);
        return distance is null ? reverse : reverse is null ? distance : Math.Min(distance.Value, reverse.Value);
    }

    private static int? DistanceToAncestor(
        string descendant,
        string ancestor,
        IReadOnlyDictionary<string, DeviceTopologyNativeDevice> nativeDevices)
    {
        var current = descendant;
        for (var distance = 0; distance <= 8 && current.Length > 0; distance++)
        {
            if (current.Equals(ancestor, StringComparison.OrdinalIgnoreCase))
            {
                return distance;
            }

            if (!nativeDevices.TryGetValue(current, out var device) || string.IsNullOrWhiteSpace(device.ParentDeviceId))
            {
                break;
            }

            current = NormalizeDeviceId(device.ParentDeviceId);
        }

        return null;
    }

    private static string NormalizeDeviceId(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Trim().Trim('"').Replace('/', '\\').ToUpperInvariant();
    }

}
