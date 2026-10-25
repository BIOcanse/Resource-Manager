using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using Microsoft.Extensions.Logging;
using ResourceManager.App.Infrastructure.ProcessIdentity;

namespace ResourceManager.App.Infrastructure.ServiceHosting;

public sealed class WindowsInteractiveUserSessionBroker : IInteractiveUserSessionBroker
{
    private const uint CreateNewProcessGroup = 0x00000200;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint MaximumAllowed = 0x02000000;
    private const int WtsActive = 0;
    private const int UserShellWaitSliceMilliseconds = 30 * 60 * 1000;
    private readonly ILogger<WindowsInteractiveUserSessionBroker>? logger;

    public WindowsInteractiveUserSessionBroker()
    {
    }

    public WindowsInteractiveUserSessionBroker(
        ILogger<WindowsInteractiveUserSessionBroker> logger)
        => this.logger = logger;

    public string? GetUserSid(uint sessionId)
    {
        using var current = WindowsIdentity.GetCurrent();
        using var process = Process.GetCurrentProcess();
        if (!current.IsSystem && process.SessionId == sessionId) return current.User?.Value;
        if (!WTSQueryUserToken(sessionId, out var token))
        {
            token?.Dispose();
            return null;
        }
        using (token)
        using (var user = new WindowsIdentity(token.DangerousGetHandle())) return user.User?.Value;
    }

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

    public uint LaunchDesktopProcess(uint sessionId, string expectedUserSid, string executablePath, string arguments)
        => LaunchInSession(sessionId, executablePath, arguments,
            new SecurityIdentifier(expectedUserSid).Value, allowCurrentUserLaunch: false);

    public uint LaunchUserShell(uint sessionId, string expectedUserSid, string executablePath, string arguments)
        => LaunchInSession(sessionId, executablePath, arguments,
            new SecurityIdentifier(expectedUserSid).Value);

    public async Task<int> LaunchUserShellAndWaitAsync(
        uint sessionId,
        string expectedUserSid,
        string executablePath,
        string arguments,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        SafeWaitHandle? processWaitHandle = null;
        try
        {
            var processId = LaunchInSession(
                sessionId,
                executablePath,
                arguments,
                new SecurityIdentifier(expectedUserSid).Value,
                retainProcessHandle: processHandle =>
                    processWaitHandle = DuplicateProcessHandleForWaiting(processHandle));

            var launchedProcessWaitHandle = processWaitHandle
                ?? throw new InvalidOperationException("The interactive user process did not provide a wait handle.");
            processWaitHandle = null;
            return await WaitForProcessExitAsync(
                    launchedProcessWaitHandle,
                    cancellationToken,
                    UserShellWaitSliceMilliseconds,
                    () => logger?.LogInformation(
                        "Cancellation was requested while waiting for owned user process {ProcessId} in session {SessionId}; continuing to observe its exit.",
                        processId,
                        sessionId),
                    () => logger?.LogWarning(
                        "Owned user process {ProcessId} in session {SessionId} is still running after a bounded wait slice; retaining operation ownership.",
                        processId,
                        sessionId))
                .ConfigureAwait(false);
        }
        finally
        {
            processWaitHandle?.Dispose();
        }
    }

