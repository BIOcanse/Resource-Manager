using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ResourceManager.App.Infrastructure.NativeCore;

internal static partial class WindowsNativeAtomicFileCommitter
{
    // Kernel traces show transient Defender scan contention (rename) and a concurrent POSIX
    // target lookup window (open). Bound only those exact statuses to two 10-ms backoffs.
    private const int TransientAttemptLimit = 3;
    private const int TransientRetryDelayMilliseconds = 10;
    private const int StatusAccessDenied = unchecked((int)0xC0000022);
    private const int StatusObjectNameNotFound = unchecked((int)0xC0000034);

    private static string ToNtPath(string extendedPath) => @"\??\" + extendedPath[4..];

    private static void LogTransient(string operation, int status, string path, int attempt,
        string outcome, string? source = null)
        => Trace.WriteLine($"AtomicFileCommit operation={operation} status=0x{status:X8} " +
            $"path=\"{path}\" source=\"{source}\" attempt={attempt}/{TransientAttemptLimit} outcome={outcome}");

    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatusBlock
    {
        public IntPtr Status;
        public nuint Information;
    }

    [LibraryImport("ntdll.dll")]
    private static partial uint RtlNtStatusToDosError(int status);
}
