using System.Diagnostics;
using ResourceManager.Shared.ServiceHosting;

namespace ResourceManager.Updater;

internal sealed partial class WindowsUpdateRuntime
{
    public void StopService(string root)
    {
        var backend = Backend(root);
        var service = WindowsServiceRegistration.Read(WindowsServiceRegistration.ProductServiceName)
            ?? throw new InvalidOperationException("产品服务不存在。");
        WindowsServiceRegistration.RequireExpectedBinary(service, backend);
        var processes = CaptureBackendProcesses(backend);
        try
        {
            var canTerminate = processes.Any(process => IsOwnedServiceProcess(service,
                process.Process.Id, process.ImagePath, backend));
            StopAndWait(
                () => WindowsServiceRegistration.StopExisting(WindowsServiceRegistration.ProductServiceName, backend),
                timeout => WaitForProcesses(processes, timeout),
                canTerminate ? () =>
                {
                    var current = WindowsServiceRegistration.Read(WindowsServiceRegistration.ProductServiceName)
                        ?? throw new InvalidOperationException("产品服务登记已变化。");
                    WindowsServiceRegistration.RequireExpectedBinary(current, backend);
                    if (current.State != 1 && current.ProcessId != 0 && current.ProcessId != service.ProcessId)
                        throw new InvalidOperationException("产品服务进程已变化，不能结束另一实例。");
                    // Validate every remaining instance before ending any of them.
                    foreach (var process in processes)
                        if (!process.Process.HasExited && !IsOwnedServiceProcess(service, process.Process.Id,
                                process.ImagePath, backend))
                            throw new InvalidOperationException("不能确认仍在运行的后端属于原服务实例。");
                    foreach (var process in processes)
                        if (!process.Process.HasExited) process.Process.Kill(entireProcessTree: false);
                } : null,
                () => WindowsServiceRegistration.WaitForStopped(WindowsServiceRegistration.ProductServiceName, backend));
        }
        finally
        {
            foreach (var process in processes) process.Process.Dispose();
        }
    }

    internal static bool IsOwnedServiceProcess(WindowsServiceState service, int processId,
        string processPath, string expectedBackend)
    {
        WindowsServiceRegistration.RequireExpectedBinary(service, expectedBackend);
        return service.Running && service.ProcessId != 0 && service.ProcessId == (uint)processId
            && Path.GetFullPath(processPath).Equals(Path.GetFullPath(expectedBackend), StringComparison.OrdinalIgnoreCase);
    }

    // This seam executes the production ordering, with recorded operations in tests.
    internal static void StopAndWait(Action requestStop, Func<int, bool> waitForExit,
        Action? terminateOwnedInstance, Action confirmServiceStopped)
    {
        var stopTimedOut = false;
        try { requestStop(); }
        catch (TimeoutException) when (terminateOwnedInstance is not null) { stopTimedOut = true; }
        if (!waitForExit(stopTimedOut ? 0 : 30_000))
        {
            if (terminateOwnedInstance is null)
                throw new TimeoutException("后端进程未退出，且无法确认可强制结束的服务实例。");
            terminateOwnedInstance();
            if (!waitForExit(10_000))
                throw new TimeoutException("服务进程结束后仍未退出；安装目录不能切换。");
        }
        confirmServiceStopped();
    }

    private static bool WaitForProcesses((Process Process, string ImagePath)[] processes, int milliseconds)
    {
        var watch = Stopwatch.StartNew();
        foreach (var process in processes)
            if (!process.Process.WaitForExit(Math.Max(0, milliseconds - checked((int)watch.ElapsedMilliseconds)))) return false;
        return true;
    }

    private static (Process Process, string ImagePath)[] CaptureBackendProcesses(string backend)
    {
        var all = Process.GetProcessesByName("ResourceManager");
        var selected = new List<(Process Process, string ImagePath)>();
        var returned = false;
        try
        {
            foreach (var process in all)
            {
                try
                {
                    // Hold the original kernel process handle through stop/kill/wait;
                    // never reopen a potentially reused PID to terminate it.
                    _ = process.SafeHandle;
                    var image = process.MainModule?.FileName;
                    if (string.Equals(image, backend, StringComparison.OrdinalIgnoreCase))
                        selected.Add((process, image!));
                }
                catch (InvalidOperationException) { /* Exited during capture. */ }
            }
            returned = true;
            return selected.ToArray();
        }
        finally
        {
            foreach (var process in all)
                if (!returned || !selected.Any(item => ReferenceEquals(item.Process, process))) process.Dispose();
        }
    }
}
