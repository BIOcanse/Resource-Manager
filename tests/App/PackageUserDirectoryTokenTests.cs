using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using ResourceManager.Shared.Packages;
using ResourceManager.Updater;

namespace Resource_Manager_APP.Tests;

public sealed class PackageUserDirectoryTokenTests
{
    [Fact]
    public void NonAdministratorTokenCreatesPrivateCacheAndDiagnosticsButCannotWriteGlobalConfig()
    {
        var root = CreateRoot();
        using var token = CreateUserToken();
        try
        {
            // Create only this test's empty installer-owned roots, then make Config read-only.
            Directory.CreateDirectory(Path.Combine(root, "Config", "WebView2"));
            Directory.CreateDirectory(Path.Combine(root, "Config", "Diagnostics", "NativeUiStartup"));
            SetServiceAccess(Path.Combine(root, "Config"));
            PackageUserDirectoryAccess.Prepare(root);
            WindowsIdentity.RunImpersonated(token, () =>
            {
                using var user = WindowsIdentity.GetCurrent();
                Assert.False(new WindowsPrincipal(user).IsInRole(WindowsBuiltInRole.Administrator));
                var sid = user.User!.Value;
                var profile = Path.Combine(root, "Config", "WebView2", sid, "Profile");
                Directory.CreateDirectory(profile);
                File.WriteAllText(Path.Combine(profile, "cache"), "user-cache");
                Assert.Equal("user-cache", File.ReadAllText(Path.Combine(profile, "cache")));
                var diagnostic = Path.Combine(root, "Config", "Diagnostics", "NativeUiStartup", sid);
                Directory.CreateDirectory(diagnostic);
                File.WriteAllText(Path.Combine(diagnostic, "webview-failure.json"), "{}");
                Assert.Throws<UnauthorizedAccessException>(() =>
                    File.WriteAllText(Path.Combine(root, "Config", "app-settings.json"), "blocked"));
            });
        }
        finally { Cleanup(root); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UpdateCopyRetainsInheritedOwnerAccessUnderDifferentDestinationPolicy(bool file)
    {
        var root = CreateRoot();
        using var token = CreateUserToken();
        try
        {
            var sourceParent = Directory.CreateDirectory(Path.Combine(root, "source"));
            SetServiceAccess(sourceParent.FullName, creatorOwner: true, createChildren: true);
            var parentAccess = sourceParent.GetAccessControl();
            parentAccess.AddAccessRule(new(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.Write,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            sourceParent.SetAccessControl(parentAccess);
            var source = WindowsIdentity.RunImpersonated(token, () =>
            {
                var child = Path.Combine(sourceParent.FullName, "profile");
                Directory.CreateDirectory(child);
                if (!file) return child;
                var cache = Path.Combine(child, "cache");
                File.WriteAllText(cache, "original");
                return cache;
            });
            var destination = Path.Combine(root, "copied");
            if (file) File.WriteAllText(destination, "original");
            else Directory.CreateDirectory(destination);
            FileSystemSecurity sourceSecurity = file
                ? new FileInfo(source).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner)
                : new DirectoryInfo(source).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
            using var identity = WindowsIdentity.GetCurrent();
            Assert.Equal(identity.User, sourceSecurity.GetOwner(typeof(SecurityIdentifier)));
            Assert.Contains(sourceSecurity.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>(),
                rule => rule.IsInherited && rule.IdentityReference.Equals(identity.User)
                    && (rule.FileSystemRights & FileSystemRights.Modify) == FileSystemRights.Modify);
            SetServiceAccess(destination, file: file);
            WindowsIdentity.RunImpersonated(token, () => Assert.Throws<UnauthorizedAccessException>(() =>
                File.WriteAllText(file ? destination : Path.Combine(destination, "cache"), "before-copy")));
            typeof(PackageSwitchTransaction).GetMethod(file ? "CopyFileAccessRules" : "CopyDirectoryAccessRules",
                BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [source, destination]);
            FileSystemSecurity copied = file ? new FileInfo(destination).GetAccessControl() : new DirectoryInfo(destination).GetAccessControl();
            Assert.DoesNotContain(copied.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>(),
                rule => rule.IdentityReference.Equals(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null))
                    && rule.AccessControlType == AccessControlType.Allow && (rule.FileSystemRights & FileSystemRights.WriteData) != 0);
            WindowsIdentity.RunImpersonated(token, () =>
            {
                var cache = file ? destination : Path.Combine(destination, "cache");
                File.WriteAllText(cache, "retained-owner-access");
                Assert.Equal("retained-owner-access", File.ReadAllText(cache));
            });
        }
        finally { Cleanup(root); }
    }

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "rm-user-token-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UpdateCopyRetainsOwnerReadOnlyAndDenyRulesWithoutGrantingWrite(bool denyWrite)
    {
        var root = CreateRoot();
        using var token = CreateUserToken();
        using var identity = WindowsIdentity.GetCurrent();
        try
        {
            var parent = Directory.CreateDirectory(Path.Combine(root, "source"));
            var source = Path.Combine(parent.FullName, "settings");
            File.WriteAllText(source, "read-only");
            var sourceAccess = new DirectorySecurity();
            sourceAccess.SetAccessRuleProtection(true, false);
            sourceAccess.AddAccessRule(new(identity.User!, FileSystemRights.ReadAndExecute,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            if (denyWrite)
                sourceAccess.AddAccessRule(new(identity.User!, FileSystemRights.WriteData | FileSystemRights.AppendData,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Deny));
            parent.SetAccessControl(sourceAccess);
            var destination = Path.Combine(root, "copied");
            File.WriteAllText(destination, "read-only");
            SetServiceAccess(destination, file: true);
            if (denyWrite)
            {
                var access = new FileInfo(destination).GetAccessControl();
                access.AddAccessRule(new(identity.User!, FileSystemRights.Write, AccessControlType.Allow));
                new FileInfo(destination).SetAccessControl(access);
                WindowsIdentity.RunImpersonated(token, () => File.WriteAllText(destination, "write-was-allowed"));
            }
            typeof(PackageSwitchTransaction).GetMethod("CopyFileAccessRules", BindingFlags.NonPublic | BindingFlags.Static)!
                .Invoke(null, [source, destination]);
            WindowsIdentity.RunImpersonated(token, () =>
            {
                Assert.NotEmpty(File.ReadAllText(destination));
                Assert.Throws<UnauthorizedAccessException>(() => File.WriteAllText(destination, "must-stay-read-only"));
            });
            if (denyWrite)
                Assert.Contains(new FileInfo(destination).GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier))
                    .Cast<FileSystemAccessRule>(), rule => !rule.IsInherited && rule.IdentityReference.Equals(identity.User)
                        && rule.AccessControlType == AccessControlType.Deny);
        }
        finally { Cleanup(root); }
    }

    private static void SetServiceAccess(string path, bool creatorOwner = false, bool createChildren = false, bool file = false)
    {
        FileSystemSecurity security = file ? new FileSecurity() : new DirectorySecurity();
        security.SetAccessRuleProtection(true, false);
        var inheritance = file ? InheritanceFlags.None : InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null), FileSystemRights.FullControl,
                inheritance, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
            FileSystemRights.ReadAndExecute, inheritance, PropagationFlags.None, AccessControlType.Allow));
        if (createChildren)
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
                FileSystemRights.CreateDirectories, InheritanceFlags.None, PropagationFlags.None, AccessControlType.Allow));
        if (creatorOwner)
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.CreatorOwnerSid, null),
                FileSystemRights.FullControl, inheritance, PropagationFlags.InheritOnly, AccessControlType.Allow));
        if (file) new FileInfo(path).SetAccessControl((FileSecurity)security);
        else new DirectoryInfo(path).SetAccessControl((DirectorySecurity)security);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void FullCopyFinishesContentBeforeApplyingReadOnlyDirectoryPermissions(bool rootFile, bool denyWrite)
    {
        var root = CreateRoot();
        using var token = CreateUserToken();
        using var identity = WindowsIdentity.GetCurrent();
        try
        {
            var source = Directory.CreateDirectory(Path.Combine(root, "source"));
            var relative = rootFile ? "data" : Path.Combine("child", "deep", "data");
            var sourceFile = Path.Combine(source.FullName, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(sourceFile)!);
            File.WriteAllText(sourceFile, "read-only-content");
            var access = new DirectorySecurity();
            access.SetAccessRuleProtection(true, false);
            access.AddAccessRule(new(identity.User!, FileSystemRights.ReadAndExecute,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            if (denyWrite)
                access.AddAccessRule(new(identity.User!, FileSystemRights.Write,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Deny));
            source.SetAccessControl(access);
            var destination = Path.Combine(root, "copied");
            typeof(PackageSwitchTransaction).GetMethod("CopyTree", BindingFlags.NonPublic | BindingFlags.Static)!
                .Invoke(null, [source.FullName, destination, true]);
            WindowsIdentity.RunImpersonated(token, () =>
            {
                Assert.Equal("read-only-content", File.ReadAllText(Path.Combine(destination, relative)));
                Assert.Throws<UnauthorizedAccessException>(() =>
                    File.WriteAllText(Path.Combine(destination, "new-file"), "must-stay-read-only"));
                Assert.Throws<UnauthorizedAccessException>(() =>
                    Directory.CreateDirectory(Path.Combine(destination, "new-directory")));
                Assert.Throws<UnauthorizedAccessException>(() =>
                    File.WriteAllText(Path.Combine(destination, relative), "must-stay-read-only"));
            });
        }
        finally { Cleanup(root); }
    }

    internal static SafeAccessTokenHandle CreateUserToken()
    {
        Assert.True(OpenProcessToken(new IntPtr(-1), 0x0002 | 0x0008, out var original),
            "OpenProcessToken failed: " + Marshal.GetLastWin32Error());
        using (original)
        {
            var sid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            var bytes = new byte[sid.BinaryLength];
            sid.GetBinaryForm(bytes, 0);
            var pinned = GCHandle.Alloc(bytes, GCHandleType.Pinned);
            try
            {
                var disabled = new SidAndAttributes { Sid = pinned.AddrOfPinnedObject() };
                Assert.True(CreateRestrictedToken(original, 1, 1, ref disabled, 0, IntPtr.Zero, 0, IntPtr.Zero, out var token),
                    "CreateRestrictedToken failed: " + Marshal.GetLastWin32Error());
                return token;
            }
            finally { pinned.Free(); }
        }
    }

    private static void Cleanup(string root)
    {
        OwnedTestDirectoryPermissions.RestoreOwnerAccessForCleanup(root);
        // Exact test-owned tree; reject unexpected growth and reparse entries.
        var pending = new Queue<string>(); pending.Enqueue(root);
        var directories = new List<string>();
        while (pending.TryDequeue(out var current))
        {
            Assert.True(directories.Count < 64);
            Assert.False(File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint));
            directories.Add(current);
            foreach (var child in Directory.GetDirectories(current)) pending.Enqueue(child);
            foreach (var file in Directory.GetFiles(current)) File.Delete(file);
        }
        for (var index = directories.Count - 1; index >= 0; index--) Directory.Delete(directories[index]);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SidAndAttributes { public IntPtr Sid; public uint Attributes; }

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out SafeAccessTokenHandle token);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateRestrictedToken(SafeAccessTokenHandle original, uint flags, uint disableCount,
        ref SidAndAttributes disabled, uint privilegeCount, IntPtr privileges, uint restrictedCount,
        IntPtr restricted, out SafeAccessTokenHandle token);
}
