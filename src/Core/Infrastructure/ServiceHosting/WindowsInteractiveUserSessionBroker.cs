using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ResourceManager.App.Infrastructure.ServiceHosting;

public sealed class WindowsInteractiveUserSessionBroker : IInteractiveUserSessionBroker
{
    private const uint CreateNewProcessGroup = 0x00000200;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint MaximumAllowed = 0x02000000;
    private const int WtsActive = 0;

    public IReadOnlyList<uint> GetActiveSessionIds()
    {
        if (!WTSEnumerateSessionsW(
                IntPtr.Zero,
                0,
                1,
                out var buffer,
                out var count))
        {
            throw CreateWin32Exception("WTSEnumerateSessionsW");
        }

        try
        {
            var sessions = new HashSet<uint>();
            var size = Marshal.SizeOf<WtsSessionInfo>();
            for (var index = 0U; index < count; index++)
            {
                var entryAddress = IntPtr.Add(buffer, checked((int)(index * size)));
                var entry = Marshal.PtrToStructure<WtsSessionInfo>(entryAddress);
                if (entry.State == WtsActive && entry.SessionId != 0)
                {
                    sessions.Add(entry.SessionId);
                }
            }

            return sessions.Order().ToArray();
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
    }

    public NativeUiProcessPresence GetNativeUiProcessPresence(
        uint sessionId,
        string expectedExecutablePath)
    {
        var expected = NormalizeExecutablePath(expectedExecutablePath);
        var processName = Path.GetFileNameWithoutExtension(expected);
        var foundConflict = false;
        foreach (var process in Process.GetProcessesByName(processName))
        {
            using (process)
            {
                try
                {
                    if (checked((uint)process.SessionId) != sessionId)
                    {
                        continue;
                    }

                    var actual = process.MainModule?.FileName;
                    if (!string.IsNullOrWhiteSpace(actual)
                        && PathsEqual(actual, expected))
                    {
                        return NativeUiProcessPresence.Expected;
                    }

                    foundConflict = true;
                }
                catch (Exception ex) when (ex is InvalidOperationException
                                            or Win32Exception
                                            or NotSupportedException)
                {
                    foundConflict = true;
                }
            }
        }

        return foundConflict
            ? NativeUiProcessPresence.Conflicting
            : NativeUiProcessPresence.Absent;
    }

    public uint LaunchNativeUi(
        uint sessionId,
        string executablePath,
        string arguments)
    {
        var executable = NormalizeExecutablePath(executablePath);
        if (!File.Exists(executable))
        {
            throw new FileNotFoundException(
                "Native UI executable does not exist.",
                executable);
        }

        if (!WTSQueryUserToken(sessionId, out var sessionToken))
        {
            throw CreateWin32Exception("WTSQueryUserToken");
        }

        using (sessionToken)
        {
            if (!DuplicateTokenEx(
                    sessionToken,
                    MaximumAllowed,
                    IntPtr.Zero,
                    SecurityImpersonation,
                    TokenPrimary,
                    out var primaryToken))
            {
                throw CreateWin32Exception("DuplicateTokenEx");
            }

            using (primaryToken)
            {
                if (!CreateEnvironmentBlock(
                        out var environment,
                        primaryToken,
                        false))
                {
                    throw CreateWin32Exception("CreateEnvironmentBlock");
                }

                try
                {
                    var startupInfo = new StartupInfo
                    {
                        Size = Marshal.SizeOf<StartupInfo>(),
                        Desktop = @"winsta0\default"
                    };
                    var commandLine = new StringBuilder(
                        QuoteCommandLineArgument(executable));
                    if (!string.IsNullOrWhiteSpace(arguments))
                    {
                        commandLine.Append(' ');
                        commandLine.Append(arguments);
                    }

                    if (!CreateProcessAsUserW(
                            primaryToken,
                            executable,
                            commandLine,
                            IntPtr.Zero,
                            IntPtr.Zero,
                            false,
                            CreateUnicodeEnvironment | CreateNewProcessGroup,
                            environment,
                            Path.GetDirectoryName(executable),
                            ref startupInfo,
                            out var processInformation))
                    {
                        throw CreateWin32Exception("CreateProcessAsUserW");
                    }

                    try
                    {
                        return processInformation.ProcessId;
                    }
                    finally
                    {
                        CloseHandle(processInformation.ThreadHandle);
                        CloseHandle(processInformation.ProcessHandle);
                    }
                }
                finally
                {
                    if (!DestroyEnvironmentBlock(environment))
                    {
                        var error = Marshal.GetLastWin32Error();
                        Trace.WriteLine(
                            $"DestroyEnvironmentBlock failed after Native UI launch with Win32 error {error}.");
                    }
                }
            }
        }
    }

    private static string NormalizeExecutablePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Path.GetFullPath(path);
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left).TrimEnd('\\', '/'),
            Path.GetFullPath(right).TrimEnd('\\', '/'),
            StringComparison.OrdinalIgnoreCase);

    private static string QuoteCommandLineArgument(string value) =>
        $"\"{value.Replace("\"", "\\\"")}\"";

    private static Win32Exception CreateWin32Exception(string operation)
    {
        var error = Marshal.GetLastWin32Error();
        return new Win32Exception(error, $"{operation} failed with Win32 error {error}.");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WtsSessionInfo
    {
        public uint SessionId;
        public IntPtr WindowStationName;
        public int State;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size;
        public string? Reserved;
        public string? Desktop;
        public string? Title;
        public uint X;
        public uint Y;
        public uint XSize;
        public uint YSize;
        public uint XCountChars;
        public uint YCountChars;
        public uint FillAttribute;
        public uint Flags;
        public ushort ShowWindow;
        public ushort Reserved2Size;
        public IntPtr Reserved2;
        public IntPtr StandardInput;
        public IntPtr StandardOutput;
        public IntPtr StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr ProcessHandle;
        public IntPtr ThreadHandle;
        public uint ProcessId;
        public uint ThreadId;
    }

    [DllImport("wtsapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSEnumerateSessionsW(
        IntPtr server,
        uint reserved,
        uint version,
        out IntPtr sessionInfo,
        out uint count);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr memory);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQueryUserToken(
        uint sessionId,
        out SafeAccessTokenHandle token);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateTokenEx(
        SafeAccessTokenHandle existingToken,
        uint desiredAccess,
        IntPtr tokenAttributes,
        int impersonationLevel,
        int tokenType,
        out SafeAccessTokenHandle duplicateToken);

    [DllImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateEnvironmentBlock(
        out IntPtr environment,
        SafeAccessTokenHandle token,
        [MarshalAs(UnmanagedType.Bool)] bool inherit);

    [DllImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyEnvironmentBlock(IntPtr environment);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessAsUserW(
        SafeAccessTokenHandle token,
        string applicationName,
        StringBuilder commandLine,
        IntPtr processAttributes,
        IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags,
        IntPtr environment,
        string? currentDirectory,
        ref StartupInfo startupInfo,
        out ProcessInformation processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    private const int SecurityImpersonation = 2;
    private const int TokenPrimary = 1;
}
