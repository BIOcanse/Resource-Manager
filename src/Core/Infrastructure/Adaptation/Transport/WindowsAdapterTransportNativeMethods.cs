using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ResourceManager.App.Infrastructure.Adaptation.Transport;

internal static class WindowsAdapterTransportNativeMethods
{
    internal const uint ErrorInsufficientBuffer = 122;
    internal const uint TokenQuery = 0x0008;
    internal const uint ProcessQueryLimitedInformation = 0x1000;
    internal const uint FileReadAttributes = 0x0080;
    internal const uint FileShareRead = 0x00000001;
    internal const uint FileShareWrite = 0x00000002;
    internal const uint FileShareDelete = 0x00000004;
    internal const uint OpenExisting = 3;

    internal enum TokenInformationClass
    {
        TokenUser = 1,
        TokenStatistics = 10,
        TokenSessionId = 12,
        TokenElevation = 20,
        TokenIntegrityLevel = 25
    }

    internal enum FileInformationClass
    {
        FileIdInfo = 18
    }

    [StructLayout(LayoutKind.Sequential)]
    internal readonly struct Luid
    {
        internal readonly uint LowPart;
        internal readonly int HighPart;

        internal ulong ToUInt64()
            => ((ulong)unchecked((uint)HighPart) << 32) | LowPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal readonly struct TokenStatistics
    {
        internal readonly Luid TokenId;
        internal readonly Luid AuthenticationId;
        internal readonly long ExpirationTime;
        internal readonly int TokenType;
        internal readonly int ImpersonationLevel;
        internal readonly uint DynamicCharged;
        internal readonly uint DynamicAvailable;
        internal readonly uint GroupCount;
        internal readonly uint PrivilegeCount;
        internal readonly Luid ModifiedId;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal readonly struct FileTime
    {
        internal readonly uint LowDateTime;
        internal readonly uint HighDateTime;

        internal long ToInt64()
            => unchecked((long)(((ulong)HighDateTime << 32) | LowDateTime));
    }

    [StructLayout(LayoutKind.Sequential)]
    internal readonly struct FileIdInfo
    {
        internal readonly ulong VolumeSerialNumber;
        internal readonly ulong FileIdLow;
        internal readonly ulong FileIdHigh;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetNamedPipeClientProcessId(
        SafePipeHandle pipe,
        out uint clientProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetNamedPipeClientSessionId(
        SafePipeHandle pipe,
        out uint clientSessionId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetNamedPipeServerProcessId(
        SafePipeHandle pipe,
        out uint serverProcessId);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ImpersonateNamedPipeClient(SafePipeHandle pipe);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool RevertToSelf();

    [DllImport("kernel32.dll")]
    internal static extern IntPtr GetCurrentThread();

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool OpenThreadToken(
        IntPtr threadHandle,
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool openAsSelf,
        out SafeAccessTokenHandle tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetTokenInformation(
        SafeAccessTokenHandle tokenHandle,
        TokenInformationClass informationClass,
        IntPtr information,
        uint informationLength,
        out uint returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ProcessIdToSessionId(
        uint processId,
        out uint sessionId);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern SafeProcessHandle OpenProcess(
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetProcessTimes(
        SafeProcessHandle process,
        out FileTime creationTime,
        out FileTime exitTime,
        out FileTime kernelTime,
        out FileTime userTime);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool QueryFullProcessImageNameW(
        SafeProcessHandle process,
        uint flags,
        StringBuilder executablePath,
        ref uint size);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern uint GetFinalPathNameByHandleW(
        SafeFileHandle file,
        StringBuilder path,
        uint pathLength,
        uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetFileInformationByHandleEx(
        SafeFileHandle file,
        FileInformationClass informationClass,
        out FileIdInfo information,
        uint informationLength);
}
