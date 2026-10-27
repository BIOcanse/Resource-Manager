using System.Runtime.InteropServices;

namespace ResourceManager.ReadWrite.Windows.Pdh;

internal static partial class PdhCounterMethods
{
    internal const int ErrorSuccess = 0;
    internal const uint PdhFmtDouble = 0x00000200;
    internal const uint PdhFmtLarge = 0x00000400;
    internal const uint PdhFmtNoScale = 0x00001000;
    internal const uint PdhCounterTypeRawCount = 0x00010000;
    internal const uint PdhCStatusValidData = 0;
    internal const uint PdhCStatusNewData = 1;
    [LibraryImport("pdh.dll", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int PdhOpenQuery(
        string? dataSource,
        UIntPtr userData,
        out IntPtr query);

    [LibraryImport("pdh.dll", EntryPoint = "PdhAddEnglishCounterW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int PdhAddEnglishCounter(
        IntPtr query,
        string counterPath,
        UIntPtr userData,
        out IntPtr counter);

    [LibraryImport("pdh.dll")]
    internal static partial int PdhCollectQueryData(IntPtr query);

    [LibraryImport("pdh.dll")]
    internal static partial int PdhGetFormattedCounterValue(
        IntPtr counter,
        uint format,
        out uint counterType,
        out PdhFmtCounterValue value);

    [LibraryImport("pdh.dll", EntryPoint = "PdhGetFormattedCounterArrayW")]
    internal static partial int PdhGetFormattedCounterArray(
        IntPtr counter,
        uint format,
        ref uint bufferSize,
        ref uint itemCount,
        IntPtr itemBuffer);

    [LibraryImport("pdh.dll")]
    internal static partial int PdhCloseQuery(IntPtr query);

    [LibraryImport("pdh.dll", EntryPoint = "PdhExpandWildCardPathW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int PdhExpandWildCardPath(
        string? dataSource,
        string wildCardPath,
        char[]? expandedPathList,
        ref uint pathListLength,
        uint flags);
}

[StructLayout(LayoutKind.Explicit)]
internal struct PdhFmtCounterValue
{
    [FieldOffset(0)]
    public uint CStatus;

    [FieldOffset(8)]
    public double DoubleValue;

    [FieldOffset(8)]
    public long LargeValue;
}

[StructLayout(LayoutKind.Explicit, Size = 24)]
internal struct PdhFmtCounterValueItem
{
    [FieldOffset(0)]
    public IntPtr Name;

    [FieldOffset(8)]
    public PdhFmtCounterValue Value;
}
