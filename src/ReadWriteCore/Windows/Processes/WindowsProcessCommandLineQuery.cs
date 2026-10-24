using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ResourceManager.ReadWrite.Windows.Processes;

internal sealed partial class WindowsProcessBasicMetricNativeApi
{
    public string? CommandLine(IntPtr handle) => WindowsProcessCommandLineQuery.Read(handle);
}

internal interface IWindowsProcessCommandLineCalls
{
    int Query(IntPtr process, int kind, IntPtr buffer, int length, out int returned);
    IntPtr Allocate(int length);
    void Free(IntPtr buffer);
}

/// <summary>The original Chromium class60 raw read. It borrows the supplied handle;
/// caller owns its creation-identity gate and lifetime. No process is opened here.</summary>
internal static class WindowsProcessCommandLineQuery
{
    internal const int InformationClass = 60, MaximumBytes = ushort.MaxValue;
    internal static string Read(IntPtr process) => Read(process, NativeCalls.Instance);
    internal static string Read(SafeProcessHandle process)
    {
        ArgumentNullException.ThrowIfNull(process);
        var addedReference = false;
        try
        {
            process.DangerousAddRef(ref addedReference);
            return Read(process.DangerousGetHandle());
        }
        finally
        {
            if (addedReference) process.DangerousRelease();
        }
    }
    internal static string Read(IntPtr process, IWindowsProcessCommandLineCalls calls)
    {
        _ = calls.Query(process, InformationClass, IntPtr.Zero, 0, out var length);
        var header = Marshal.SizeOf<UnicodeString>();
        if (length < header || length > MaximumBytes)
            throw new InvalidOperationException("Invalid process command line length.");
        var buffer = calls.Allocate(length);
        if (buffer == IntPtr.Zero) throw new InvalidOperationException("Cannot allocate process command line buffer.");
        try
        {
            if (calls.Query(process, InformationClass, buffer, length, out var returned) < 0)
                throw new InvalidOperationException("Cannot read process command line.");
            if (returned < header || returned > length)
                throw new InvalidOperationException("Invalid process command line returned length.");
            var value = Marshal.PtrToStructure<UnicodeString>(buffer);
            if (value.Length % 2 != 0 || value.MaximumLength % 2 != 0 || value.Length > value.MaximumLength)
                throw new InvalidOperationException("Invalid process command line UTF-16 bounds.");
            if (value.Length == 0 && value.Buffer == IntPtr.Zero && value.MaximumLength == 0) return string.Empty;
            var offset = value.Buffer.ToInt64() - buffer.ToInt64();
            if (offset < header || offset > returned || value.Length > returned - offset)
                throw new InvalidOperationException("Invalid process command line bounds.");
            if (value.Length == 0) return string.Empty;
            return Marshal.PtrToStringUni(value.Buffer, value.Length / 2) ?? string.Empty; // Exact raw UTF-16 code units; no validation/normalization or NUL search.
        }
        finally { calls.Free(buffer); }
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct UnicodeString { internal ushort Length, MaximumLength; internal IntPtr Buffer; }
    private sealed class NativeCalls : IWindowsProcessCommandLineCalls
    {
        internal static readonly NativeCalls Instance = new();
        public int Query(IntPtr process, int kind, IntPtr buffer, int length, out int returned)
            => NtQueryBuffer(process, kind, buffer, length, out returned);
        public IntPtr Allocate(int length) => Marshal.AllocHGlobal(length);
        public void Free(IntPtr buffer) => Marshal.FreeHGlobal(buffer);
        [DllImport("ntdll.dll", EntryPoint = "NtQueryInformationProcess")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern int NtQueryBuffer(IntPtr process, int kind, IntPtr information, int length, out int returned);
    }
}
