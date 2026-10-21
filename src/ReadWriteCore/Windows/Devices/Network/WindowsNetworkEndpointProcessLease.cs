using System.Runtime.InteropServices;
using NativeQuery = ResourceManager.ReadWrite.Windows.Processes.WindowsProcessBasicMetricNativeApi;

namespace ResourceManager.ReadWrite.Windows.Devices;

internal sealed class WindowsNetworkEndpointProcessLease : IWindowsNetworkEndpointProcessLease
{
    private nint handle;
    public uint ProcessId { get; }
    private WindowsNetworkEndpointProcessLease(uint processId, nint handle) { ProcessId = processId; this.handle = handle; }

    internal static WindowsNetworkEndpointProcessLease? Open(uint processId)
    {
        if (!OperatingSystem.IsWindows() || processId == 0) return null;
        var process = NativeQuery.OpenProcess(0x1000 | 0x00100000,
            false, unchecked((int)processId)); // SYNCHRONIZE only permits a zero-time liveness query.
        return process == 0 ? null : new(processId, process);
    }

    public bool TryReadCreationFileTime(out ulong created)
    {
        created = 0;
        if (handle == 0 || WaitForSingleObject(handle, 0) != 258
            || !NativeQuery.GetProcessTimes(handle, out var creation, out _, out _, out _)
            || WaitForSingleObject(handle, 0) != 258) return false;
        created = creation.ToUInt64();
        return created != 0;
    }

    public void Dispose()
    {
        var process = Interlocked.Exchange(ref handle, 0);
        if (process != 0) NativeQuery.CloseHandle(process);
    }

    [DllImport("kernel32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint WaitForSingleObject(nint handle, uint milliseconds);
}
