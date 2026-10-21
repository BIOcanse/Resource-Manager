using System.Security.AccessControl;
using System.Security.Principal;
using ResourceManager.Shared.Packages;

namespace Resource_Manager_APP.Tests;

public sealed class ImportedUserCacheAccessTests
{
    [Fact]
    public void ImportRestoresCurrentUserCacheWriteAccessUsingTheActualUserIdentity()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User!;
        var fixture = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "rm-import-cache-test-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            var source = Directory.CreateDirectory(Path.Combine(fixture, "source")).FullName;
            var path = Path.Combine("Config", "WebView2", sid.Value, "Profile");
            Directory.CreateDirectory(Path.Combine(source, path));
            File.WriteAllText(Path.Combine(source, path, "preferences"), "saved preferences");
            var zip = Path.Combine(fixture, "data.zip");
            UserDataArchive.Create(source, zip);
            var target = Directory.CreateDirectory(Path.Combine(fixture, "target")).FullName;
            UserDataArchive.Import(target, zip);
            Assert.Equal("saved preferences", File.ReadAllText(Path.Combine(target, path, "preferences")));
            if (!sid.IsWellKnown(WellKnownSidType.LocalSystemSid))
            {
                var security = new DirectoryInfo(Path.Combine(target, "Config", "WebView2", sid.Value)).GetAccessControl();
                Assert.Equal(sid, security.GetOwner(typeof(SecurityIdentifier)));
                Assert.Contains(security.GetAccessRules(true, false, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>(), rule =>
                    rule.IdentityReference.Equals(sid) && rule.AccessControlType == AccessControlType.Allow &&
                    rule.FileSystemRights.HasFlag(FileSystemRights.FullControl));
            }
        }
        finally { OwnedTestDirectoryPermissions.RestoreOwnerAccessForCleanup(fixture); Directory.Delete(fixture, true); }
    }
}
