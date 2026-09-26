using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using ResourceManager.App.Application.GpuPlacement;

namespace ResourceManager.App.Infrastructure.GpuPlacement.WindowExecution;

// These handles belong to one window execution, not to the session activation service.
internal sealed class WindowsGpuWindowActionLaunchContext : IDisposable
{
    private readonly SafeProcessHandle target;
    private readonly long creationFileTimeUtc;
    private IntPtr environment;

    private WindowsGpuWindowActionLaunchContext(SafeProcessHandle target, long creationFileTimeUtc,
        string userSid, uint sessionId, string pipeDacl, SafeAccessTokenHandle? userToken, IntPtr environment)
    {
        this.target = target;
        this.creationFileTimeUtc = creationFileTimeUtc;
        UserSid = userSid;
        SessionId = sessionId;
        PipeDacl = pipeDacl;
        UserToken = userToken;
        this.environment = environment;
    }

    internal string UserSid { get; }
    internal uint SessionId { get; }
    internal string PipeDacl { get; }
    internal SafeAccessTokenHandle? UserToken { get; }
    internal IntPtr EnvironmentBlock => environment;
    internal bool UsesSessionToken => UserToken is not null;

    internal static WindowsGpuWindowActionLaunchContext Open(GpuWindowActionRequest request)
    {
        request.Validate();
        return Open(request.ProcessId, request.CreationFileTimeUtc);
    }

    internal static WindowsGpuWindowActionLaunchContext Open(int processId, long creationFileTimeUtc)
    {
        if (processId <= 4 || creationFileTimeUtc <= 0)
            throw new ArgumentException("A precise target process instance is required.");
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var target = Native.OpenProcess(Native.ProcessQueryAndSynchronize, false, checked((uint)processId));
        SafeAccessTokenHandle? userToken = null;
        var environment = IntPtr.Zero;
        try
        {
            if (target.IsInvalid) throw Native.Error("OpenProcess(window target)");
            RequireTarget(target, creationFileTimeUtc);
            using var targetToken = OpenToken(target);
            var targetUser = ReadUser(targetToken);
            var targetSession = ReadSession(targetToken);
            using var current = Process.GetCurrentProcess();
            using var currentToken = OpenToken(current.SafeHandle);
            var creator = ReadUser(currentToken);
            var system = new SecurityIdentifier(creator).IsWellKnown(WellKnownSidType.LocalSystemSid);
            string logonSid;
            if (system)
            {
                if (targetSession == 0) throw new UnauthorizedAccessException("A window action requires an interactive target session.");
                if (!Native.WTSQueryUserToken(targetSession, out userToken))
                    throw Native.Error("WTSQueryUserToken(window target session)");
                RequireIdentity(userToken, targetUser, targetSession);
                logonSid = ReadLogonSid(userToken);
                if (!Native.CreateEnvironmentBlock(out environment, userToken, false))
                    throw Native.Error("CreateEnvironmentBlock(window target session)");
            }
            else
            {
                RequireIdentity(currentToken, targetUser, targetSession);
                logonSid = ReadLogonSid(currentToken);
            }
            RequireTarget(target, creationFileTimeUtc);
            // The client needs data read/write and wait, not FILE_CREATE_PIPE_INSTANCE (0x4).
            var dacl = $"D:P(A;;GA;;;{creator})(A;;0x00100003;;;{logonSid})";
            return new(target, creationFileTimeUtc, targetUser, targetSession, dacl, userToken, environment);
        }
        catch
        {
            if (environment != IntPtr.Zero) _ = Native.DestroyEnvironmentBlock(environment);
            userToken?.Dispose();
            target.Dispose();
            throw;
        }
    }

    internal void RequireTargetAlive() => RequireTarget(target, creationFileTimeUtc);

    internal void RequireWorkerIdentity(SafeProcessHandle worker)
    {
        using var token = OpenToken(worker);
        RequireIdentity(token, UserSid, SessionId);
        RequireTargetAlive();
    }

    private static void RequireTarget(SafeProcessHandle handle, long creation)
    {
        var wait = Native.WaitForSingleObject(handle, 0);
        if (wait == uint.MaxValue) throw Native.Error("WaitForSingleObject(window target)");
        if (!Native.GetProcessTimes(handle, out var actual, out _, out _, out _))
            throw Native.Error("GetProcessTimes(window target)");
        if (wait != Native.WaitTimeout || actual != creation)
            throw new InvalidDataException("The requested window target instance is not alive.");
    }

