using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.ReadWrite.Windows.Devices.Models;

namespace ResourceManager.App.Infrastructure.DeviceTopology.Pci;

public static class PciDeviceIdentificationReader
{
    public static PciDeviceIdentification? ReadForDevice(string deviceId, DeviceTopologyNativeDevice? native)
        => deviceId.StartsWith("PCI\\", StringComparison.OrdinalIgnoreCase)
            && native is not null && string.Equals(deviceId, native.DeviceId, StringComparison.OrdinalIgnoreCase)
                ? Read(native.HardwareIds, native.CompatibleIds) : null;

    private static readonly HashSet<string> HardwareFormats = new(StringComparer.Ordinal) {
        "VEN,DEV,SUBSYS,REV", "VEN,DEV,SUBSYS", "VEN,DEV,REV", "VEN,DEV", "VEN,DEV,CC"
    };
    private static readonly HashSet<string> CompatibleFormats = new(StringComparer.Ordinal) {
        "VEN,DEV,REV", "VEN,DEV", "VEN,CC", "VEN", "CC,DT", "CC",
        "VEN,DEV,REV,CC", "VEN,DEV,CC"
    };

    public static PciDeviceIdentification Read(IReadOnlyList<string> hardwareIds, IReadOnlyList<string> compatibleIds)
    {
        if (hardwareIds.Count > 64 || compatibleIds.Count > 64
            || hardwareIds.Any(static id => id is null || id.Length > 128)
            || compatibleIds.Any(static id => id is null || id.Length > 128)) return PciDeviceIdentification.Empty;
        var hardware = hardwareIds.Select(static id => Parse(id, false)).OfType<Identifier>().ToArray();
        var pairs = hardware.Select(static id => (id.Vendor, id.Device)).Distinct().ToArray();
        if (pairs.Length != 1) return PciDeviceIdentification.Empty;
        var vendor = pairs[0].Vendor;
        var device = pairs[0].Device;
        var compatible = compatibleIds.Select(static id => Parse(id, true)).OfType<Identifier>().ToArray();
        if (compatible.Any(id => id.Vendor is not null && id.Vendor != vendor
            || id.Device is not null && id.Device != device)) return PciDeviceIdentification.Empty;
        var revision = Unique(hardware.Concat(compatible).Select(static id => id.Revision));
        var subsystem = Unique(hardware.Select(static id => id.Subsystem));
        var classes = hardware.Concat(compatible).Where(static id => id.Class is not null)
            .Select(static id => id.Class!).Distinct(StringComparer.Ordinal).ToArray();
        // Four-character CC is a less-specific declaration, never a zero PI.
        var classPrefixes = classes.Select(static code => code[..4]).Distinct(StringComparer.Ordinal).ToArray();
        var classCode = classPrefixes.Length == 1 ? classPrefixes[0] : null;
        var programmingInterface = classCode is null ? null
            : Unique(classes.Where(static code => code.Length == 6).Select(static code => code[4..]));
        return new(vendor, device, revision, subsystem?[..4], subsystem?[4..],
            classCode?[..2], classCode?[2..], programmingInterface);
    }

    private static string? Unique(IEnumerable<string?> values)
    {
        var unique = values.Where(static value => value is not null).Distinct(StringComparer.Ordinal).ToArray();
        return unique.Length == 1 ? unique[0] : null;
    }

    private static Identifier? Parse(string value, bool compatible)
    {
        if (!value.StartsWith("PCI\\", StringComparison.OrdinalIgnoreCase)) return null;
        var parts = value[4..].ToUpperInvariant().Split('&');
        if (parts.Length is < 1 or > 4) return null;
        var keys = new string[parts.Length];
        var data = new string[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            var separator = parts[i].IndexOf('_');
            if (separator <= 0) return null;
            keys[i] = parts[i][..separator]; data[i] = parts[i][(separator + 1)..];
            var validLength = keys[i] switch {
                "VEN" or "DEV" or "DT" => data[i].Length == 4,
                "SUBSYS" => data[i].Length == 8,
                "REV" => data[i].Length == 2,
                "CC" => data[i].Length is 4 or 6,
                _ => false
            };
            if (!validLength || data[i].Any(static c => c is not (>= '0' and <= '9' or >= 'A' and <= 'F'))) return null;
        }
        if (!(compatible ? CompatibleFormats : HardwareFormats).Contains(string.Join(',', keys))) return null;
        string? vendor = null, device = null, revision = null, subsystem = null, classCode = null;
        for (var i = 0; i < keys.Length; i++)
        {
            switch (keys[i])
            {
                case "VEN": vendor = data[i]; break;
                case "DEV": device = data[i]; break;
                case "REV": revision = data[i]; break;
                case "SUBSYS": subsystem = data[i]; break;
                case "CC": classCode = data[i]; break;
            }
        }
        if (vendor is "0000" or "FFFF") return null;
        if (subsystem is not null && subsystem[4..] is "0000" or "FFFF") subsystem = null;
        return new(vendor, device, revision, subsystem, classCode);
    }

    private sealed record Identifier(string? Vendor, string? Device, string? Revision, string? Subsystem, string? Class);
}
