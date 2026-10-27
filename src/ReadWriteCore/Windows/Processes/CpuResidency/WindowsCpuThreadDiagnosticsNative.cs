using System.Runtime.InteropServices;

namespace ResourceManager.ReadWrite.Windows.Processes;

internal sealed record WindowsSystemCpuThread(int ThreadId, long CreationFileTime100Ns,
    long KernelTime100Ns, long UserTime100Ns, ulong StartAddress);

internal static class WindowsSystemCpuThreadInventory
{
    // Reuse the bounded native transaction and validated table layout, not Process.Threads.
    internal static bool TryRead(int processId, long creation, out IReadOnlyList<WindowsSystemCpuThread> threads)
    {
        threads = [];
        var options = WindowsProcessDetailsOptions.Default;
        if (!new WindowsProcessDetailsNativeApi(options).TryReadSystemProcessInformation(out var bytes)
            || !WindowsProcessSystemThreadParser.TryParse(bytes, new HashSet<int> { processId }, options, out _)
            || !WindowsProcessSystemThreadParser.TryParseCreations(bytes, options, out var births)
            || !births.TryGetValue(processId, out var actual) || actual != creation) return false;
        var offset = 0;
        while (offset < bytes.Length)
        {
            var header = MemoryMarshal.Read<WindowsProcessSystemThreadParser.SystemProcessInformationHeader>(bytes.AsSpan(offset));
            if (header.UniqueProcessId.ToInt64() == processId)
            {
                var result = new List<WindowsSystemCpuThread>((int)header.NumberOfThreads);
                for (var index = 0; index < header.NumberOfThreads; index++)
                {
                    var thread = MemoryMarshal.Read<WindowsProcessSystemThreadParser.SystemThreadInformation>(bytes.AsSpan(
                        offset + WindowsProcessSystemThreadParser.ProcessHeaderSize + index * WindowsProcessSystemThreadParser.ThreadInformationSize));
                    // SYSTEM_THREAD_INFORMATION's first three LARGE_INTEGERs: kernel, user, creation.
                    result.Add(new(checked((int)thread.ClientThreadId.ToInt64()), thread.Reserved1c,
                        thread.Reserved1a, thread.Reserved1b, unchecked((ulong)thread.StartAddress.ToInt64())));
                }
                threads = result;
                return true;
            }
            if (header.NextEntryOffset == 0) break;
            offset += checked((int)header.NextEntryOffset);
        }
        return false;
    }
}

internal sealed partial class WindowsProcessDetailsNativeApi
{
    internal static IntPtr OpenDiagnosticThread(int pid, int tid, long birth, out long creation,
        out long kernel, out long user, out int error)
    {
        creation = kernel = user = 0;
        // QUERY_INFORMATION includes the limited query rights required by GetThreadDescription/Times.
        var handle = OpenThread(0x0040, false, unchecked((uint)tid));
        error = handle == IntPtr.Zero ? Marshal.GetLastWin32Error() : 0;
        if (handle == IntPtr.Zero) return handle;
        if (GetProcessIdOfThread(handle) != (uint)pid) error = 1168;
        else if (!ReadDiagnosticTimes(handle, out creation, out kernel, out user, out _)) error = Marshal.GetLastWin32Error();
        else if (birth != 0 && birth != creation) error = 1168;
        else return handle;
        CloseHandleNative(handle);
        return IntPtr.Zero;
    }

    internal static bool ReadDiagnosticTimes(IntPtr handle, out long creation, out long kernel, out long user, out long exit)
    {
        creation = kernel = user = exit = 0;
        if (!GetThreadTimes(handle, out var c, out var e, out var k, out var u)) return false;
        creation = Join(c); exit = Join(e); kernel = Join(k); user = Join(u);
        return true;
    }
    private static long Join(FileTime value) => unchecked((long)(((ulong)value.High << 32) | value.Low));
    internal static (string? Name, int? Error) ReadDiagnosticName(IntPtr handle)
    {
        var status = GetThreadDescription(handle, out var text);
        try
        {
            var name = status < 0 ? null : Marshal.PtrToStringUni(text);
            return (string.IsNullOrEmpty(name) ? null : name, status < 0 ? status : null);
        }
        finally { if (text != IntPtr.Zero) LocalFree(text); }
    }
    internal static ulong? ReadDiagnosticStartAddress(IntPtr handle)
        => NtQueryInformationThread(handle, 9, out var address, IntPtr.Size, IntPtr.Zero) == 0
            ? unchecked((ulong)address.ToInt64()) : null;
    internal static void CloseDiagnosticThread(IntPtr handle) => CloseHandleNative(handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr OpenThread(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint tid);
    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint GetProcessIdOfThread(IntPtr thread);
    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetThreadTimes(IntPtr thread, out FileTime creation, out FileTime exit, out FileTime kernel, out FileTime user);
    [DllImport("kernel32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int GetThreadDescription(IntPtr thread, out IntPtr description);
    [DllImport("kernel32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr LocalFree(IntPtr memory);
    [DllImport("ntdll.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int NtQueryInformationThread(IntPtr thread, int informationClass, out IntPtr address, int length, IntPtr returned);
}
