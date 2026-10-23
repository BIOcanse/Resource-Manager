using System.Security.AccessControl;
using System.Security.Principal;
using ResourceManager.Updater;

namespace Resource_Manager_APP.Tests;

public sealed class PackageSwitchAccessRuleTests
{
    [Theory]
    [InlineData("GA", 0x10000000)]
    [InlineData("GRGWGXRC", unchecked((int)0xE0020000))]
    [InlineData("0xE0010000", unchecked((int)0xE0010000))]
    public void OwnerInheritedGenericMaskIsKeptWithoutTheTypedConstructor(string rights, int mask)
    {
        var owner = WindowsIdentity.GetCurrent().User!;
        var source = new DirectorySecurity();
        source.SetSecurityDescriptorSddlForm($"O:{owner.Value}D:(A;OICIIOID;{rights};;;{owner.Value})");
        var destination = new DirectorySecurity();
        PackageSwitchTransaction.CopyOwnedAccessRules(source, destination);
        var descriptor = new RawSecurityDescriptor(destination.GetSecurityDescriptorBinaryForm(), 0);
        var ace = Assert.Single(descriptor.DiscretionaryAcl!.Cast<CommonAce>());
        Assert.Equal(owner, ace.SecurityIdentifier);
        Assert.Equal(mask, ace.AccessMask);
        Assert.Equal(AceQualifier.AccessAllowed, ace.AceQualifier);
        Assert.Equal(AceFlags.ContainerInherit | AceFlags.ObjectInherit | AceFlags.InheritOnly, ace.AceFlags);
    }

    [Fact]
    public void ExplicitDenyAndProtectionStayWhileUnrelatedInheritedRightsAreNotCopied()
    {
        var owner = WindowsIdentity.GetCurrent().User!;
        var source = new DirectorySecurity();
        source.SetSecurityDescriptorSddlForm($"O:{owner.Value}D:P(D;;GW;;;WD)(A;OICIIO;GA;;;{owner.Value})(A;OICIIOID;GA;;;BA)");
        var destination = new DirectorySecurity();
        PackageSwitchTransaction.CopyOwnedAccessRules(source, destination);
        Assert.True(destination.AreAccessRulesProtected);
        var descriptor = new RawSecurityDescriptor(destination.GetSecurityDescriptorBinaryForm(), 0);
        var rules = descriptor.DiscretionaryAcl!.Cast<CommonAce>().ToArray();
        Assert.Contains(rules, row => row.AceQualifier == AceQualifier.AccessDenied
            && row.AccessMask == 0x40000000 && row.SecurityIdentifier.IsWellKnown(WellKnownSidType.WorldSid));
        Assert.Contains(rules, row => row.SecurityIdentifier == owner && row.AccessMask == 0x10000000);
        if (!owner.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid))
            Assert.DoesNotContain(rules, row => row.SecurityIdentifier.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid));
    }

    [Fact]
    public void ActualConfigCopyWithGenericInheritOnlyRightsRetainsBytesAndOwnerAccess()
    {
        var sandbox = Path.Combine(Path.GetTempPath(), "rm-update-generic-acl-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(sandbox, "installed");
        var target = Path.Combine(sandbox, "next");
        var sourceConfig = Path.Combine(source, "Config");
        var targetConfig = Path.Combine(target, "Config");
        Directory.CreateDirectory(sourceConfig);
        try
        {
            var info = new DirectoryInfo(sourceConfig);
            var security = info.GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
            var owner = (SecurityIdentifier)security.GetOwner(typeof(SecurityIdentifier));
            var descriptor = new CommonSecurityDescriptor(true, false, security.GetSecurityDescriptorBinaryForm(), 0);
            descriptor.DiscretionaryAcl!.AddAccess(AccessControlType.Allow, owner, 0x10000000,
                InheritanceFlags.ObjectInherit | InheritanceFlags.ContainerInherit, PropagationFlags.InheritOnly);
            var binary = new byte[descriptor.BinaryLength];
            descriptor.GetBinaryForm(binary, 0);
            security.SetSecurityDescriptorBinaryForm(binary, AccessControlSections.Access);
            info.SetAccessControl(security);
            File.WriteAllText(Path.Combine(sourceConfig, "settings.json"), "unaltered-user-settings");
            PackageSwitchTransaction.CopyUserData(source, target);
            Assert.Equal("unaltered-user-settings", File.ReadAllText(Path.Combine(targetConfig, "settings.json")));
            Assert.Contains(new DirectoryInfo(targetConfig).GetAccessControl().GetAccessRules(true, false,
                typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>(), rule => rule.IdentityReference.Equals(owner));
        }
        finally
        {
            // This fixture creates exactly these two files and four directories.
            foreach (var config in new[] { sourceConfig, targetConfig })
            {
                File.Delete(Path.Combine(config, "settings.json"));
                if (Directory.Exists(config)) Directory.Delete(config);
            }
            foreach (var path in new[] { source, target, sandbox })
                if (Directory.Exists(path)) Directory.Delete(path);
        }
    }
}