    private static SafeAccessTokenHandle OpenToken(SafeProcessHandle process)
    {
        if (Native.OpenProcessToken(process, Native.TokenQuery, out var token)) return token;
        var error = Native.Error("OpenProcessToken(window execution)");
        token?.Dispose();
        throw error;
    }

    private static string ReadUser(SafeAccessTokenHandle token)
    {
        using var identity = new WindowsIdentity(token.DangerousGetHandle());
        return identity.User?.Value ?? throw new InvalidDataException("The window execution token has no user SID.");
    }

    private static uint ReadSession(SafeAccessTokenHandle token)
    {
        if (!Native.GetTokenSession(token, Native.TokenSessionId, out var session, sizeof(uint), out var returned))
            throw Native.Error("GetTokenInformation(TokenSessionId)");
        if (returned != sizeof(uint)) throw new InvalidDataException("The token session has an invalid size.");
        return session;
    }

    private static void RequireIdentity(SafeAccessTokenHandle token, string user, uint session)
    {
        if (ReadUser(token) != user || ReadSession(token) != session)
            throw new UnauthorizedAccessException("The window worker must run as the target user in the target session.");
    }

    private static string ReadLogonSid(SafeAccessTokenHandle token)
    {
        if (Native.GetTokenInformation(token, Native.TokenLogonSid, IntPtr.Zero, 0, out var size)
            || Marshal.GetLastWin32Error() != Native.ErrorInsufficientBuffer)
            throw Native.Error("GetTokenInformation(TokenLogonSid size)");
        var buffer = Marshal.AllocHGlobal(checked((int)size));
        try
        {
            if (!Native.GetTokenInformation(token, Native.TokenLogonSid, buffer, size, out var returned))
                throw Native.Error("GetTokenInformation(TokenLogonSid)");
            if (returned > size || returned < Marshal.SizeOf<Native.TokenGroups>())
                throw new InvalidDataException("The token logon SID has an invalid size.");
            var groups = Marshal.PtrToStructure<Native.TokenGroups>(buffer);
            if (groups.Count != 1 || groups.First.Sid == IntPtr.Zero
                || (groups.First.Attributes & Native.LogonIdAttributes) != Native.LogonIdAttributes)
                throw new UnauthorizedAccessException("An explicit interactive logon SID is required.");
            return new SecurityIdentifier(groups.First.Sid).Value;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    public void Dispose()
    {
        var value = Interlocked.Exchange(ref environment, IntPtr.Zero);
        if (value != IntPtr.Zero) _ = Native.DestroyEnvironmentBlock(value);
        UserToken?.Dispose();
        target.Dispose();
    }

    private static class Native
    {
        internal const uint ProcessQueryAndSynchronize = 0x00101000;
        internal const uint TokenQuery = 0x8;
        internal const int TokenSessionId = 12, TokenLogonSid = 28;
        internal const uint WaitTimeout = 258, LogonIdAttributes = 0xC0000000;
        internal const int ErrorInsufficientBuffer = 122;

        [StructLayout(LayoutKind.Sequential)]
        internal struct SidAndAttributes { internal IntPtr Sid; internal uint Attributes; }
        [StructLayout(LayoutKind.Sequential)]
        internal struct TokenGroups { internal uint Count; internal SidAndAttributes First; }

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint id);
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern uint WaitForSingleObject(SafeProcessHandle process, uint timeout);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetProcessTimes(SafeProcessHandle process, out long creation, out long exit, out long kernel, out long user);
        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
        [DllImport("advapi32.dll", EntryPoint = "GetTokenInformation", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetTokenSession(SafeAccessTokenHandle token, int kind, out uint value, uint size, out uint returned);
        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetTokenInformation(SafeAccessTokenHandle token, int kind, IntPtr value, uint size, out uint returned);
        [DllImport("wtsapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool WTSQueryUserToken(uint session, out SafeAccessTokenHandle token);
        [DllImport("userenv.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CreateEnvironmentBlock(out IntPtr environment, SafeAccessTokenHandle token,
            [MarshalAs(UnmanagedType.Bool)] bool inherit);
        [DllImport("userenv.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DestroyEnvironmentBlock(IntPtr environment);

        internal static Win32Exception Error(string operation) => new(Marshal.GetLastWin32Error(), operation);
    }
}
