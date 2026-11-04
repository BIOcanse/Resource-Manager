using System.Globalization;
using ResourceManager.ReadWrite.Windows.Devices.Models;
using F = ResourceManager.ReadWrite.Information.DeviceInformationFieldRegistry;

namespace ResourceManager.ReadWrite.Information;

public static partial class WindowsInformationFieldProjection
{
    public static IReadOnlyList<DeviceInformationField> PageFileFields(DeviceTopologyPageFileInformation file)
    {
        DeviceInformationField Bytes(DeviceInformationDefinition definition, ulong? pages)
            => Value(definition, pages is { } count && file.PageSizeBytes is > 0
                ? ((UInt128)count * file.PageSizeBytes.Value).ToString(CultureInfo.InvariantCulture) : null);
        return [
            Value(F.MemoryAllPagefilePathReported, file.PathReported),
            Bytes(F.MemoryAllPagefileCapacity, file.CapacityPages),
            Bytes(F.MemoryAllPagefileUsed, file.UsedPages),
            Bytes(F.MemoryAllPagefilePeakUsedReportedBytes, file.PeakUsedPages),
            Integer(F.MemoryAllPagefileCapacityReportedPages, file.CapacityPages),
            Integer(F.MemoryAllPagefileUsedReportedPages, file.UsedPages),
            Integer(F.MemoryAllPagefilePeakUsedReportedPages, file.PeakUsedPages),
            Integer(F.MemoryAllPagefilePageSizeReportedBytes, file.PageSizeBytes is > 0 ? file.PageSizeBytes : null)];
    }
}
