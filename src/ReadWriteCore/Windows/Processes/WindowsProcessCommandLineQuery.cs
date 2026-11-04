using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ResourceManager.ReadWrite.Windows.Processes;

internal sealed partial class WindowsProcessBasicMetricNativeApi
{
    [RuntimeRead(ReadVolatilityKind.Fixed)]
    public string? CommandLine(IntPtr handle) => WindowsProcessCommandLineQuery.Read(handle);
}

internal interface IWindowsProcessCommandLineCalls
{
    int Query(IntPtr process, int kind, IntPtr buffer, int length, out int returned);
    IntPtr Allocate(int length);
    void Free(IntPtr buffer);
    bool IsExited(IntPtr process) => false;
}

/// <summary>The original Chromium class60 raw read. It borrows the supplied handle;
/// caller owns its creation-identity gate and lifetime. No process is opened here.</summary>
internal static class WindowsProcessCommandLineQuery
{
    internal readonly record struct Result(ProcessReadStatus Status, string? Value, string? Reason);
    internal const int InformationClass = 60, MaximumBytes = ushort.MaxValue;
    internal static string? Read(IntPtr process) => Read(process, NativeCalls.Instance);
    internal static string? Read(SafeProcessHandle process)
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
    internal static string? Read(IntPtr process, IWindowsProcessCommandLineCalls calls)
    {
        var result = ReadResult(process, calls);
        if (result.Reason is not null) System.Diagnostics.Debug.WriteLine($"Process command line: {result.Reason}");
        return result.Value;
    }
    [RuntimeRead(ReadVolatilityKind.Fixed)]
    internal static Result ReadResult(IntPtr process, IWindowsProcessCommandLineCalls calls)
    {
        Result Missing(string reason) => calls.IsExited(process)
            ? new(ProcessReadStatus.Exited, null, "process-exited")
            : new(ProcessReadStatus.Unavailable, null, reason);
        var status = calls.Query(process, InformationClass, IntPtr.Zero, 0, out var length);
        if (status == unchecked((int)0xC000010A)) return new(ProcessReadStatus.Exited, null, "process-exited");
        if (status < 0 && status != unchecked((int)0xC0000004) && status != unchecked((int)0xC0000023))
            return Missing("query-length-failed");
        var header = Marshal.SizeOf<UnicodeString>();
        if (length < header || length > MaximumBytes)
            return Missing("invalid-length");
        var buffer = calls.Allocate(length);
        if (buffer == IntPtr.Zero) return Missing("allocation-failed");
        try
        {
            status = calls.Query(process, InformationClass, buffer, length, out var returned);
            if (status == unchecked((int)0xC000010A)) return new(ProcessReadStatus.Exited, null, "process-exited");
            if (status < 0) return Missing("query-failed");
            if (returned < header || returned > length)
                return Missing("invalid-returned-length");
            var value = Marshal.PtrToStructure<UnicodeString>(buffer);
            if (value.Length % 2 != 0 || value.MaximumLength % 2 != 0 || value.Length > value.MaximumLength)
                return Missing("invalid-utf16-bounds");
            if (value.Length == 0 && value.Buffer == IntPtr.Zero && value.MaximumLength == 0) return Current(string.Empty);
            var offset = value.Buffer.ToInt64() - buffer.ToInt64();
            if (offset < header || offset > returned || value.Length > returned - offset)
                return Missing("invalid-buffer-bounds");
            if (value.Length == 0) return Current(string.Empty);
            return Current(Marshal.PtrToStringUni(value.Buffer, value.Length / 2) ?? string.Empty); // Exact raw UTF-16 code units.
        }
        finally { calls.Free(buffer); }
    }
    private static Result Current(string value) => new(ProcessReadStatus.Current, value, null);
    [StructLayout(LayoutKind.Sequential)]
    internal struct UnicodeString { internal ushort Length, MaximumLength; internal IntPtr Buffer; }
    private sealed class NativeCalls : IWindowsProcessCommandLineCalls
    {
        internal static readonly NativeCalls Instance = new();
        public int Query(IntPtr process, int kind, IntPtr buffer, int length, out int returned)
            => NtQueryBuffer(process, kind, buffer, length, out returned);
        public IntPtr Allocate(int length) => Marshal.AllocHGlobal(length);
        public void Free(IntPtr buffer) => Marshal.FreeHGlobal(buffer);
        public bool IsExited(IntPtr process)
            => WindowsProcessBasicMetricNativeApi.GetProcessTimes(process, out _, out var exit, out _, out _)
                && exit.ToUInt64() != 0;
        [DllImport("ntdll.dll", EntryPoint = "NtQueryInformationProcess")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern int NtQueryBuffer(IntPtr process, int kind, IntPtr information, int length, out int returned);
    }
}
