using System.Runtime.InteropServices;

namespace ResourceManager.ReadWrite.Windows.Devices.Hid;

internal enum WindowsHidReportType { Input = 0, Output = 1, Feature = 2 }

[StructLayout(LayoutKind.Explicit, Size = 64)]
internal struct WindowsHidCaps
{
    [FieldOffset(0)] internal ushort Usage;
    [FieldOffset(2)] internal ushort UsagePage;
    [FieldOffset(4)] internal ushort InputReportByteLength;
    [FieldOffset(6)] internal ushort OutputReportByteLength;
    [FieldOffset(8)] internal ushort FeatureReportByteLength;
    [FieldOffset(44)] internal ushort NumberLinkCollectionNodes;
    [FieldOffset(46)] internal ushort NumberInputButtonCaps;
    [FieldOffset(48)] internal ushort NumberInputValueCaps;
    [FieldOffset(50)] internal ushort NumberInputDataIndices;
    [FieldOffset(52)] internal ushort NumberOutputButtonCaps;
    [FieldOffset(54)] internal ushort NumberOutputValueCaps;
    [FieldOffset(56)] internal ushort NumberOutputDataIndices;
    [FieldOffset(58)] internal ushort NumberFeatureButtonCaps;
    [FieldOffset(60)] internal ushort NumberFeatureValueCaps;
    [FieldOffset(62)] internal ushort NumberFeatureDataIndices;

    internal readonly ushort ButtonCount(WindowsHidReportType report) => report switch {
        WindowsHidReportType.Input => NumberInputButtonCaps, WindowsHidReportType.Output => NumberOutputButtonCaps,
        WindowsHidReportType.Feature => NumberFeatureButtonCaps, _ => throw new ArgumentOutOfRangeException(nameof(report)) };
    internal readonly ushort ValueCount(WindowsHidReportType report) => report switch {
        WindowsHidReportType.Input => NumberInputValueCaps, WindowsHidReportType.Output => NumberOutputValueCaps,
        WindowsHidReportType.Feature => NumberFeatureValueCaps, _ => throw new ArgumentOutOfRangeException(nameof(report)) };
}

[StructLayout(LayoutKind.Sequential)]
internal struct WindowsHidSelection
{
    internal ushort UsageMinimum, UsageMaximum, StringMinimum, StringMaximum;
    internal ushort DesignatorMinimum, DesignatorMaximum, DataIndexMinimum, DataIndexMaximum;
}

[StructLayout(LayoutKind.Explicit, Size = 72)]
internal struct WindowsHidButtonCaps
{
    [FieldOffset(0)] internal ushort UsagePage;
    [FieldOffset(2)] internal byte ReportId;
    [FieldOffset(3)] internal byte IsAlias;
    [FieldOffset(4)] internal ushort BitField;
    [FieldOffset(6)] internal ushort LinkCollection;
    [FieldOffset(8)] internal ushort LinkUsage;
    [FieldOffset(10)] internal ushort LinkUsagePage;
    [FieldOffset(12)] internal byte IsRange;
    [FieldOffset(13)] internal byte IsStringRange;
    [FieldOffset(14)] internal byte IsDesignatorRange;
    [FieldOffset(15)] internal byte IsAbsolute;
    // Formerly reserved; interpreted only when the runtime parser API is >= 2.
    [FieldOffset(16)] internal ushort ReportCount;
    [FieldOffset(56)] internal WindowsHidSelection Selection;
}

[StructLayout(LayoutKind.Explicit, Size = 72)]
internal struct WindowsHidValueCaps
{
    [FieldOffset(0)] internal ushort UsagePage;
    [FieldOffset(2)] internal byte ReportId;
    [FieldOffset(3)] internal byte IsAlias;
    [FieldOffset(4)] internal ushort BitField;
    [FieldOffset(6)] internal ushort LinkCollection;
    [FieldOffset(8)] internal ushort LinkUsage;
    [FieldOffset(10)] internal ushort LinkUsagePage;
    [FieldOffset(12)] internal byte IsRange;
    [FieldOffset(13)] internal byte IsStringRange;
    [FieldOffset(14)] internal byte IsDesignatorRange;
    [FieldOffset(15)] internal byte IsAbsolute;
    [FieldOffset(16)] internal byte HasNull;
    [FieldOffset(18)] internal ushort BitSize;
    [FieldOffset(20)] internal ushort ReportCount;
    [FieldOffset(32)] internal uint UnitsExponentRaw;
    [FieldOffset(36)] internal uint UnitsRaw;
    [FieldOffset(40)] internal int LogicalMinimum;
    [FieldOffset(44)] internal int LogicalMaximum;
    [FieldOffset(48)] internal int PhysicalMinimum;
    [FieldOffset(52)] internal int PhysicalMaximum;
    [FieldOffset(56)] internal WindowsHidSelection Selection;
}
