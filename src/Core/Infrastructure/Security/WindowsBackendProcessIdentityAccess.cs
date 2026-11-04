using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;

namespace ResourceManager.App.Infrastructure.Security;

// User-session clients must verify the SYSTEM server image and creation identity
// before credentials are exchanged. No process mutation or memory access is granted.
internal static class WindowsBackendProcessIdentityAccess
{
    private const uint DaclSecurityInformation = 4;
    internal const int QueryLimitedInformation = 0x1000;

    internal static void InitializeForCurrentSystemProcess()
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!identity.IsSystem) throw new InvalidOperationException("Backend process query access is only prepared for LocalSystem.");
        var process = GetCurrentProcess();
        _ = GetKernelObjectSecurity(process, DaclSecurityInformation, null, 0, out var length);
        if (length is < 20 or > 65536) throw new Win32Exception(Marshal.GetLastWin32Error());
        var bytes = new byte[length];
        if (!GetKernelObjectSecurity(process, DaclSecurityInformation, bytes, length, out _))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        var descriptor = new RawSecurityDescriptor(bytes, 0);
        if (!AddQueryAccess(descriptor)) return;
        var updated = new byte[descriptor.BinaryLength];
        descriptor.GetBinaryForm(updated, 0);
        if (!SetKernelObjectSecurity(process, DaclSecurityInformation, updated))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    internal static bool AddQueryAccess(RawSecurityDescriptor descriptor)
    {
        var acl = descriptor.DiscretionaryAcl;
        // A null DACL already permits queries; do not replace the existing policy.
        if (acl is null) return false;
        var users = new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null);
        var insertion = acl.Count;
        for (var index = 0; index < acl.Count; index++)
        {
            var ace = acl[index];
            if (ace is CommonAce { IsCallback: false, AceQualifier: AceQualifier.AccessAllowed } allowed
                && allowed.SecurityIdentifier == users && (allowed.AccessMask & QueryLimitedInformation) != 0)
                return false;
            if (insertion == acl.Count && (ace.AceFlags & AceFlags.Inherited) != 0) insertion = index;
        }
        acl.InsertAce(insertion, new CommonAce(AceFlags.None, AceQualifier.AccessAllowed,
            QueryLimitedInformation, users, isCallback: false, opaque: null));
        return true;
    }

    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetKernelObjectSecurity(IntPtr handle, uint information,
        [Out] byte[]? descriptor, uint length, out uint required);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetKernelObjectSecurity(IntPtr handle, uint information, byte[] descriptor);
}
