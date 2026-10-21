namespace ResourceManager.ReadWrite.Windows.Devices.Models;

public sealed record DeviceTopologyHidInformation(
    string DeviceIdentity,
    string? PnpInstanceId,
    HidCollectionUsage? Usage,
    HidReportLengths? ReportLengths,
    IReadOnlyList<HidButtonCapability>? Buttons,
    IReadOnlyList<HidValueCapability>? Values);

public sealed record HidCollectionUsage(ushort UsagePage, ushort Usage);
public sealed record HidReportLengths(ushort InputBytes, ushort OutputBytes, ushort FeatureBytes);

// For a single usage, each minimum/maximum pair is normalized to the single index;
// the reserved half of the native NotRange union is never interpreted as a maximum.
public sealed record HidCapabilitySelection(bool IsRange,
    ushort UsageMinimum, ushort UsageMaximum, ushort StringMinimum, ushort StringMaximum,
    ushort DesignatorMinimum, ushort DesignatorMaximum, ushort DataIndexMinimum, ushort DataIndexMaximum);

public sealed record HidControlCapability(string ReportType, ushort UsagePage, byte ReportId, bool IsAlias,
    ushort BitField, ushort LinkCollection, ushort LinkUsagePage, ushort LinkUsage,
    bool IsStringRange, bool IsDesignatorRange, bool IsAbsolute, HidCapabilitySelection Selection);

public sealed record HidButtonCapability(HidControlCapability Control, ushort? ReportCount);
public sealed record HidValueCapability(HidControlCapability Control, bool HasNull, ushort BitSize, ushort ReportCount,
    uint UnitsExponentRaw, uint UnitsRaw, int LogicalMinimum, int LogicalMaximum, int PhysicalMinimum, int PhysicalMaximum);
