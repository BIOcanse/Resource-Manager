using System.Globalization;
using System.Text;
using ResourceManager.App.Domain.DeviceTopology;

namespace ResourceManager.App.Infrastructure.DeviceTopology;

public sealed class DeviceIdCatalog
{
    private readonly Lazy<DeviceIdCatalogSnapshot> snapshot;

    public DeviceIdCatalog(IHostEnvironment environment)
    {
        var roots = ResolveRoots(environment.ContentRootPath);
        snapshot = new Lazy<DeviceIdCatalogSnapshot>(
            () => LoadSnapshot(roots),
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    internal DeviceTopologyIdResolution? ResolveUsb(ushort vendorId, ushort productId)
    {
        if (vendorId == 0)
        {
            return null;
        }

        return Resolve(
            snapshot.Value.Usb,
            "usb.ids",
            vendorId,
            productId,
            null,
            null);
    }

    internal DeviceTopologyIdResolution? ResolveHardwareIds(IReadOnlyList<string> hardwareIds)
    {
        foreach (var hardwareId in hardwareIds)
        {
            if (TryReadHexPart(hardwareId, "VID_", 4, out var usbVendor)
                && TryReadHexPart(hardwareId, "PID_", 4, out var usbProduct))
            {
                return ResolveUsb((ushort)usbVendor, (ushort)usbProduct);
            }

            if (!TryReadHexPart(hardwareId, "VEN_", 4, out var pciVendor)
                || !TryReadHexPart(hardwareId, "DEV_", 4, out var pciDevice))
            {
                continue;
            }

            ushort? subVendor = null;
            ushort? subDevice = null;
            if (TryReadHexPart(hardwareId, "SUBSYS_", 8, out var subsystem))
            {
                subDevice = (ushort)(subsystem >> 16);
                subVendor = (ushort)(subsystem & 0xFFFF);
            }

            return Resolve(
                snapshot.Value.Pci,
                "pci.ids",
                (ushort)pciVendor,
                (ushort)pciDevice,
                subVendor,
                subDevice);
        }

        return null;
    }

    private static DeviceTopologyIdResolution? Resolve(
        DeviceIdDatabase? database,
        string databaseName,
        ushort vendorId,
        ushort deviceId,
        ushort? subVendorId,
        ushort? subDeviceId)
    {
        if (database is null)
        {
            return null;
        }

        database.Vendors.TryGetValue(vendorId, out var vendorName);
        database.Devices.TryGetValue(
            DeviceIdDatabaseParser.CreateDeviceKey(vendorId, deviceId),
            out var deviceName);
        string? subsystemName = null;
        if (subVendorId is not null && subDeviceId is not null)
        {
            database.Subsystems.TryGetValue(
                DeviceIdDatabaseParser.CreateSubsystemKey(
                    vendorId,
                    deviceId,
                    subVendorId.Value,
                    subDeviceId.Value),
                out subsystemName);
        }

        if (vendorName is null && deviceName is null && subsystemName is null)
        {
            return null;
        }

        return new DeviceTopologyIdResolution(
            databaseName,
            database.Version,
            vendorName,
            deviceName,
            subsystemName);
    }

    private static DeviceIdCatalogSnapshot LoadSnapshot(IReadOnlyList<string> roots)
    {
        var usbPath = FindFile(roots, "usb.ids");
        var pciPath = FindFile(roots, "pci.ids");
        return new DeviceIdCatalogSnapshot(
            LoadDatabase(usbPath, DeviceIdDatabaseKind.Usb, Encoding.Latin1),
            LoadDatabase(pciPath, DeviceIdDatabaseKind.Pci, new UTF8Encoding(false, true)));
    }

    private static DeviceIdDatabase? LoadDatabase(
        string? path,
        DeviceIdDatabaseKind kind,
        Encoding encoding)
    {
        if (path is null)
        {
            return null;
        }

        try
        {
            return DeviceIdDatabaseParser.Parse(File.ReadLines(path, encoding), kind);
        }
        catch (DecoderFallbackException) when (kind == DeviceIdDatabaseKind.Pci)
        {
            return DeviceIdDatabaseParser.Parse(File.ReadLines(path, Encoding.Latin1), kind);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? FindFile(IEnumerable<string> roots, string fileName)
    {
        return roots
            .Select(root => Path.Combine(root, fileName))
            .FirstOrDefault(File.Exists);
    }

    private static IReadOnlyList<string> ResolveRoots(string contentRootPath)
    {
        return new[]
        {
            Path.Combine(contentRootPath, "Infrastructure", "Resources", "DeviceIds"),
            Path.Combine(contentRootPath, "DeviceIds"),
            Path.Combine(AppContext.BaseDirectory, "DeviceIds"),
            Path.Combine(AppContext.BaseDirectory, "Infrastructure", "Resources", "DeviceIds")
        }
        .Select(Path.GetFullPath)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();
    }

    private static bool TryReadHexPart(string value, string marker, int digits, out uint result)
    {
        result = 0;
        var index = value.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0 || index + marker.Length + digits > value.Length)
        {
            return false;
        }

        return uint.TryParse(
            value.AsSpan(index + marker.Length, digits),
            NumberStyles.HexNumber,
            CultureInfo.InvariantCulture,
            out result);
    }

    private sealed record DeviceIdCatalogSnapshot(DeviceIdDatabase? Usb, DeviceIdDatabase? Pci);
}
