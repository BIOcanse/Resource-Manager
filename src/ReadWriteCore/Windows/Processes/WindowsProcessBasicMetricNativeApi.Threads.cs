using System.Runtime.InteropServices;
using ResourceManager.ReadWrite.ProcessFields;

namespace ResourceManager.ReadWrite.Windows.Processes;

internal sealed partial class WindowsProcessBasicMetricNativeApi
{
    internal const uint QueryLimitedInformation = 0x1000, Synchronize = 0x100000;
    public IntPtr Open(int processId, ProcessMetricReadMask mask)
        => OpenSelected(processId, mask, OpenProcess);
    internal static IntPtr OpenSelected(int processId, ProcessMetricReadMask mask, Func<uint, bool, int, IntPtr> open)
    {
        if (mask.HasFlag(ProcessMetricReadMask.ThreadCount))
        {
            var handle = open(QueryLimitedInformation | Synchronize, false, processId);
            if (handle != IntPtr.Zero && handle != new IntPtr(-1)) return handle;
            // Extra synchronization permission must not suppress independently readable mixed fields.
        }
        return open(QueryLimitedInformation, false, processId);
    }
    public IReadOnlyDictionary<int, uint?> ThreadCounts(IReadOnlyDictionary<int, IntPtr> handles)
        => new WindowsProcessThreadCountQuery(new WindowsProcessThreadCountCalls()).ReadBatch(handles);
    public bool ThreadCountLive(IntPtr handle, int pid)
    {
        var calls = new WindowsProcessThreadCountCalls();
        return calls.ProcessId(handle) == (uint)pid && calls.State(handle) == WindowsProcessThreadCountQuery.WaitTimeout;
    }
    public uint? ThreadCount(IntPtr handle, int processId)
        => new WindowsProcessThreadCountQuery(new WindowsProcessThreadCountCalls()).Read(handle, processId);
}

internal interface IWindowsProcessThreadCountCalls
{
    uint ProcessId(IntPtr process);
    uint State(IntPtr process);
    IntPtr Snapshot();
    bool First(IntPtr snapshot, ref WindowsProcessEntry32 entry);
    bool Next(IntPtr snapshot, ref WindowsProcessEntry32 entry);
    bool Close(IntPtr snapshot);
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct WindowsProcessEntry32
{
    internal uint Size, Usage, ProcessId;
    internal UIntPtr DefaultHeapId;
    internal uint ModuleId, ThreadCount, ParentProcessId;
    internal int BasePriority;
    internal uint Flags;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] internal string ExeFile;
}

internal sealed class WindowsProcessThreadCountQuery(IWindowsProcessThreadCountCalls calls)
{
    internal const int MaximumEntries = 131072;
    internal const uint WaitTimeout = 0x102;
    internal uint? Read(IntPtr process, int expectedPid)
        => ReadBatch(new Dictionary<int, IntPtr> { [expectedPid] = process }).GetValueOrDefault(expectedPid);

    internal IReadOnlyDictionary<int, uint?> ReadBatch(IReadOnlyDictionary<int, IntPtr> processes)
    {
        var live = processes.Where(pair => pair.Key > 0 && calls.ProcessId(pair.Value) == (uint)pair.Key
            && calls.State(pair.Value) == WaitTimeout).ToDictionary(pair => pair.Key, pair => pair.Value);
        var counts = new Dictionary<int, uint?>();
        if (live.Count == 0) return counts;
        var snapshot = calls.Snapshot();
        if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1)) return counts;
        var released = false;
        try
        {
            var entry = new WindowsProcessEntry32 { Size = checked((uint)Marshal.SizeOf<WindowsProcessEntry32>()), ExeFile = string.Empty };
            var present = calls.First(snapshot, ref entry);
            for (var examined = 0; present && examined < MaximumEntries; examined++)
            {
                if (entry.ProcessId <= int.MaxValue && live.ContainsKey((int)entry.ProcessId)) counts[(int)entry.ProcessId] = entry.ThreadCount;
                if (counts.Count == live.Count) break;
                present = calls.Next(snapshot, ref entry);
            }
            foreach (var pair in live)
                if (calls.State(pair.Value) != WaitTimeout) counts.Remove(pair.Key);
        }
        finally { released = calls.Close(snapshot); }
        return released ? counts : new Dictionary<int, uint?>();
    }

}

internal sealed class WindowsProcessThreadCountCalls : IWindowsProcessThreadCountCalls
{
    public uint ProcessId(IntPtr process) => GetProcessId(process);
    public uint State(IntPtr process) => WaitForSingleObject(process, 0);
    public IntPtr Snapshot() => CreateToolhelp32Snapshot(2, 0); // TH32CS_SNAPPROCESS only: no heaps/modules/thread walk.
    public bool First(IntPtr snapshot, ref WindowsProcessEntry32 entry) => Process32FirstW(snapshot, ref entry);
    public bool Next(IntPtr snapshot, ref WindowsProcessEntry32 entry) => Process32NextW(snapshot, ref entry);
    public bool Close(IntPtr snapshot) => WindowsProcessBasicMetricNativeApi.CloseHandle(snapshot);
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint GetProcessId(IntPtr process);
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint WaitForSingleObject(IntPtr process, uint milliseconds);
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true, CharSet = CharSet.Unicode)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Process32FirstW(IntPtr snapshot, ref WindowsProcessEntry32 entry);
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true, CharSet = CharSet.Unicode)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Process32NextW(IntPtr snapshot, ref WindowsProcessEntry32 entry);
}
