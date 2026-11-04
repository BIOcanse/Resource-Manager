namespace ResourceManager.ReadWrite.Windows.Devices.Models;

/// <summary>Windows can select only the first table for a signature, not a physical instance.</summary>
public sealed record AcpiFirmwareTable(
    uint SignatureId,
    string Signature,
    uint ReportedInstanceCount,
    AcpiDescriptionTable? FirstTable);

/// <summary>Validated common header and exactly Length bytes. Revision is table-specific.</summary>
public sealed record AcpiDescriptionTable(
    byte[] Raw,
    byte Revision,
    string? OemId,
    string? OemTableId,
    uint OemRevision,
    string? CreatorId,
    uint CreatorRevision);
