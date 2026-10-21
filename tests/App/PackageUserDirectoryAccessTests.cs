using System.Security.AccessControl;
using System.Security.Principal;
using ResourceManager.Shared.Packages;
using ResourceManager.Shared.Localization;

namespace Resource_Manager_APP.Tests;

public sealed class PackageUserDirectoryAccessTests
{
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
