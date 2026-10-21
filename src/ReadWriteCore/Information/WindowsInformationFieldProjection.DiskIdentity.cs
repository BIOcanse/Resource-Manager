using ResourceManager.ReadWrite.Windows.Devices.Models;
using ResourceManager.ReadWrite.Windows.Devices;

namespace ResourceManager.ReadWrite.Information;

public static partial class WindowsInformationFieldProjection
{
    internal const int MaximumPhysicalDiskRelationCandidates = 4096;
    public static IReadOnlyDictionary<(StorageIdentifyProtocol Protocol, string Serial), DeviceTopologyStorageInformation[]>?
        PhysicalDiskRelationCandidates(IReadOnlyList<DeviceTopologyStorageInformation>? information)
    {
        if (information is null || information.Count > MaximumPhysicalDiskRelationCandidates) return null;
        return information.GroupBy(static item => item.DeviceIdentity, StringComparer.Ordinal)
            .Where(static group => group.Count() == 1).Select(static group => group.Single())
            .Where(static item => CurrentPhysicalIdentity(item))
            .GroupBy(static item => (item.Protocol, item.HardwareIdentityEvidence!.SerialNumber.Trim().ToUpperInvariant()))
            .ToDictionary(static group => group.Key, static group => group.ToArray());
    }

    private static bool CurrentPhysicalIdentity(DeviceTopologyStorageInformation item)
    {
        if (item.HardwareIdentityEvidence is not { } identity || string.IsNullOrWhiteSpace(identity.SerialNumber)) return false;
        var actual = item.Protocol switch
        {
            StorageIdentifyProtocol.Ata => item.Ata?.SerialNumber,
            StorageIdentifyProtocol.NvmeController => item.Nvme?.SerialNumber,
            _ => null
        };
        return !string.IsNullOrWhiteSpace(actual) && string.Equals(actual.Trim(), identity.SerialNumber.Trim(), StringComparison.OrdinalIgnoreCase)
            && item.DeviceIdentity == WindowsStorageDeviceIdentity.Create(identity.UniqueId, identity.SerialNumber);
    }

    private static string? PhysicalDiskRelation(DeviceTopologyDiskLayoutInformation disk,
        IReadOnlyDictionary<(StorageIdentifyProtocol Protocol, string Serial), DeviceTopologyStorageInformation[]>? candidates)
        => PhysicalDiskRelation(disk.HardwareIdentification, candidates);

    internal static string? PhysicalDiskRelation(DiskHardwareIdentification? actual,
        IReadOnlyDictionary<(StorageIdentifyProtocol Protocol, string Serial), DeviceTopologyStorageInformation[]>? candidates)
    {
        if (actual is null || candidates is null || string.IsNullOrWhiteSpace(actual.SerialNumber)
            || !candidates.TryGetValue((actual.Protocol, actual.SerialNumber.Trim().ToUpperInvariant()), out var matches)
            || matches.Length != 1) return null;
        var item = matches[0]; var identity = item.HardwareIdentityEvidence!;
        if (!string.IsNullOrWhiteSpace(identity.UniqueId)
            && (identity.UniqueIdFormat is not (0 or 1 or 2 or 3 or 8) || actual.DeviceIdentifiers is null
                || !WindowsAtaSmartReader.MatchesUniqueId(actual.DeviceIdentifiers, identity.UniqueId, identity.UniqueIdFormat.Value))) return null;
        return item.DeviceIdentity;
    }
}
