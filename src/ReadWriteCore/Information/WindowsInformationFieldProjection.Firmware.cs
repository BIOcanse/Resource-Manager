using ResourceManager.ReadWrite.Windows.Devices.Models;
using ResourceManager.ReadWrite.Windows.Devices;
using F = ResourceManager.ReadWrite.Information.DeviceInformationFieldRegistry;

namespace ResourceManager.ReadWrite.Information;

public static partial class WindowsInformationFieldProjection
{
    public static IReadOnlyList<DeviceInformationField> SlotFields(FirmwareSystemSlot slot) => [
        Value(F.MotherboardAllSlotDesignation, slot.Designation),
        Integer(F.MotherboardAllSlotTypeDeclaredCode, slot.SlotType),
        Integer(F.MotherboardAllSlotDataBusWidthDeclaredCode, slot.DataBusWidth),
        Integer(F.MotherboardAllSlotCurrentUsageDeclaredCode, slot.CurrentUsage),
        Integer(F.MotherboardAllSlotLengthDeclaredCode, slot.SlotLength),
        Integer(F.MotherboardAllSlotIdDeclared, slot.SlotId),
        Integer(F.MotherboardAllSlotCharacteristics1DeclaredMask, slot.Characteristics1),
        Integer(F.MotherboardAllSlotCharacteristics2DeclaredMask, slot.Characteristics2),
        Integer(F.MotherboardAllSlotPciSegmentDeclared, slot.PciSegment),
        Integer(F.MotherboardAllSlotPciBusDeclared, slot.PciBus),
        Integer(F.MotherboardAllSlotPciDeviceDeclared, slot.PciDevice),
        Integer(F.MotherboardAllSlotPciFunctionDeclared, slot.PciFunction)
    ];

    public static IReadOnlyList<DeviceInformationField> MemoryModuleFields(FirmwareMemoryModule module) => [
        Integer(F.MemoryAllDimmCapacity, module.CapacityBytes),
        Text(F.MemoryAllDimmLocator, module.Locator),
        Text(F.MemoryAllDimmManufacturer, module.Manufacturer),
        Text(F.MemoryAllDimmPartNumber, module.PartNumber),
        Text(F.MemoryAllDimmSerialNumberDeclared, module.SerialNumber),
        Text(F.MemoryAllDimmTypeDeclared, module.MemoryType),
        Integer(F.MemoryAllDimmDeclaredDataRate, module.DeclaredDataRateMtps),
        Integer(F.MemoryAllDimmConfiguredDataRate, module.ConfiguredDataRateMtps),
        Integer(F.MemoryAllDimmTotalWidthDeclaredBits, module.TotalWidthBits),
        Integer(F.MemoryAllDimmDataWidthDeclaredBits, module.DataWidthBits),
        Number(F.MemoryAllDimmConfiguredVoltageDeclaredVolts, module.ConfiguredVoltageVolts)
    ];

    public static IReadOnlyList<DeviceInformationField> ProcessorSocketFields(FirmwareProcessorSocket socket)
        => [Text(F.CpuAllSocketDesignation, socket.Designation)];

    public static DeviceInformationSnapshot Firmware(SmbiosFirmwareCapture capture, DateTimeOffset? observedAt)
    {
        var devices = new List<DeviceInformationObject>
            { new("bios", "bios", [Text(F.BiosAllReleaseDate, capture.Declarations.BiosReleaseDate)]) };
        foreach (var module in capture.Modules)
            if (!string.IsNullOrWhiteSpace(module.DeviceIdentity))
                devices.Add(new(module.DeviceIdentity, "memory_dimm", MemoryModuleFields(module).ToArray()));
        foreach (var slot in capture.Declarations.Slots)
            if (!string.IsNullOrWhiteSpace(slot.DeviceIdentity))
                devices.Add(new(slot.DeviceIdentity, "motherboard_slot", SlotFields(slot).ToArray()));
        foreach (var socket in capture.Declarations.ProcessorSockets)
            if (!string.IsNullOrWhiteSpace(socket.DeviceIdentity))
                devices.Add(new(socket.DeviceIdentity, "cpu_socket", ProcessorSocketFields(socket).ToArray()));
        var unique = devices.GroupBy(static device => device.DeviceIdentity, StringComparer.Ordinal)
            .Where(static group => group.Count() == 1).Select(static group => group.Single())
            .OrderBy(static device => device.DeviceIdentity, StringComparer.Ordinal);
        return Frame(unique, observedAt);
    }
}
