using System.ComponentModel;
using System.Runtime.InteropServices;
using ResourceManager.App.Domain.GpuPlacement;

namespace ResourceManager.App.Infrastructure.GpuPlacement;

public sealed partial class WindowsGpuPlacementInjector
{
    private static RuntimeGpuProviderInjectionResult? CheckProcessIdentity(
        SafeKernelHandle handle, GpuPlacementProcessInstance target)
    {
        if (NativeMethods.WaitForSingleObject(handle, 0) != NativeMethods.WaitTimeout)
            return Failed(target.ProcessId, "process-unavailable", "目标已退出或无法确认存活。");

        if (!NativeMethods.GetProcessTimes(handle, out var creation, out _, out _, out _))
        {
            var error = Marshal.GetLastWin32Error();
            return Failed(target.ProcessId, "process-identity-unavailable", new Win32Exception(error).Message, error);
        }
        if (creation != target.ProcessStartKey)
            return Failed(target.ProcessId, "process-identity-mismatch", "目标 PID 已不是计划中的进程实例。");

        if (!TryReadExecutablePath(handle, out var executablePath))
        {
            var error = Marshal.GetLastWin32Error();
            return Failed(target.ProcessId, "process-image-unavailable", new Win32Exception(error).Message, error);
        }
        return PathsMatch(executablePath, target.ExecutablePath)
            ? null
            : Failed(target.ProcessId, "process-image-mismatch", "目标可执行路径与计划不符。");
    }

    private static bool PathsMatch(string actual, string expected)
    {
        try
        {
            return Path.GetFullPath(actual).Equals(Path.GetFullPath(expected), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
        {
            return false;
        }
    }
}
