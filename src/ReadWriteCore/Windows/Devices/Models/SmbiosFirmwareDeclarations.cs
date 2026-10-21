namespace ResourceManager.ReadWrite.Windows.Devices.Models;

/// <summary>Firmware declarations from one raw SMBIOS table, never runtime PCI state.</summary>
public sealed record SmbiosFirmwareDeclarations(
    string? BiosReleaseDate,
    IReadOnlyList<FirmwareSystemSlot> Slots)
{
    public static SmbiosFirmwareDeclarations Empty { get; } = new(null, []);
    public IReadOnlyList<FirmwareProcessorSocket> ProcessorSockets { get; init; } = [];
}

/// <summary>A firmware Central Processor socket declaration; no inferred OS package association.</summary>
public sealed record FirmwareProcessorSocket(string DeviceIdentity, string? Designation);

public sealed record FirmwareSystemSlot(
    string DeviceIdentity,
    string? Designation,
    byte? SlotType,
    byte? DataBusWidth,
    byte? CurrentUsage,
    byte? SlotLength,
    ushort? SlotId,
    byte? Characteristics1,
    byte? Characteristics2,
    ushort? PciSegment,
    byte? PciBus,
    byte? PciDevice,
    byte? PciFunction);
