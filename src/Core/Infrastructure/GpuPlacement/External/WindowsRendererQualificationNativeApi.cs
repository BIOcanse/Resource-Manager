using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using ResourceManager.ReadWrite.Windows.Processes;

namespace ResourceManager.App.Infrastructure.GpuPlacement.External;

internal sealed class WindowsRendererQualificationNativeApi : IRendererQualificationNativeApi
{
    internal static readonly WindowsRendererQualificationNativeApi Instance = new();
    private readonly WindowsProcessBasicMetricNativeApi processApi = new();
    private readonly WindowsProcessDetailsNativeApi detailsApi = new();
    public IntPtr Open(int processId) => detailsApi.OpenProcess(0x00101410, processId); // synchronize, query, limited query, VM read
    public void Close(IntPtr process) => detailsApi.CloseHandle(process);
    public RendererProcessIdentity? ReadIdentity(IntPtr process, int processId)
    {
        if (detailsApi.GetProcessId(process) != (uint)processId || !detailsApi.IsRunning(process)
            || !WindowsProcessBasicMetricNativeApi.GetProcessTimes(process, out var creation, out var exit, out _, out _)
            || exit.ToUInt64() != 0 || creation.ToUInt64() == 0
            || !ResourceManager.App.Infrastructure.Windows.NativeMethods.ProcessIdToSessionId((uint)processId, out var session)) return null;
        var image = processApi.ImagePath(process); // The existing ReadWriteCore image-path reader, borrowing this handle.
        return string.IsNullOrWhiteSpace(image) ? null : new(processId, creation.ToUInt64(), image, session);
    }
    public string[] ReadArguments(IntPtr process)
    {
        var pointer = CommandLineToArgvW(WindowsProcessCommandLineQuery.Read(process), out var count);
        if (pointer == IntPtr.Zero) throw new Win32Exception();
        try
        {
            if (count <= 0 || count > 4096) throw new InvalidOperationException("Invalid argument count.");
            var arguments = new string[count];
            for (var index = 0; index < count; index++) arguments[index] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(pointer, index * IntPtr.Size)) ?? string.Empty;
            return arguments;
        }
        finally { _ = LocalFree(pointer); }
    }
    public int? ReadParentId(IntPtr process) => processApi.ParentPid(process) is uint pid && pid <= int.MaxValue ? (int)pid : null;
    public bool? IsDebugged(IntPtr process) => CheckRemoteDebuggerPresent(process, out var value) ? value : null;
    public bool IsAmd64(string path) => WindowsIfeoGpuLaunchInterceptionRegistry.TryReadMachine(path, out var machine) && machine == 0x8664;

    public IReadOnlyList<RendererModuleName>? ReadModuleNames(IntPtr process)
    {
        // One bounded enumeration. A truncated or changing table is unavailable, never retried here.
        var addresses = new IntPtr[WindowsProcessDetailsOptions.Default.MaximumModulesPerProcess];
        var capacity = checked((uint)(addresses.Length * IntPtr.Size));
        if (!EnumProcessModulesEx(process, addresses, capacity, out var needed, 3)
            || needed > capacity || needed % IntPtr.Size != 0) return null;
        var names = new List<RendererModuleName>(checked((int)(needed / IntPtr.Size)));
        var buffer = new StringBuilder(512);
        var seen = new HashSet<IntPtr>();
        for (var index = 0; index < needed / IntPtr.Size; index++)
        {
            var module = addresses[index];
            if (module == IntPtr.Zero || !seen.Add(module)) return null;
            buffer.Clear();
            var length = GetModuleBaseNameW(process, module, buffer, (uint)buffer.Capacity);
            if (length == 0 || length >= buffer.Capacity - 1) return null;
            names.Add(new(buffer.ToString(), module));
        }
        return names;
    }
    public string? ReadModulePath(IntPtr process, RendererModuleName module)
    {
        var buffer = new StringBuilder(32768);
        var length = GetModuleFileNameExW(process, module.Address, buffer, (uint)buffer.Capacity);
        return length > 0 && length < buffer.Capacity - 1 ? buffer.ToString() : null;
    }
    public int? ReadModuleSize(IntPtr process, RendererModuleName module)
        => GetModuleInformation(process, module.Address, out var info, (uint)Marshal.SizeOf<ModuleInformation>())
            && info.Size <= int.MaxValue ? (int)info.Size : null;
    public Version ReadVersion(string path)
    {
        var version = FileVersionInfo.GetVersionInfo(path);
        return new(version.FileMajorPart, version.FileMinorPart, version.FileBuildPart);
    }
    public string HashFile(string path) { using var file = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(file)); }
    public byte[]? ReadMemory(IntPtr process, IntPtr address, int length)
    {
        var buffer = new byte[length];
        return ReadProcessMemory(process, address, buffer, (nuint)length, out var read) && read == (nuint)length ? buffer : null;
    }
    [StructLayout(LayoutKind.Sequential)] private struct ModuleInformation { internal IntPtr Base; internal uint Size; internal IntPtr Entry; }
    [DllImport("psapi.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumProcessModulesEx(IntPtr process, [Out] IntPtr[] modules, uint bytes, out uint needed, uint filter);
    [DllImport("psapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetModuleBaseNameW(IntPtr process, IntPtr module, StringBuilder name, uint length);
    [DllImport("psapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetModuleFileNameExW(IntPtr process, IntPtr module, StringBuilder name, uint length);
    [DllImport("psapi.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetModuleInformation(IntPtr process, IntPtr module, out ModuleInformation information, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CheckRemoteDebuggerPresent(IntPtr process, [MarshalAs(UnmanagedType.Bool)] out bool present);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadProcessMemory(IntPtr process, IntPtr address, [Out] byte[] buffer, nuint size, out nuint read);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CommandLineToArgvW(string commandLine, out int count);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr pointer);
}
