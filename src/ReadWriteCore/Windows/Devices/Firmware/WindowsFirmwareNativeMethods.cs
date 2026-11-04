using System.Runtime.InteropServices;

namespace ResourceManager.ReadWrite.Windows.Devices;

internal static partial class WindowsFirmwareNativeMethods
{
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetPhysicallyInstalledSystemMemory(out ulong totalMemoryInKilobytes);

    [LibraryImport("kernel32.dll", EntryPoint = "EnumSystemFirmwareTables", SetLastError = true)]
    internal static partial uint EnumSystemFirmwareTablesSize(uint provider, IntPtr buffer, uint bufferSize);

    [LibraryImport("kernel32.dll", EntryPoint = "EnumSystemFirmwareTables", SetLastError = true)]
    internal static partial uint EnumSystemFirmwareTables(uint provider, [Out] byte[] buffer, uint bufferSize);

    [LibraryImport("kernel32.dll", EntryPoint = "GetSystemFirmwareTable", SetLastError = true)]
    internal static partial uint GetSystemFirmwareTableSize(
        uint firmwareTableProvider,
        uint firmwareTableId,
        IntPtr firmwareTableBuffer,
        uint bufferSize);

    [LibraryImport("kernel32.dll", EntryPoint = "GetSystemFirmwareTable", SetLastError = true)]
    internal static partial uint GetSystemFirmwareTable(
        uint firmwareTableProvider,
        uint firmwareTableId,
        [Out] byte[] firmwareTableBuffer,
        uint bufferSize);
}
