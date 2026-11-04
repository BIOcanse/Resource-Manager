using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ResourceManager.App.Infrastructure.ResourceBreakdown;

/// <summary>Impersonate this process on this synchronous query thread; restore its original token.</summary>
internal sealed class ProfileSingleProcessPrivilege : IDisposable
{
    private readonly SafeAccessTokenHandle previous;
    private readonly bool hadPrevious;
    private bool oldEnabled, adjusted;
    private ProfileSingleProcessPrivilege(SafeAccessTokenHandle previous, bool hadPrevious)
    { this.previous = previous; this.hadPrevious = hadPrevious; }

    internal static ProfileSingleProcessPrivilege Enter()
    {
        var hadToken = OpenThreadToken(GetCurrentThread(), 0x000C, true, out var previous);
        var tokenError = hadToken ? 0 : Marshal.GetLastWin32Error();
        if (!hadToken && tokenError != 1008)
        { previous.Dispose(); throw new Win32Exception(tokenError); }
        if (!ImpersonateSelf(2))
        {
            var error = Marshal.GetLastWin32Error();
            previous.Dispose();
            throw new Win32Exception(error);
        }
        var scope = new ProfileSingleProcessPrivilege(previous, hadToken);
        try
        {
            var status = RtlAdjustPrivilege(13, true, true, out scope.oldEnabled);
            if (status < 0) throw new PfnQueryException($"profile-privilege:0x{(uint)status:X8}");
            scope.adjusted = true;
            return scope;
        }
        catch { scope.Dispose(); throw; }
    }
    public void Dispose()
    {
        try
        {
            if (adjusted)
            {
                var status = RtlAdjustPrivilege(13, oldEnabled, true, out _);
                if (status < 0) throw new PfnQueryException($"privilege-restore:0x{(uint)status:X8}");
            }
        }
        finally
        {
            try
            {
                var restored = hadPrevious ? SetThreadToken(0, previous.DangerousGetHandle()) : RevertToSelf();
                if (!restored) throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            finally { previous.Dispose(); }
        }
    }
    [DllImport("ntdll.dll")]
    private static extern int RtlAdjustPrivilege(uint privilege, [MarshalAs(UnmanagedType.U1)] bool enable,
        [MarshalAs(UnmanagedType.U1)] bool currentThread, [MarshalAs(UnmanagedType.U1)] out bool wasEnabled);
    [DllImport("kernel32.dll")] private static extern nint GetCurrentThread();
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenThreadToken(nint thread, uint access, bool openAsSelf, out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool ImpersonateSelf(int level);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool SetThreadToken(nint thread, nint token);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool RevertToSelf();
}
internal sealed class PfnQueryException(string code) : Exception(code)
{ internal string Code => Message; }
