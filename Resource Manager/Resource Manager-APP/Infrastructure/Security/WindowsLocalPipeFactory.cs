using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ResourceManager.App.Infrastructure.Security;

internal static class WindowsLocalPipeFactory
{
    internal static NamedPipeServerStream Create(
        string pipeName,
        string protectedDaclSddl,
        int inputBufferSize,
        int outputBufferSize)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException();
        }
        if (string.IsNullOrWhiteSpace(pipeName)
            || pipeName.Contains('\\')
            || pipeName.Contains('/'))
        {
            throw new ArgumentException("A simple named-pipe name is required.", nameof(pipeName));
        }
        if (string.IsNullOrWhiteSpace(protectedDaclSddl)
            || !protectedDaclSddl.StartsWith("D:P", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "An explicit protected DACL SDDL is required.",
                nameof(protectedDaclSddl));
        }
        if (inputBufferSize <= 0 || outputBufferSize <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(inputBufferSize),
                "Pipe buffers must be positive.");
        }

        if (!Native
            .ConvertStringSecurityDescriptorToSecurityDescriptorW(
                protectedDaclSddl,
                Native.SecurityDescriptorRevision,
                out var descriptor,
                out _))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        try
        {
            var attributes = new Native.SecurityAttributes
            {
                Length = checked((uint)Marshal.SizeOf<Native.SecurityAttributes>()),
                SecurityDescriptor = descriptor,
                InheritHandle = false
            };
            var handle = Native.CreateNamedPipeW(
                $"\\\\.\\pipe\\{pipeName}",
                Native.PipeAccessDuplex
                    | Native.FileFlagFirstPipeInstance
                    | Native.FileFlagOverlapped,
                Native.PipeRejectRemoteClients,
                maximumInstances: 1,
                checked((uint)outputBufferSize),
                checked((uint)inputBufferSize),
                defaultTimeoutMilliseconds: 0,
                ref attributes);
            if (handle.IsInvalid)
            {
                var error = Marshal.GetLastWin32Error();
                handle.Dispose();
                throw new Win32Exception(error);
            }
            try
            {
                return new NamedPipeServerStream(
                    PipeDirection.InOut,
                    isAsync: true,
                    isConnected: false,
                    handle);
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }
        finally
        {
            _ = Native.LocalFree(descriptor);
        }
    }

    private static class Native
    {
        internal const uint PipeAccessDuplex = 0x00000003;
        internal const uint FileFlagFirstPipeInstance = 0x00080000;
        internal const uint FileFlagOverlapped = 0x40000000;
        internal const uint PipeRejectRemoteClients = 0x00000008;
        internal const uint SecurityDescriptorRevision = 1;

        [StructLayout(LayoutKind.Sequential)]
        internal struct SecurityAttributes
        {
            internal uint Length;
            internal IntPtr SecurityDescriptor;
            [MarshalAs(UnmanagedType.Bool)] internal bool InheritHandle;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern SafePipeHandle CreateNamedPipeW(string name, uint openMode, uint pipeMode,
            uint maximumInstances, uint outputBufferSize, uint inputBufferSize, uint defaultTimeoutMilliseconds,
            ref SecurityAttributes securityAttributes);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string securityDescriptor,
            uint revision, out IntPtr descriptor, out uint descriptorSize);
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern IntPtr LocalFree(IntPtr memory);
    }
}
