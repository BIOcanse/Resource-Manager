using System.Globalization;
using ResourceManager.ReadWrite.Windows.Devices.Models;
using F = ResourceManager.ReadWrite.Information.DeviceInformationFieldRegistry;

namespace ResourceManager.ReadWrite.Information;

public static partial class WindowsInformationFieldProjection
{
    public static DeviceInformationSnapshot CreateAcpi(
        IReadOnlyList<AcpiFirmwareTable> tables, DateTimeOffset observedAt)
    {
        var devices = tables.Select(table => new DeviceInformationObject(
            "windows-acpi|first-by-signature|" + table.SignatureId.ToString("X8", CultureInfo.InvariantCulture),
            "firmware_table", Array.AsReadOnly(new DeviceInformationField[] {
                Value(F.FirmwareAllAcpiTableSignature, table.Signature),
                Integer(F.FirmwareAllAcpiSignatureInstanceCountReported, table.ReportedInstanceCount),
                Value(F.FirmwareAllAcpiTableRaw, table.FirstTable?.Raw),
                Integer(F.FirmwareAllAcpiTableRevision, table.FirstTable?.Revision),
                Integer(F.FirmwareAllAcpiTableLengthDeclaredBytes, (ulong?)table.FirstTable?.Raw.Length),
                Value(F.FirmwareAllAcpiOemIdDeclared, table.FirstTable?.OemId),
                Value(F.FirmwareAllAcpiOemTableIdDeclared, table.FirstTable?.OemTableId),
                Integer(F.FirmwareAllAcpiOemRevisionDeclared, table.FirstTable?.OemRevision),
                Value(F.FirmwareAllAcpiCreatorIdDeclared, table.FirstTable?.CreatorId),
                Integer(F.FirmwareAllAcpiCreatorRevisionDeclared, table.FirstTable?.CreatorRevision)
            }))).OrderBy(static device => device.DeviceIdentity, StringComparer.Ordinal).ToArray();
        return new(observedAt, Array.AsReadOnly(devices));
    }

    public static DeviceInformationSnapshot ClearAcpiValues(DeviceInformationSnapshot current)
        => new(null, Array.AsReadOnly(current.Devices.Select(device => device with {
            Fields = Array.AsReadOnly(device.Fields.Select(field => field with { Value = null }).ToArray())
        }).ToArray()));
}
