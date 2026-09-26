using System.Runtime.InteropServices;

namespace ResourceManager.App.Infrastructure.Windows;

internal static partial class NativeMethods
{
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
}
