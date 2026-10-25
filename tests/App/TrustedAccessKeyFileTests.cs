using System.Security.AccessControl;
using System.Security.Principal;
using System.Diagnostics;
using ResourceManager.Shared.Runtime.TrustedAccess;

namespace Resource_Manager_APP.Tests;

public sealed class TrustedAccessKeyFileTests
{
    [Fact]
    public void PathForPackage_UsesTheManagedSecurityLocation()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "trusted-access-package"));

        Assert.Equal(
            Path.Combine(root, "UserData", "Security", "default-key"),
            TrustedAccessKeyFile.PathForPackage(root));
    }

    [Fact]
    public void ReadRejectsAnUnprotectedSecurityDirectory()
    {
        using var temporary = new TrustedAccessTemporaryDirectory();
        var securityDirectory = Path.Combine(temporary.Root, "UserData", "Security");
        Directory.CreateDirectory(securityDirectory);
        File.WriteAllText(Path.Combine(securityDirectory, "default-key"), string.Empty);

        Assert.Throws<UnauthorizedAccessException>(() => TrustedAccessKeyFile.Read(temporary.Root));
    }

    [Fact]
    public async Task ReadRejectsAnAncestorReparsePoint()
    {
        using var temporary = new TrustedAccessTemporaryDirectory();
        var target = Path.Combine(temporary.Root, "target");
        Directory.CreateDirectory(Path.Combine(target, "Security"));
        var userDataLink = Path.Combine(temporary.Root, "UserData");
        using var command = Process.Start(new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            ArgumentList = { "/c", "mklink", "/J", userDataLink, target }
        })!;
        await command.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, command.ExitCode);

        Assert.Throws<UnauthorizedAccessException>(() => TrustedAccessKeyFile.Read(temporary.Root));
    }

    [Fact]
    public void WriteAtomicCreatesLocalUsersReadOnlyAclOrRejectsAnUnprivilegedWriter()
    {
        using var temporary = new TrustedAccessTemporaryDirectory();
        var key = "rm_sk_" + new string('a', 64);
        using var identity = WindowsIdentity.GetCurrent();
        var isSystem = identity.User?.IsWellKnown(WellKnownSidType.LocalSystemSid) == true;
        var isAdministrator = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        if (!isSystem && !isAdministrator)
        {
            Assert.Throws<UnauthorizedAccessException>(() => TrustedAccessKeyFile.WriteAtomic(temporary.Root, key));
            return;
        }

        TrustedAccessKeyFile.WriteAtomic(temporary.Root, key);
        Assert.Equal(key, TrustedAccessKeyFile.Read(temporary.Root));

        var path = TrustedAccessKeyFile.PathForPackage(temporary.Root);
        var security = new FileInfo(path).GetAccessControl(
            AccessControlSections.Owner | AccessControlSections.Access);
        Assert.True(security.AreAccessRulesProtected);
        var usersSid = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        var usersRule = Assert.Single(security.GetAccessRules(
                includeExplicit: true,
                includeInherited: false,
                targetType: typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .Where(rule => rule.IdentityReference.Equals(usersSid)));
        Assert.Equal(FileSystemRights.Read | FileSystemRights.Synchronize, usersRule.FileSystemRights);
        Assert.False(TrustedAccessKeyFile.Read(temporary.Root)!.Contains("\r", StringComparison.Ordinal));

        File.WriteAllText(path, "malformed");
        Assert.Throws<InvalidDataException>(() => TrustedAccessKeyFile.Read(temporary.Root));
        File.WriteAllText(path, new string('x', 100));
        Assert.Throws<InvalidDataException>(() => TrustedAccessKeyFile.Read(temporary.Root));
    }

    [Fact]
    public void KeyFileValidationRejectsMalformedAndOversizedValues()
    {
        Assert.True(TrustedAccessKeyFile.IsApiKey("rm_sk_" + new string('a', 64)));
        Assert.False(TrustedAccessKeyFile.IsApiKey("rm_sk_" + new string('g', 64)));
        Assert.False(TrustedAccessKeyFile.IsApiKey("rm_sk_" + new string('a', 63)));
        Assert.Throws<InvalidDataException>(() => TrustedAccessKeyFile.ValidateKeyFileLength(73));
        TrustedAccessKeyFile.ValidateKeyFileLength(72);
        Assert.Throws<ArgumentException>(() => TrustedAccessKeyFile.WriteAtomic(packageRoot: "unused", key: "malformed"));
    }
}
