using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using ResourceManager.App.Application.GpuPlacement;
using ResourceManager.App.Domain.GpuPlacement;

namespace ResourceManager.App.Infrastructure.GpuPlacement.External;

internal static class ChromiumGpuProcessIdentity
{
    internal static bool HasCompatibleRenderer(IReadOnlyList<string> arguments, GpuGraphicsApi? confirmedApi)
    {
        if (confirmedApi != GpuGraphicsApi.D3D11) return false;
        var processTypes = arguments.Where(argument => argument.StartsWith("--type=", StringComparison.Ordinal)).ToArray();
        if (processTypes.Length != 1 || processTypes[0] != "--type=gpu-process") return false;
        foreach (var argument in arguments)
        {
            if (argument.StartsWith("--use-angle=", StringComparison.Ordinal)
                && argument is not "--use-angle=d3d11" and not "--use-angle=default") return false;
            if (argument.StartsWith("--use-gl=", StringComparison.Ordinal)
                && argument is not "--use-gl=angle" and not "--use-gl=default") return false;
            if (argument.StartsWith("--enable-features=", StringComparison.Ordinal)
                && argument[18..].Split(',').Any(feature => feature.Trim().Split(':', '<', '.')[0] == "Vulkan")) return false;
        }
        return true;
    }

    internal static ExternalGpuPlacementPlan? Read(GpuPlacementProcessInstance expected, GpuGraphicsApi? confirmedApi)
    {
        if (string.IsNullOrWhiteSpace(expected.ExecutablePath) || expected.ProcessId <= 4 || expected.ProcessStartKey == 0)
            return null;
        try
        {
            if (!WindowsIfeoGpuLaunchInterceptionRegistry.TryReadMachine(expected.ExecutablePath, out var machine)
                || machine != 0x8664) return null;
            using var gpu = Process.GetProcessById(expected.ProcessId);
            if (!Matches(gpu, expected) || !HasCompatibleRenderer(Arguments(gpu), confirmedApi))
                return null;
            var basic = new BasicInformation();
            if (NtQueryInformationProcess(gpu.Handle, 0, ref basic, Marshal.SizeOf<BasicInformation>(), out _) < 0)
                return null;
            using var browser = Process.GetProcessById(checked((int)basic.ParentId));
            var browserStartKey = checked((ulong)browser.StartTime.ToUniversalTime().ToFileTimeUtc());
            var browserImage = browser.MainModule?.FileName;
            if (browserStartKey > expected.ProcessStartKey || browser.SessionId == 0 || browser.SessionId != gpu.SessionId
                || string.IsNullOrWhiteSpace(browserImage)
                || Arguments(browser).Any(argument => argument.StartsWith("--type=", StringComparison.Ordinal))
                || !CheckRemoteDebuggerPresent(browser.Handle, out var debugged) || debugged) return null;
            return new(new(browser.Id, browserStartKey,
                browser.ProcessName, browserImage), expected);
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException or ArgumentException or OverflowException)
        {
            return null;
        }
    }

    internal static bool Matches(Process process, GpuPlacementProcessInstance expected)
        => !process.HasExited && checked((ulong)process.StartTime.ToUniversalTime().ToFileTimeUtc()) == expected.ProcessStartKey
            && string.Equals(process.MainModule?.FileName, expected.ExecutablePath, StringComparison.OrdinalIgnoreCase);

    private static string[] Arguments(Process process)
    {
        var pointer = CommandLineToArgvW(CommandLine(process), out var count);
        if (pointer == IntPtr.Zero) throw new Win32Exception();
        try
        {
            if (count <= 0 || count > 4096) throw new InvalidOperationException("Invalid argument count.");
            var arguments = new string[count];
            for (var index = 0; index < count; index++)
                arguments[index] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(pointer, index * IntPtr.Size)) ?? string.Empty;
            return arguments;
        }
        finally { _ = LocalFree(pointer); }
    }

    private static string CommandLine(Process process)
    {
        _ = NtQueryBuffer(process.Handle, 60, IntPtr.Zero, 0, out var length);
        if (length < Marshal.SizeOf<UnicodeString>() || length > ushort.MaxValue)
            throw new InvalidOperationException("Invalid process command line length.");
        var buffer = Marshal.AllocHGlobal(length);
        try
        {
            if (NtQueryBuffer(process.Handle, 60, buffer, length, out _) < 0)
                throw new InvalidOperationException("Cannot read process command line.");
            var value = Marshal.PtrToStructure<UnicodeString>(buffer);
            var offset = value.Buffer.ToInt64() - buffer.ToInt64();
            if (offset < 0 || offset + value.Length > length || value.Length % 2 != 0)
                throw new InvalidOperationException("Invalid process command line bounds.");
            return Marshal.PtrToStringUni(value.Buffer, value.Length / 2) ?? string.Empty;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString { public ushort Length, MaximumLength; public IntPtr Buffer; }
    [StructLayout(LayoutKind.Sequential)]
    private struct BasicInformation { public IntPtr Exit, Peb, Affinity, Priority, Id, ParentId; }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CommandLineToArgvW(string commandLine, out int count);
    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr pointer);
    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr process, int kind, ref BasicInformation information, int length, out int returned);
    [DllImport("ntdll.dll", EntryPoint = "NtQueryInformationProcess")]
    private static extern int NtQueryBuffer(IntPtr process, int kind, IntPtr information, int length, out int returned);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CheckRemoteDebuggerPresent(IntPtr process, [MarshalAs(UnmanagedType.Bool)] out bool present);
}
