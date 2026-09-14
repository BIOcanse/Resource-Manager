using ResourceManager.App.Domain.RuntimeSpecialization;

namespace ResourceManager.App.Infrastructure.Monitoring;

public sealed class FirmwareIdentityReader
{
    public FirmwareIdentitySnapshot Read()
    {
        var bios = FirstRow(PlatformSensorWmiUtilities.QueryObjects(
            @"root\CIMV2",
            "SELECT Manufacturer, SMBIOSBIOSVersion, Version FROM Win32_BIOS"));
        var system = FirstRow(PlatformSensorWmiUtilities.QueryObjects(
            @"root\CIMV2",
            "SELECT Manufacturer, Model FROM Win32_ComputerSystem"));
        var baseBoard = FirstRow(PlatformSensorWmiUtilities.QueryObjects(
            @"root\CIMV2",
            "SELECT Manufacturer, Product FROM Win32_BaseBoard"));

        return new FirmwareIdentitySnapshot(
            Clean(ReadString(bios, "Manufacturer")),
            Clean(ReadString(bios, "SMBIOSBIOSVersion") ?? ReadString(bios, "Version")),
            Clean(ReadString(system, "Manufacturer")),
            Clean(ReadString(system, "Model")),
            Clean(ReadString(baseBoard, "Manufacturer")),
            Clean(ReadString(baseBoard, "Product")));
    }

    private static IReadOnlyDictionary<string, object?>? FirstRow(QueryResult result)
    {
        return result.Rows.Count == 0 ? null : result.Rows[0];
    }

    private static string? ReadString(IReadOnlyDictionary<string, object?>? row, string property)
    {
        return row is null ? null : PlatformSensorWmiUtilities.ReadString(row, property);
    }

    private static string? Clean(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
