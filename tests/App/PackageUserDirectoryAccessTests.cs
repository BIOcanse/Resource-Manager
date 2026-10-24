using System.Security.AccessControl;
using System.Security.Principal;
using ResourceManager.Shared.Packages;
using ResourceManager.Shared.Localization;

namespace Resource_Manager_APP.Tests;

public sealed class PackageUserDirectoryAccessTests
{
    [Fact]
    public void RestoredCacheAccessUsesEveryValidatedDirectorySid()
    {
        var root = Path.Combine(Path.GetTempPath(), "rm-cache-sids-" + Guid.NewGuid().ToString("N"));
        var users = new[] { "S-1-5-21-111-222-333-1001", "S-1-5-21-111-222-333-1002" };
        try
        {
            foreach (var relative in new[] { "Config/WebView2", "Config/Diagnostics/NativeUiStartup" })
                foreach (var user in users)
                    Directory.CreateDirectory(Path.Combine(root, relative, user, "Profile"));
            PackageUserDirectoryAccess.PrepareRestoredUserCache(root);
            foreach (var relative in new[] { "Config/WebView2", "Config/Diagnostics/NativeUiStartup" })
                foreach (var user in users)
                {
                    var rules = new DirectoryInfo(Path.Combine(root, relative, user, "Profile")).GetAccessControl()
                        .GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>();
                    Assert.Contains(rules, rule => rule.IdentityReference.Value == user && rule.IsInherited
                        && (rule.FileSystemRights & FileSystemRights.Modify) == FileSystemRights.Modify);
                    Assert.DoesNotContain(rules, rule => users.Contains(rule.IdentityReference.Value) && rule.IdentityReference.Value != user);
                }
        }
        finally
        {
            OwnedTestDirectoryPermissions.RestoreOwnerAccessForCleanup(root);
            foreach (var relative in new[] { "Config/WebView2", "Config/Diagnostics/NativeUiStartup" })
                foreach (var user in users)
                {
                    Directory.Delete(Path.Combine(root, relative, user, "Profile"));
                    Directory.Delete(Path.Combine(root, relative, user));
                }
            Directory.Delete(Path.Combine(root, "Config", "WebView2"));
            Directory.Delete(Path.Combine(root, "Config", "Diagnostics", "NativeUiStartup"));
            Directory.Delete(Path.Combine(root, "Config", "Diagnostics"));
            Directory.Delete(Path.Combine(root, "Config"));
            Directory.Delete(root);
        }
    }

    [Theory]
    [InlineData("not-a-sid")]
    [InlineData("S-1-1-0")]
    [InlineData("S-1-5-18")]
    public void RestoredCacheRejectsNamesThatDoNotIdentifyUserAccounts(string name)
        => Assert.Throws<InvalidDataException>(() => PackageUserDirectoryAccess.RequireCacheUser(name));
    [Fact]
    public void UserCreatedProfilesInheritOwnerAccessWithoutGrantingUsersWriteToGlobalConfig()
    {
        var root = Path.Combine(Path.GetTempPath(), "rm-user-space-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var config = Directory.CreateDirectory(Path.Combine(root, "Config"));
            using var fixtureIdentity = WindowsIdentity.GetCurrent();
            var fixtureAccess = new DirectorySecurity();
            fixtureAccess.SetAccessRuleProtection(true, false);
            fixtureAccess.AddAccessRule(new(fixtureIdentity.User!, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            fixtureAccess.AddAccessRule(new(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.ReadAndExecute,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            config.SetAccessControl(fixtureAccess);
            PackageUserDirectoryAccess.Prepare(root);
            using var identity = WindowsIdentity.GetCurrent();
            var sid = identity.User!;
            var profile = Path.Combine(root, "Config", "WebView2", sid.Value, "Profile");
            Directory.CreateDirectory(profile);
            File.WriteAllText(Path.Combine(profile, "cache"), "user-cache");
            var rules = new DirectoryInfo(profile).GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier))
                .Cast<FileSystemAccessRule>().ToArray();
            Assert.Contains(rules, rule => rule.IdentityReference.Equals(sid) && rule.AccessControlType == AccessControlType.Allow
                && (rule.FileSystemRights & FileSystemRights.Modify) == FileSystemRights.Modify);
            var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
            Assert.DoesNotContain(rules, rule => rule.IdentityReference.Equals(users));
            var configRules = new DirectoryInfo(Path.Combine(root, "Config")).GetAccessControl()
                .GetAccessRules(false, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>();
            Assert.DoesNotContain(configRules, rule => rule.IdentityReference.Equals(users)
                && (rule.FileSystemRights & FileSystemRights.WriteData) != 0);
        }
        finally
        {
            OwnedTestDirectoryPermissions.RestoreOwnerAccessForCleanup(root);
            var pending = new Stack<string>(); var directories = new List<string>(); pending.Push(root);
            while (pending.TryPop(out var directory))
            {
                directories.Add(directory);
                foreach (var file in Directory.GetFiles(directory)) File.Delete(file);
                foreach (var child in Directory.GetDirectories(directory)) pending.Push(child);
            }
            for (var i = directories.Count - 1; i >= 0; i--) Directory.Delete(directories[i]);
        }
    }

    [Theory]
    [InlineData("zh-CN")][InlineData("zh-TW")][InlineData("en-US")][InlineData("ja-JP")][InlineData("ko-KR")]
    [InlineData("fr-FR")][InlineData("de-DE")][InlineData("es-ES")][InlineData("ru-RU")]
    public void InstallerExplainsUserDirectoryPermissionsInEverySupportedLanguage(string language)
        => Assert.False(string.IsNullOrWhiteSpace(ToolText.For(language).InstallUserDirectories));
}
