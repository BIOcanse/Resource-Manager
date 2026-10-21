using ResourceManager.App.Domain.DeviceTopology;
using F = ResourceManager.ReadWrite.Information.DeviceInformationFieldRegistry;

namespace ResourceManager.ReadWrite.Information;

public static partial class WindowsInformationFieldProjection
{
    /// <summary>Original PCI declarations and allocated resources from the same native device.</summary>
    public static IReadOnlyList<DeviceInformationField> Pci(DeviceTopologyPort port) => [
        Text(F.PciAllPnpDeviceIdDeclared, port.DeviceId),
        Text(F.PciAllParentDeviceId, port.NativeParentDeviceId),
        Text(F.PciAllVendorId, port.PciIdentification?.VendorId),
        Text(F.PciAllDeviceId, port.PciIdentification?.DeviceId),
        Text(F.PciAllRevisionIdDeclared, port.PciIdentification?.RevisionId),
        Text(F.PciAllSubsystemIdentifierDeclared, port.PciIdentification?.SubsystemIdentifier),
        Text(F.PciAllSubsystemVendorIdDeclared, port.PciIdentification?.SubsystemVendorId),
        Text(F.PciAllBaseClassDeclaredCode, port.PciIdentification?.BaseClass),
        Text(F.PciAllSubclassDeclaredCode, port.PciIdentification?.Subclass),
        Text(F.PciAllProgrammingInterfaceDeclaredCode, port.PciIdentification?.ProgrammingInterface),
        Value(F.PciAllResourceRange, port.PciResourceRanges),
        Value(F.PciAllInterruptsAllocatedReported, port.PciInterrupts),
        Value(F.PciAllDmaChannelsAllocatedReported, port.PciDmaChannels)
    ];
}
