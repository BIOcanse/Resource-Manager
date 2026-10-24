using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using ResourceManager.App.Domain.Settings;
using ResourceManager.Shared.ServiceHosting;

namespace ResourceManager.App.Infrastructure.ServiceHosting;

// This boundary never creates, starts, stops or reconfigures another service.
public sealed class WindowsServiceRecoveryControl : IServiceRecoveryControl
{
    public string? GetSkipReason()
    {
        var state = WindowsServiceRegistration.Read(WindowsServiceRegistration.ProductServiceName);
        if (state is null) return "installed-service-not-found";
        if (state.ProcessId != Environment.ProcessId) return "installed-service-has-another-process";
        WindowsServiceRegistration.RequireExpectedBinary(state,
            Environment.ProcessPath ?? throw new InvalidOperationException("Missing backend executable path."));
        return null;
    }

    public void Write(ServiceRecoveryConfiguration configuration)
    {
        using var manager = OpenSCManagerW(null, null, 1);
        CheckHandle(manager);
        // SERVICE_START is also required for SC_ACTION_RESTART.
        using var service = OpenServiceW(manager, WindowsServiceRegistration.ProductServiceName, 1 | 2 | 16);
        CheckHandle(service);
        var size = Marshal.SizeOf<NativeAction>();
        // cActions=0 with a NON-null pointer deletes existing actions; null means leave unchanged.
        var actions = Marshal.AllocHGlobal(Math.Max(size, checked(configuration.Actions.Count * size)));
        try
        {
            for (var i = 0; i < configuration.Actions.Count; i++)
            {
                var action = configuration.Actions[i];
                Marshal.StructureToPtr(new NativeAction { Type = action.Type, Delay = action.DelayMilliseconds },
                    IntPtr.Add(actions, i * size), false);
            }
            var native = new NativeFailureActions
            {
                ResetPeriod = configuration.ResetPeriodSeconds,
                Count = (uint)configuration.Actions.Count,
                Actions = actions
            };
            Check(ChangeServiceConfig2W(service, 2, ref native));
            // Do not set SERVICE_CONFIG_FAILURE_ACTIONS_FLAG. A newly installed service defaults to crash-only.
        }
        finally { Marshal.FreeHGlobal(actions); }
    }

    public ServiceRecoveryConfiguration Read()
    {
        using var manager = OpenSCManagerW(null, null, 1);
        CheckHandle(manager);
        using var service = OpenServiceW(manager, WindowsServiceRegistration.ProductServiceName, 1);
        CheckHandle(service);
        var buffer = Query(service, 2, out var bytes);
        try
        {
            var native = Marshal.PtrToStructure<NativeFailureActions>(buffer);
            var size = Marshal.SizeOf<NativeAction>();
            var offset = native.Actions.ToInt64() - buffer.ToInt64();
            if (native.Count > 0 && (native.Actions == IntPtr.Zero || offset < 0
                || offset + (long)native.Count * size > bytes))
                throw new InvalidDataException("Invalid service failure action buffer.");
            var actions = new ServiceRecoveryAction[checked((int)native.Count)];
            for (var i = 0; i < actions.Length; i++)
            {
                var action = Marshal.PtrToStructure<NativeAction>(IntPtr.Add(native.Actions, checked(i * size)));
                actions[i] = new(action.Type, action.Delay);
            }
            var flag = Query(service, 4, out _);
            try { return new(native.ResetPeriod, actions, Marshal.ReadInt32(flag) != 0); }
            finally { Marshal.FreeHGlobal(flag); }
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static IntPtr Query(ServiceHandle service, uint level, out uint bytes)
    {
        _ = QueryServiceConfig2W(service, level, IntPtr.Zero, 0, out bytes);
        var error = Marshal.GetLastWin32Error();
        if (error != 122) throw new Win32Exception(error);
        // QueryServiceConfig2 has a documented 8 KiB maximum.
        if (bytes < (level == 2 ? Marshal.SizeOf<NativeFailureActions>() : sizeof(int)) || bytes > 8192)
            throw new InvalidDataException("Invalid service configuration buffer size.");
        var buffer = Marshal.AllocHGlobal((int)bytes);
        try { Check(QueryServiceConfig2W(service, level, buffer, bytes, out _)); return buffer; }
        catch { Marshal.FreeHGlobal(buffer); throw; }
    }

    private static void Check(bool success)
    {
        if (!success) throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    private static void CheckHandle(ServiceHandle handle)
    {
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeAction { public uint Type; public uint Delay; }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeFailureActions
    {
        public uint ResetPeriod;
        public IntPtr RebootMessage;
        public IntPtr Command;
        public uint Count;
        public IntPtr Actions;
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
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr handle);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ChangeServiceConfig2W(ServiceHandle service, uint level, ref NativeFailureActions value);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceConfig2W(ServiceHandle service, uint level, IntPtr buffer, uint size, out uint needed);
}
