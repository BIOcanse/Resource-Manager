using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace MemoryMeasurement;

internal static class Native
{
    [StructLayout(LayoutKind.Sequential)] internal struct Mbi
    {
        public ulong Base, AllocationBase;
        public uint AllocationProtect, PartitionAndPadding;
        public ulong Size;
        public uint State, Protect, Type, Padding;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct Ws { public ulong Address, Flags; }
    [DllImport("kernel32", SetLastError = true)] internal static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32", SetLastError = true)] internal static extern nuint VirtualQueryEx(SafeProcessHandle process, ulong address, out Mbi info, nuint length);
    [DllImport("kernel32", SetLastError = true)] internal static extern bool ReadProcessMemory(SafeProcessHandle process, ulong address, [Out] byte[] buffer, nuint size, out nuint read);
    [DllImport("psapi", CharSet = CharSet.Unicode, SetLastError = true)] static extern uint GetMappedFileNameW(SafeProcessHandle process, ulong address, StringBuilder name, int length);
    [DllImport("psapi", SetLastError = true)] internal static extern bool QueryWorkingSetEx(SafeProcessHandle process, [In, Out] Ws[] pages, int bytes);
    [DllImport("kernel32", SetLastError = true)] internal static extern IntPtr OpenThread(uint access, bool inherit, int tid);
    [DllImport("kernel32", SetLastError = true)] internal static extern uint GetProcessIdOfThread(IntPtr thread);
    [DllImport("kernel32")] internal static extern bool CloseHandle(IntPtr handle);
    [DllImport("ntdll")] internal static extern int NtQueryInformationThread(IntPtr thread, int kind, out ulong address, int size, out int returned);
    [DllImport("kernel32", SetLastError = true)] internal static extern bool GetThreadTimes(IntPtr thread, out long created, out long exited, out long kernel, out long user);
    [DllImport("kernel32", SetLastError = true)] internal static extern bool IsWow64Process(SafeProcessHandle process, out bool wow64);
    [DllImport("kernel32", SetLastError = true)] internal static extern ulong VirtualAlloc(ulong address, nuint size, uint allocation, uint protect);
    [DllImport("kernel32", SetLastError = true)] internal static extern bool VirtualFree(ulong address, nuint size, uint kind);
    [DllImport("kernel32", SetLastError = true)] internal static extern bool VirtualProtect(ulong address, nuint size, uint protect, out uint previous);

    internal static SafeProcessHandle Open(int pid)
    {
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess) throw new NotSupportedException("Use 64-bit Windows and 64-bit pwsh/dotnet.");
        var handle = OpenProcess(0x410, false, pid); // QUERY_INFORMATION | VM_READ; never VM_WRITE, VM_OPERATION or SUSPEND_RESUME.
        if (handle.IsInvalid) { handle.Dispose(); throw new Win32Exception(Marshal.GetLastWin32Error()); }
        if (!IsWow64Process(handle, out bool wow64)) { handle.Dispose(); throw new Win32Exception(Marshal.GetLastWin32Error()); }
        if (wow64) { handle.Dispose(); throw new NotSupportedException("32-bit targets are not supported by this x64 tool."); }
        return handle;
    }
    internal static string MappedName(SafeProcessHandle handle, ulong address)
    {
        var name = new StringBuilder(32768);
        return GetMappedFileNameW(handle, address, name, name.Capacity) == 0 ? "" : name.ToString();
    }
    internal static int Read(SafeProcessHandle handle, ulong address, byte[] buffer, int length)
    {
        ReadProcessMemory(handle, address, buffer, (nuint)length, out nuint read);
        return (int)read;
    }
}

internal sealed class Region
{
    public int Id { get; set; }
    public ulong Address { get; set; }
    public string AddressHex => $"0x{Address:x}";
    public ulong Bytes { get; set; }
    public uint Protection { get; set; }
    public uint NativeType { get; set; }
    public bool Readable { get; set; }
    public string Class { get; set; } = "";
    public string Module { get; set; } = "";
    public ulong ObservedZeroBytes { get; set; }
    public string Key => Class + (Module.Length == 0 ? "" : ":" + Module);
}

internal sealed class Budget
{
    readonly Stopwatch clock = Stopwatch.StartNew();
    readonly TimeSpan maximum;
    internal Budget(int windowSeconds = 0) => maximum = TimeSpan.FromSeconds(1800L + windowSeconds);
    internal void Check()
    {
        JobStop.Check();
        if (clock.Elapsed > maximum) throw new TimeoutException("Measurement time bound exceeded; results are incomplete.");
    }
}

internal static class JobStop
{
    static readonly string? FileName = Environment.GetEnvironmentVariable("JOBQUEUE_STOP_FILE");
    static readonly Stopwatch Clock = Stopwatch.StartNew();
    static long nextCheck;
    static bool requested;
    internal static void Check()
    {
        if (FileName is null) return;
        if (!requested && Clock.ElapsedMilliseconds >= nextCheck)
        {
            requested = File.Exists(FileName);
            nextCheck = Clock.ElapsedMilliseconds + 1000;
        }
        if (requested) throw new OperationCanceledException("JobQueue requested a safe stop; no complete measurement was produced.");
    }
}

internal static class AddressSpace
{
    internal static List<Region> Read(SafeProcessHandle process, IReadOnlyList<HeapRange> gc, Budget budget)
    {
        var regions = new List<Region>(); ulong address = 0; int queries = 0;
        while (address < 0x0000800000000000)
        {
            budget.Check(); if (++queries > 2_000_000) throw new InvalidOperationException("VirtualQueryEx query bound exceeded.");
            if (Native.VirtualQueryEx(process, address, out var info, 48) == 0)
            {
                int error = Marshal.GetLastWin32Error();
                if (error == 87) break; // Above maximum user address.
                throw new Win32Exception(error, $"VirtualQueryEx failed at 0x{address:x}; address walk incomplete.");
            }
            ulong next = checked(info.Base + info.Size);
            if (next <= address) throw new InvalidDataException("VirtualQueryEx did not advance.");
            if (info.State == 0x1000)
            {
                uint protection = info.Protect & 0xff;
                bool readable = (info.Protect & 0x100) == 0 && protection is 2 or 4 or 8 or 0x20 or 0x40 or 0x80;
                string cls = info.Type switch { 0x1000000 => "image", 0x40000 => "mapped", 0x20000 => "private", _ => "unknown" };
                string module = cls is "image" or "mapped" ? Native.MappedName(process, info.Base) : "";
                // Split private regions at GC committed-range boundaries so GC is disjoint, not double-counted.
                var cuts = new SortedSet<ulong> { info.Base, next };
                if (cls == "private")
                {
                    int g = 0;
                    while (g < gc.Count)
                    {
                        var range = gc[g++];
                        if (range.Start < next && range.End > info.Base) { cuts.Add(Math.Max(info.Base, range.Start)); cuts.Add(Math.Min(next, range.End)); }
                    }
                }
                ulong previous = info.Base;
                foreach (ulong cut in cuts)
                {
                    if (cut == previous) continue;
                    bool isGc = cls == "private" && gc.Any(r => r.Start <= previous && previous < r.End);
                    if (regions.Count >= 65536) throw new InvalidOperationException("65536 committed-region bound exceeded.");
                    regions.Add(new Region { Id = regions.Count, Address = previous, Bytes = cut - previous, Protection = info.Protect,
                        NativeType = info.Type, Readable = readable, Class = isGc ? "gc-heap" : cls, Module = module });
                    previous = cut;
                }
            }
            address = next;
        }
        return regions;
    }
}
