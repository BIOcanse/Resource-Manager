using System.Globalization;

namespace ResourceManager.App.Infrastructure.DeviceTopology;

internal static class DeviceIdDatabaseParser
{
    public static DeviceIdDatabase Parse(IEnumerable<string> lines, DeviceIdDatabaseKind kind)
    {
        var vendors = new Dictionary<ushort, string>();
        var devices = new Dictionary<uint, string>();
        var subsystems = new Dictionary<ulong, string>();
        ushort? currentVendor = null;
        ushort? currentDevice = null;
        var version = "unknown";

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd();
            if (line.StartsWith("# Version:", StringComparison.OrdinalIgnoreCase))
            {
                version = line[(line.IndexOf(':') + 1)..].Trim();
                continue;
            }

            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            if (line.StartsWith("\t\t", StringComparison.Ordinal))
            {
                if (kind == DeviceIdDatabaseKind.Pci
                    && currentVendor is not null
                    && currentDevice is not null
                    && TryParseSubsystem(line[2..], out var subVendor, out var subDevice, out var subsystemName))
                {
                    subsystems[CreateSubsystemKey(
                        currentVendor.Value,
                        currentDevice.Value,
                        subVendor,
                        subDevice)] = subsystemName;
                }

                continue;
            }

            if (line[0] == '\t')
            {
                if (currentVendor is not null
                    && TryParseNamedId(line[1..], out var deviceId, out var deviceName))
                {
                    devices[CreateDeviceKey(currentVendor.Value, deviceId)] = deviceName;
                    currentDevice = deviceId;
                }

                continue;
            }

            currentDevice = null;
            if (TryParseNamedId(line, out var vendorId, out var vendorName))
            {
                vendors[vendorId] = vendorName;
                currentVendor = vendorId;
            }
            else
            {
                currentVendor = null;
            }
        }

        return new DeviceIdDatabase(kind, version, vendors, devices, subsystems);
    }

    private static bool TryParseNamedId(string value, out ushort id, out string name)
    {
        id = 0;
        name = string.Empty;
        var parts = value.Split(
            [' ', '\t'],
            2,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2
            || parts[0].Length != 4
            || !ushort.TryParse(parts[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out id)
            || string.IsNullOrWhiteSpace(parts[1]))
        {
            return false;
        }

        name = parts[1];
        return true;
    }

    private static bool TryParseSubsystem(
        string value,
        out ushort subVendor,
        out ushort subDevice,
        out string name)
    {
        subVendor = 0;
        subDevice = 0;
        name = string.Empty;
        var parts = value.Split(
            [' ', '\t'],
            3,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 3
            || parts[0].Length != 4
            || parts[1].Length != 4
            || !ushort.TryParse(parts[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out subVendor)
            || !ushort.TryParse(parts[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out subDevice)
            || string.IsNullOrWhiteSpace(parts[2]))
        {
            return false;
        }

        name = parts[2];
        return true;
    }

    internal static uint CreateDeviceKey(ushort vendorId, ushort deviceId)
    {
        return ((uint)vendorId << 16) | deviceId;
    }

    internal static ulong CreateSubsystemKey(
        ushort vendorId,
        ushort deviceId,
        ushort subVendorId,
        ushort subDeviceId)
    {
        return ((ulong)vendorId << 48)
            | ((ulong)deviceId << 32)
            | ((ulong)subVendorId << 16)
            | subDeviceId;
    }
}

internal enum DeviceIdDatabaseKind
{
    Usb,
    Pci
}

internal sealed record DeviceIdDatabase(
    DeviceIdDatabaseKind Kind,
    string Version,
    IReadOnlyDictionary<ushort, string> Vendors,
    IReadOnlyDictionary<uint, string> Devices,
    IReadOnlyDictionary<ulong, string> Subsystems);
