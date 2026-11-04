using System.Globalization;
using ResourceManager.ReadWrite.Windows.Devices.Models;
using F = ResourceManager.ReadWrite.Information.DeviceInformationFieldRegistry;

namespace ResourceManager.ReadWrite.Information;

public static partial class WindowsInformationFieldProjection
{
    public static IReadOnlyCollection<DeviceInformationField> StorageFields(DeviceTopologyStorageInformation storage)
    {
        // Applicability is typed and fixed; sampling and publication never classify field-name strings.
        if (storage.Protocol == StorageIdentifyProtocol.Ata)
        {
            var data = storage.Ata;
            return [
                Text(F.StorageAllModelDeclared, data?.Model),
                Text(F.StorageAllSerialDeclared, data?.SerialNumber),
                Text(F.StorageAllFirmwareVersionDeclared, data?.FirmwareRevision),
                Integer(F.StorageAllCapacityDeclared, data?.CapacityBytes),
                Integer(F.StorageAllAtaLogicalSectorSizeDeclaredBytes, data?.LogicalSectorBytes),
                Integer(F.StorageAllAtaPhysicalSectorSizeDeclaredBytes, data?.PhysicalSectorBytes),
                Boolean(F.StorageAllAtaTrimSupported, data?.TrimSupported),
                Integer(F.StorageAllAtaMajorVersionSupportedMask, data?.MajorVersionSupportedMask),
                Integer(F.StorageAllAtaMediaRotationRateDeclaredRpm, data?.MediaRotationRateRpm),
                Boolean(F.StorageAllAtaNonRotatingMediaDeclared, data?.NonRotatingMedia),
                Value(F.StorageAllAtaIdentifyRaw, data?.Raw)];
        }
        if (storage.Protocol == StorageIdentifyProtocol.NvmeController)
        {
            var data = storage.Nvme;
            return [
                Text(F.StorageAllNvmeSubsystemModelDeclared, data?.Model),
                Text(F.StorageAllNvmeSubsystemSerialDeclared, data?.SerialNumber),
                Text(F.StorageAllNvmeSubsystemFirmwareVersionDeclared, data?.FirmwareRevision),
                Integer(F.StorageAllNvmeControllerVendorIdDeclared, data?.VendorId),
                Integer(F.StorageAllNvmeControllerSubsystemVendorIdDeclared, data?.SubsystemVendorId),
                Integer(F.StorageAllNvmeControllerIeeeOuiDeclared, data?.IeeeOui),
                Integer(F.StorageAllNvmeControllerVersionDeclared, data?.Version),
                Integer(F.StorageAllNvmeControllerIdDeclared, data?.ControllerId),
                Value(F.StorageAllNvmeControllerAccessibleTotalNvmCapacityReportedBytes, data?.TotalNvmCapacityBytes?.ToString(CultureInfo.InvariantCulture)),
                Value(F.StorageAllNvmeControllerAccessibleUnallocatedNvmCapacityReportedBytes, data?.UnallocatedNvmCapacityBytes?.ToString(CultureInfo.InvariantCulture)),
                Integer(F.StorageAllNvmeSubsystemMaximumValidNamespaceIdDeclared, data?.MaximumValidNamespaceId),
                Boolean(F.StorageAllNvmeDatasetManagementSupported, data?.DatasetManagementSupported),
                Value(F.StorageAllNvmeIdentifyControllerRaw, data?.Raw)];
        }
        return [];
    }
}