    private static uint LaunchInSession(uint sessionId, string executablePath, string arguments, string? expectedUserSid,
        bool allowCurrentUserLaunch = true,
        Action<IntPtr>? retainProcessHandle = null)
    {
        var executable = NormalizeExecutablePath(executablePath);
        if (!File.Exists(executable))
        {
            throw new FileNotFoundException(
                "Interactive process executable does not exist.",
                executable);
        }

        if (expectedUserSid is not null
            && TryLaunchInCurrentUserSession(
                sessionId,
                expectedUserSid,
                executable,
                arguments,
                out var currentSessionProcessId,
                allowCurrentUserLaunch,
                retainProcessHandle))
            return currentSessionProcessId;

        if (!WTSQueryUserToken(sessionId, out var sessionToken))
        {
            var error = Marshal.GetLastWin32Error();
            sessionToken?.Dispose();
            throw CreateWin32Exception("WTSQueryUserToken", error);
        }

        using (sessionToken)
        {
            if (expectedUserSid is not null)
            {
                using var user = new WindowsIdentity(sessionToken.DangerousGetHandle());
                if (user.IsSystem || user.User?.Value != expectedUserSid)
                    throw new UnauthorizedAccessException("The interactive session user changed.");
            }
            if (!DuplicateTokenEx(
                    sessionToken,
                    MaximumAllowed,
                    IntPtr.Zero,
                    SecurityImpersonation,
                    TokenPrimary,
                    out var primaryToken))
            {
                var error = Marshal.GetLastWin32Error();
                primaryToken?.Dispose();
                throw CreateWin32Exception("DuplicateTokenEx", error);
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
                        retainProcessHandle?.Invoke(processInformation.ProcessHandle);
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
                            $"DestroyEnvironmentBlock failed after interactive process launch with Win32 error {error}.");
                    }
                }
            }
        }
    }

    private static bool TryLaunchInCurrentUserSession(
        uint sessionId,
        string expectedUserSid,
        string executable,
        string arguments,
        out uint processId,
        bool allowCurrentUserLaunch = true,
        Action<IntPtr>? retainProcessHandle = null)
    {
        processId = 0;
        if (!allowCurrentUserLaunch) return false;
        using var identity = WindowsIdentity.GetCurrent();
        using var current = Process.GetCurrentProcess();
        if (identity.IsSystem || current.SessionId != sessionId
            || identity.User?.Value != expectedUserSid)
            return false;

        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = executable,
            Arguments = arguments,
            WorkingDirectory = Path.GetDirectoryName(executable),
            UseShellExecute = false
        }) ?? throw new InvalidOperationException("The interactive user process did not start.");
        processId = checked((uint)process.Id);
        retainProcessHandle?.Invoke(process.SafeHandle.DangerousGetHandle());
        return true;
    }

    internal static SafeWaitHandle DuplicateProcessHandleForWaiting(IntPtr processHandle)
    {
        var currentProcess = GetCurrentProcess();
        if (!DuplicateHandle(
                currentProcess,
                processHandle,
                currentProcess,
                out var duplicateHandle,
                0,
                false,
                DuplicateSameAccess))
        {
            throw CreateWin32Exception("DuplicateHandle");
        }

        return new SafeWaitHandle(duplicateHandle, ownsHandle: true);
    }

    internal static Task<int> WaitForProcessExitAsync(
        SafeWaitHandle processWaitHandle,
        CancellationToken cancellationToken,
        int waitSliceMilliseconds = UserShellWaitSliceMilliseconds,
        Action? onCancellationObserved = null,
        Action? onWaitSliceElapsed = null)
    {
        ArgumentNullException.ThrowIfNull(processWaitHandle);
        if (waitSliceMilliseconds <= 0) throw new ArgumentOutOfRangeException(nameof(waitSliceMilliseconds));

        return Task.Run(() =>
        {
            using var processWait = new EventWaitHandle(false, EventResetMode.ManualReset);
            processWait.SafeWaitHandle = processWaitHandle;
            WaitHandle[] waitHandles = cancellationToken.CanBeCanceled
                ? [processWait, cancellationToken.WaitHandle]
                : [processWait];
            while (true)
            {
                var waitResult = WaitHandle.WaitAny(waitHandles, waitSliceMilliseconds);
                if (waitResult == WaitHandle.WaitTimeout)
                {
                    NotifyWaitObserver(onWaitSliceElapsed);
                    continue;
                }
                if (waitResult == 1)
                {
                    NotifyWaitObserver(onCancellationObserved);
                    waitHandles = [processWait];
                    continue;
                }
                if (waitResult != 0)
                {
                    throw new InvalidOperationException(
                        $"Waiting for the interactive user process returned unexpected result {waitResult}.");
                }
                if (!GetExitCodeProcess(processWait.SafeWaitHandle, out var exitCode))
                    throw CreateWin32Exception("GetExitCodeProcess");
                return unchecked((int)exitCode);
            }
        }, CancellationToken.None);
    }

    private static void NotifyWaitObserver(Action? observer)
    {
        if (observer is null) return;
        try
        {
            observer();
        }
        catch (Exception error)
        {
            Trace.WriteLine($"Owned-process wait observer failed; retaining the wait until process exit. {error}");
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
        return CreateWin32Exception(operation, error);
    }

    private static Win32Exception CreateWin32Exception(string operation, int error)
    {
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
    private const uint DuplicateSameAccess = 0x00000002;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateHandle(
        IntPtr sourceProcessHandle,
        IntPtr sourceHandle,
        IntPtr targetProcessHandle,
        out IntPtr targetHandle,
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        uint options);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(SafeWaitHandle processHandle, out uint exitCode);
}
