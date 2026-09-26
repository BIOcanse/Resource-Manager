using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ResourceManager.Shared.ServiceHosting;

public sealed record WindowsServiceState(uint State, uint StartType, string BinaryPath, string Account)
{
    public bool Running => State == 4;
}

public static class WindowsServiceRegistration
{
    public const string ProductServiceName = "ResourceManager.Service";
    private const uint NoChange = uint.MaxValue;

    public static WindowsServiceState? Read(string serviceName)
    {
        using var manager = OpenManager(1);
        using var service = OpenServiceW(manager, serviceName, 1 | 4);
        if (service.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            if (error == 1060) return null;
            throw new Win32Exception(error);
        }
        return Read(service);
    }

    public static void RequireExpectedBinary(WindowsServiceState state, string executablePath)
    {
        var command = state.BinaryPath.Trim();
        // This service has exactly one executable and no command-line arguments.
        var binary = command.Length >= 2 && command[0] == '"' && command[^1] == '"'
            ? command[1..^1] : command;
        if (!Path.GetFullPath(binary).Equals(Path.GetFullPath(executablePath), StringComparison.OrdinalIgnoreCase)
            || !state.Account.Equals("LocalSystem", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The registered service belongs to another executable or account.");
    }

    public static void EnsureRunning(string serviceName, string executablePath, bool restart)
    {
        using var manager = OpenManager(1 | 2);
        using var service = OpenOrCreate(manager, serviceName, executablePath);
        var state = Read(service);
        RequireExpectedBinary(state, executablePath);
        if (state.State is 2 or 3) WaitForState(service, state.State == 2 ? 4u : 1u);
        state = Read(service);
        if (restart && state.Running)
        {
            Check(ControlService(service, 1, out _));
            WaitForState(service, 1);
        }
        if (Read(service).State != 4)
        {
            if (!StartServiceW(service, 0, IntPtr.Zero))
            {
                var error = Marshal.GetLastWin32Error();
                if (error != 1056) throw new Win32Exception(error);
            }
            WaitForState(service, 4);
        }
    }

    public static void SetAutoStart(string serviceName, string executablePath, bool enabled)
    {
        using var manager = OpenManager(1);
        using var service = OpenServiceW(manager, serviceName, 1 | 2 | 4);
        if (service.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        RequireExpectedBinary(Read(service), executablePath);
        SetStartType(service, enabled);
    }

    private static ServiceHandle OpenOrCreate(ServiceHandle manager, string name, string path)
    {
        const uint access = 1 | 2 | 4 | 16 | 32;
        var service = OpenServiceW(manager, name, access);
        if (!service.IsInvalid) return service;
        var error = Marshal.GetLastWin32Error();
        service.Dispose();
        if (error != 1060) throw new Win32Exception(error);
        service = CreateServiceW(manager, name, "Resource Manager Service", access, 16,
            3, 1, $"\"{Path.GetFullPath(path)}\"", null, IntPtr.Zero, null, null, null);
        if (!service.IsInvalid) return service;
        error = Marshal.GetLastWin32Error();
        service.Dispose();
        if (error == 1073)
        {
            service = OpenServiceW(manager, name, access);
            if (!service.IsInvalid) return service;
            error = Marshal.GetLastWin32Error();
            service.Dispose();
        }
        throw new Win32Exception(error);
    }

    private static WindowsServiceState Read(ServiceHandle service)
    {
        _ = QueryServiceConfigW(service, IntPtr.Zero, 0, out var bytes);
        var error = Marshal.GetLastWin32Error();
        if (error != 122 || bytes == 0) throw new Win32Exception(error);
        var buffer = Marshal.AllocHGlobal(checked((int)bytes));
        try
        {
            Check(QueryServiceConfigW(service, buffer, bytes, out _));
            var config = Marshal.PtrToStructure<ServiceConfig>(buffer);
            Check(QueryServiceStatus(service, out var status));
            return new WindowsServiceState(status.CurrentState, config.StartType,
                Marshal.PtrToStringUni(config.BinaryPath) ?? "",
                Marshal.PtrToStringUni(config.Account) ?? "");
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static void SetStartType(ServiceHandle service, bool enabled)
    {
        var desired = enabled ? 2u : 3u;
        if (Read(service).StartType == desired) return;
        Check(ChangeServiceConfigW(service, NoChange, desired, NoChange,
            null, null, IntPtr.Zero, null, null, null, null));
    }

    private static void WaitForState(ServiceHandle service, uint expected)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(30))
        {
            Check(QueryServiceStatus(service, out var status));
            if (status.CurrentState == expected) return;
            if (expected == 4 && status.CurrentState == 1)
                throw new Win32Exception(checked((int)status.Win32ExitCode), "The service stopped during startup.");
            Thread.Sleep(100);
        }
        throw new TimeoutException("The service did not complete its state transition within 30 seconds.");
    }

    private static ServiceHandle OpenManager(uint access)
    {
        var handle = OpenSCManagerW(null, null, access);
        if (!handle.IsInvalid) return handle;
        var error = Marshal.GetLastWin32Error();
        handle.Dispose();
        throw new Win32Exception(error);
    }

    private static void Check(bool success)
    {
        if (!success) throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceConfig
    {
        public uint ServiceType, StartType, ErrorControl;
        public IntPtr BinaryPath, LoadOrderGroup;
        public uint TagId;
        public IntPtr Dependencies, Account, DisplayName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        public uint ServiceType, CurrentState, ControlsAccepted, Win32ExitCode,
            ServiceSpecificExitCode, CheckPoint, WaitHint;
    }

    private sealed class ServiceHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public ServiceHandle() : base(true) { }
        protected override bool ReleaseHandle() => CloseServiceHandle(handle);
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ServiceHandle OpenSCManagerW(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ServiceHandle OpenServiceW(ServiceHandle manager, string name, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ServiceHandle CreateServiceW(ServiceHandle manager, string name, string display,
        uint access, uint type, uint start, uint error, string binary, string? group, IntPtr tag,
        string? dependencies, string? account, string? password);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryServiceConfigW(ServiceHandle service, IntPtr config, uint size, out uint needed);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ChangeServiceConfigW(ServiceHandle service, uint type, uint start, uint error,
        string? binary, string? group, IntPtr tag, string? dependencies, string? account, string? password, string? display);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool QueryServiceStatus(ServiceHandle service, out ServiceStatus status);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool ControlService(ServiceHandle service, uint control, out ServiceStatus status);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool StartServiceW(ServiceHandle service, uint count, IntPtr arguments);
    [DllImport("advapi32.dll")]
    private static extern bool CloseServiceHandle(IntPtr handle);
}
