using ResourceManager.ReadWrite.Windows.Devices.Models;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ResourceManager.ReadWrite.Windows.Devices;

public static partial class WindowsNvmeHealthReader
{
    private static IWindowsNvmeHealthQuery? Open(int number)
    {
        var handle = CreateFileW($@"\\.\PhysicalDrive{number}", 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (!handle.IsInvalid) return new DeviceQuery(handle);
        handle.Dispose();
        return null;
    }

    private sealed class DeviceQuery(SafeFileHandle handle) : IWindowsNvmeHealthQuery
    {
        public byte[]? Query(byte[] input, int outputLength)
        {
            var output = new byte[outputLength];
            return DeviceIoControl(handle, QueryPropertyIoctl, input, (uint)input.Length, output, (uint)output.Length,
                out var returned, IntPtr.Zero) && returned <= output.Length ? output[..(int)returned] : null;
        }
        public void Dispose() => handle.Dispose();
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern SafeFileHandle CreateFileW(string fileName, uint desiredAccess, uint shareMode,
        IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint controlCode, byte[] input,
        uint inputSize, byte[] output, uint outputSize, out uint bytesReturned, IntPtr overlapped);
}
