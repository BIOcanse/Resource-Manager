using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace ResourceManager.App.Infrastructure.Optimization;

// Installation prepares the same SYSTEM-owned ACL that the service verifies.
// This only enables a privilege already present in the maintenance process token.
internal sealed class WindowsValidationStorageOwnerPrivilege : IDisposable
{
    private readonly WindowsIdentity identity;
    private readonly TokenPrivileges previous;

    private WindowsValidationStorageOwnerPrivilege(WindowsIdentity identity, TokenPrivileges previous)
    {
        this.identity = identity;
        this.previous = previous;
    }

    internal static IDisposable? Acquire(SecurityIdentifier owner)
    {
        using (var current = WindowsIdentity.GetCurrent())
            if (current.User == owner) return null;

        var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.AdjustPrivileges | TokenAccessLevels.Query);
        try
        {
            if (!LookupPrivilegeValue(null, "SeRestorePrivilege", out var luid))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            var requested = new TokenPrivileges { Count = 1, Luid = luid, Attributes = 2 };
            if (!AdjustTokenPrivileges(identity.AccessToken, false, ref requested,
                    Marshal.SizeOf<TokenPrivileges>(), out var previous, out _))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            var error = Marshal.GetLastWin32Error();
            if (error != 0) throw new Win32Exception(error, "Cannot prepare SYSTEM-owned validation storage.");
            return new WindowsValidationStorageOwnerPrivilege(identity, previous);
        }
        catch { identity.Dispose(); throw; }
    }

    public void Dispose()
    {
        try
        {
            var restore = previous;
            if (!AdjustTokenPrivileges(identity.AccessToken, false, ref restore,
                    Marshal.SizeOf<TokenPrivileges>(), out _, out _))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            var error = Marshal.GetLastWin32Error();
            if (error != 0) throw new Win32Exception(error, "Cannot restore validation storage owner privilege.");
        }
        finally { identity.Dispose(); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid { public uint Low; public int High; }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivileges { public uint Count; public Luid Luid; public uint Attributes; }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LookupPrivilegeValue(string? system, string name, out Luid luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AdjustTokenPrivileges(SafeAccessTokenHandle token,
        [MarshalAs(UnmanagedType.Bool)] bool disableAll, ref TokenPrivileges state,
        int bufferLength, out TokenPrivileges previous, out int returnLength);
}
