using System.Runtime.InteropServices;

namespace ResourceManager.ReadWrite.Windows.Services;

internal sealed class WindowsServiceNativeApi : IWindowsServiceNativeApi
{
    public int LastError => Marshal.GetLastWin32Error();
    public IntPtr OpenManager(uint access)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("SCM information requires Windows.");
        return NativeMethods.OpenSCManager(null, null, access);
    }
    public bool Enumerate(IntPtr manager, uint type, IntPtr buffer, uint size, out uint bytes, out uint count, ref uint resume)
        => NativeMethods.EnumServicesStatusEx(manager, 0, type, 0x00000003, buffer, size, out bytes, out count, ref resume, null);
    public IntPtr OpenService(IntPtr manager, string name, uint access) => NativeMethods.OpenService(manager, name, access);
    public bool QueryConfiguration(IntPtr service, IntPtr buffer, uint size, out uint bytes)
        => NativeMethods.QueryServiceConfig(service, buffer, size, out bytes);
    public IntPtr Allocate(int size) => Marshal.AllocHGlobal(size);
    public void Free(IntPtr buffer) => Marshal.FreeHGlobal(buffer);
    public void Close(IntPtr handle) => _ = NativeMethods.CloseServiceHandle(handle);
}

internal static class NativeMethods
{
    [DllImport("advapi32.dll", EntryPoint = "OpenSCManagerW", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern IntPtr OpenSCManager(string? machineName, string? databaseName, uint desiredAccess);
    [DllImport("advapi32.dll", EntryPoint = "EnumServicesStatusExW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumServicesStatusEx(IntPtr manager, int infoLevel, uint serviceType,
        uint serviceState, IntPtr services, uint bufferSize, out uint bytesNeeded, out uint servicesReturned,
        ref uint resumeHandle, string? groupName);
    [DllImport("advapi32.dll", EntryPoint = "OpenServiceW", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern IntPtr OpenService(IntPtr manager, string name, uint desiredAccess);
    [DllImport("advapi32.dll", EntryPoint = "QueryServiceConfigW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool QueryServiceConfig(IntPtr service, IntPtr configuration, uint bufferSize, out uint bytesNeeded);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseServiceHandle(IntPtr handle);
}

[StructLayout(LayoutKind.Sequential)]
internal struct ServiceStatusProcess
{
    public uint ServiceType, CurrentState, ControlsAccepted, Win32ExitCode, ServiceSpecificExitCode,
        CheckPoint, WaitHint, ProcessId, ServiceFlags;
}
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct EnumServiceStatusProcess
{
    [MarshalAs(UnmanagedType.LPWStr)] public string ServiceName;
    [MarshalAs(UnmanagedType.LPWStr)] public string DisplayName;
    public ServiceStatusProcess Status;
}
[StructLayout(LayoutKind.Sequential)]
internal struct QueryServiceConfig
{
    public uint ServiceType, StartType, ErrorControl;
    public IntPtr BinaryPathName, LoadOrderGroup;
    public uint TagId;
    public IntPtr Dependencies, ServiceStartName, DisplayName;
}
