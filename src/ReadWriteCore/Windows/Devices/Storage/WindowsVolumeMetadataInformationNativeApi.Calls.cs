using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using ResourceManager.ReadWrite.Windows.Devices.Models;

namespace ResourceManager.ReadWrite.Windows.Devices;

internal sealed partial class WindowsVolumeMetadataInformationNativeApi
{
    internal const uint ExtentsIoctl = 0x00560000;
    private sealed class NativeCalls : IWindowsVolumeMetadataNativeCalls
    {
        public SafeFileHandle OpenVolume(string path) => CreateFileW(path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        public bool Extents(SafeFileHandle handle, byte[] buffer, out uint returnedBytes)
            => DeviceIoControl(handle, ExtentsIoctl, IntPtr.Zero, 0, buffer, (uint)buffer.Length, out returnedBytes, IntPtr.Zero);
        public bool MountPaths(string root, char[] buffer, out uint returnedCharacters)
            => GetVolumePathNamesForVolumeNameW(root, buffer, (uint)buffer.Length, out returnedCharacters);
        public bool FileSystem(string root, out DeviceTopologyVolumeFileSystemInformation? value)
        {
            var label = new StringBuilder(261); var name = new StringBuilder(261);
            var success = GetVolumeInformationW(root, label, (uint)label.Capacity, out var serial, out var maximum, out var flags, name, (uint)name.Capacity);
            value = success ? new(name.ToString(), label.ToString(), serial, maximum, flags) : null;
            return success;
        }
        public int LastError() => Marshal.GetLastPInvokeError();
        public uint ThreadErrorMode() => GetThreadErrorMode();
        public bool SetThreadErrorMode(uint mode, out uint previous) => WindowsVolumeMetadataInformationNativeApi.SetThreadErrorMode(mode, out previous);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, IntPtr input, uint inputSize, [Out] byte[] output, uint outputSize, out uint returned, IntPtr overlapped);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumePathNamesForVolumeNameW(string root, [Out] char[] paths, uint capacity, out uint returned);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeInformationW(string root, StringBuilder label, uint labelCapacity, out uint serial, out uint maximumComponent, out uint flags, StringBuilder name, uint nameCapacity);
    [DllImport("kernel32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint GetThreadErrorMode();
    [DllImport("kernel32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetThreadErrorMode(uint mode, out uint previous);
}
