using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

namespace ResourceManager.ReadWrite.Windows.Processes;

internal sealed partial class WindowsProcessBasicMetricNativeApi
{
    [RuntimeRead(ReadVolatilityKind.Fixed)]
    public string? ImagePath(IntPtr handle)
    {
        for (var capacity = 1024; capacity <= 32768; capacity *= 2)
        {
            var text = new StringBuilder(capacity); var size = capacity;
            if (QueryFullProcessImageName(handle, 0, text, ref size))
                return size > 0 && size < capacity ? text.ToString() : null;
            if (Marshal.GetLastWin32Error() != 122) return null;
        }
        return null;
    }

    [RuntimeRead(ReadVolatilityKind.Fixed)]
    public uint? ParentPid(IntPtr handle)
    {
        var size = Marshal.SizeOf<ProcessBasicInformation>();
        var status = NtQueryInformationProcess(handle, 0, out var value, size, out var returned);
        return status >= 0 && returned == size ? DecodeInheritedPid(value.InheritedFromUniqueProcessId) : null;
    }

    internal static uint? DecodeInheritedPid(UIntPtr value)
        => value.ToUInt64() <= uint.MaxValue ? (uint)value.ToUInt64() : null;

    [RuntimeRead(ReadVolatilityKind.Fixed)]
    public string? UserSid(IntPtr handle)
    {
        if (!OperatingSystem.IsWindows()) return null;
        if (!OpenProcessToken(handle, 0x0008, out var token)) return null;
        var buffer = IntPtr.Zero; string? value = null; var released = true;
        try
        {
            _ = GetTokenInformation(token, 1, IntPtr.Zero, 0, out var length);
            if (Marshal.GetLastWin32Error() == 122 && length >= Marshal.SizeOf<TokenUser>() && length <= 65536)
            {
                buffer = Marshal.AllocHGlobal(length);
                if (GetTokenInformation(token, 1, buffer, length, out var returned)
                    && returned >= Marshal.SizeOf<TokenUser>() && returned <= length)
                {
                    var sid = Marshal.PtrToStructure<TokenUser>(buffer).User.Sid;
                    var offset = sid.ToInt64() - buffer.ToInt64();
                    if (offset >= Marshal.SizeOf<TokenUser>() && offset <= returned - 8
                        && Marshal.ReadByte(sid, 1) <= 15
                        && 8 + 4 * Marshal.ReadByte(sid, 1) <= returned - offset && IsValidSid(sid))
                    {
                        var sidLength = GetLengthSid(sid);
                        if (sidLength >= 8 && sidLength <= returned - offset)
                            value = new SecurityIdentifier(sid).Value;
                    }
                }
            }
        }
        finally
        {
            if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
            released = CloseHandle(token);
        }
        return released ? value : null;
    }

    // PBI's six pointer-width slots preserve native alignment on x86 and x64.
    // The last slot is the inherited parent PID, not a current parent creation identity.
    [StructLayout(LayoutKind.Sequential)]
    internal struct ProcessBasicInformation
    {
        public IntPtr ExitStatus, PebBaseAddress, AffinityMask, BasePriority;
        public UIntPtr UniqueProcessId, InheritedFromUniqueProcessId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SidAndAttributes { public IntPtr Sid; public uint Attributes; }
    [StructLayout(LayoutKind.Sequential)]
    private struct TokenUser { public SidAndAttributes User; }

    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder imageName, ref int size);

    [DllImport("ntdll.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int NtQueryInformationProcess(IntPtr process, int informationClass,
        out ProcessBasicInformation information, int length, out int returnedLength);

    [DllImport("advapi32.dll", ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(IntPtr token, int informationClass, IntPtr information, int length, out int returnedLength);

    [DllImport("advapi32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsValidSid(IntPtr sid);

    [DllImport("advapi32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint GetLengthSid(IntPtr sid);
}
