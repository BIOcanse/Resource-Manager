namespace ResourceManager.ReadWrite.Windows.Devices.Models;

/// <summary>Modules and declarations decoded from one raw SMBIOS capture.</summary>
public sealed record SmbiosFirmwareCapture(
    IReadOnlyList<FirmwareMemoryModule> Modules,
    SmbiosFirmwareDeclarations Declarations)
{
    // Null means absent or truncated Type 3; codes exclude the independent chassis lock bit.
    public IReadOnlyList<byte>? ChassisTypes { get; init; }
}
