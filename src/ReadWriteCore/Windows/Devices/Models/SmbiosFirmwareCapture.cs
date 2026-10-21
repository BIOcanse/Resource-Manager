namespace ResourceManager.ReadWrite.Windows.Devices.Models;

/// <summary>Modules and declarations decoded from one raw SMBIOS capture.</summary>
public sealed record SmbiosFirmwareCapture(
    IReadOnlyList<FirmwareMemoryModule> Modules,
    SmbiosFirmwareDeclarations Declarations);
