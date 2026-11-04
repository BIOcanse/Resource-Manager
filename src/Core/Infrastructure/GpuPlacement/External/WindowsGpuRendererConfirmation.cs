using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using ResourceManager.App.Domain.GpuPlacement;

namespace ResourceManager.App.Infrastructure.GpuPlacement.External;

// Reads the graphics kernel's registered process/adapter client. It does not attach
// a debugger, invoke target code, or infer a backend from loaded libraries.
internal static class WindowsGpuRendererConfirmation
{
    internal sealed record AdapterClient(ulong AdapterKey, int Status, uint ClientHint)
    {
        public GpuGraphicsApi? GraphicsApi => Status == 0 ? Classify(ClientHint) : null;
    }

    internal static GpuGraphicsApi? Classify(uint hint) => hint switch
    {
        11 => GpuGraphicsApi.D3D11,
        12 => GpuGraphicsApi.D3D12,
        4 => GpuGraphicsApi.Vulkan,
        1 => GpuGraphicsApi.OpenGL,
        9 => GpuGraphicsApi.D3D9,
        13 => GpuGraphicsApi.D3D9 | GpuGraphicsApi.D3D12,
        14 => GpuGraphicsApi.D3D11 | GpuGraphicsApi.D3D12,
        16 => GpuGraphicsApi.OpenGL | GpuGraphicsApi.D3D12,
        21 => GpuGraphicsApi.Vulkan | GpuGraphicsApi.D3D12,
        _ => null
    };

    internal static bool ConfirmsOnlyD3D11(IReadOnlyList<AdapterClient> clients)
        => clients.Any(client => client.Status == 0 && client.ClientHint == 11)
            && clients.Where(client => client.Status == 0).All(client => client.ClientHint == 11);

    internal static IReadOnlyList<AdapterClient> Read(GpuPlacementProcessInstance expected,
        IEnumerable<ulong> adapterKeys)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)
            || RuntimeInformation.ProcessArchitecture != Architecture.X64
            || expected.ProcessId <= 4 || expected.ProcessStartKey == 0) return [];
        using var process = OpenProcess(0x400, false, expected.ProcessId);
        if (process.IsInvalid || !Matches(process, expected)) return [];
        var result = new List<AdapterClient>();
        foreach (var adapter in adapterKeys.Where(key => key != 0).Distinct())
        {
            var query = new Query { Type = 2, Adapter = adapter, Process = process.DangerousGetHandle() };
            var status = D3DKMTQueryStatistics(ref query);
            result.Add(new(adapter, status, status == 0 ? query.ClientHint : 0));
        }
        GC.KeepAlive(process);
        return Matches(process, expected) ? result : [];
    }

    private static bool Matches(SafeProcessHandle process, GpuPlacementProcessInstance expected)
    {
        if (!GetProcessTimes(process, out var creation, out var exit, out _, out _)
            || creation != expected.ProcessStartKey || exit != 0) return false;
        var path = new char[32768];
        var length = path.Length;
        return QueryFullProcessImageName(process, 0, path, ref length)
            && StringComparer.OrdinalIgnoreCase.Equals(new string(path, 0, length), expected.ExecutablePath);
    }

    // Windows SDK 10.0.26100.0 d3dkmthk.h x64 layout. The result union starts at
    // 24; PROCESS_ADAPTER_INFORMATION.ClientHint is at 264 within that union.
    [StructLayout(LayoutKind.Explicit, Size = 0x328)]
    internal struct Query
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(4)] public ulong Adapter;
        [FieldOffset(16)] public IntPtr Process;
        [FieldOffset(288)] public uint ClientHint;
    }

    [DllImport("gdi32.dll")]
    private static extern int D3DKMTQueryStatistics(ref Query query);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int processId);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(SafeProcessHandle process, out ulong creation,
        out ulong exit, out ulong kernel, out ulong user);
    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle process, int flags,
        [Out] char[] path, ref int length);
}
