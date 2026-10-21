using System.Security.AccessControl;
using System.Security.Principal;
using ResourceManager.App.Infrastructure.Security;

namespace Resource_Manager_APP.Tests;

public sealed class WindowsBackendProcessIdentityAccessTests
{
    [Fact]
    public void AddsOnlyLimitedQueryAndPreservesTheExistingDescriptor()
    {
        var descriptor = new RawSecurityDescriptor("O:SYG:SYD:(A;;GA;;;SY)(A;;GA;;;BA)");
        var acl = descriptor.DiscretionaryAcl!;
        var original = acl.Cast<GenericAce>().Select(Bytes).ToArray();
        Assert.True(WindowsBackendProcessIdentityAccess.AddQueryAccess(descriptor));
        Assert.Equal(original, acl.Cast<GenericAce>().Take(2).Select(Bytes).ToArray());
        var added = Assert.IsType<CommonAce>(acl[2]);
        Assert.Equal(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null), added.SecurityIdentifier);
        Assert.Equal(0x1000, added.AccessMask);
        Assert.Equal(AceFlags.None, added.AceFlags);
        Assert.Equal(AceQualifier.AccessAllowed, added.AceQualifier);
        Assert.Equal("S-1-5-18", descriptor.Owner!.Value);
        Assert.Equal("S-1-5-18", descriptor.Group!.Value);
        var once = descriptor.GetSddlForm(AccessControlSections.All);
        Assert.False(WindowsBackendProcessIdentityAccess.AddQueryAccess(descriptor));
        Assert.Equal(once, descriptor.GetSddlForm(AccessControlSections.All));
    }

    [Fact]
    public void KeepsExplicitDeniesAndInsertsBeforeInheritedAces()
    {
        var descriptor = new RawSecurityDescriptor("D:(D;;0x20;;;AU)(A;;GA;;;SY)(A;ID;0x1000;;;BA)");
        Assert.True(WindowsBackendProcessIdentityAccess.AddQueryAccess(descriptor));
        Assert.Equal(AceQualifier.AccessDenied, Assert.IsType<CommonAce>(descriptor.DiscretionaryAcl![0]).AceQualifier);
        Assert.Equal("S-1-5-11", Assert.IsType<CommonAce>(descriptor.DiscretionaryAcl[2]).SecurityIdentifier.Value);
        Assert.Equal(AceFlags.Inherited, descriptor.DiscretionaryAcl[3].AceFlags);
    }

    [Fact]
    public void DoesNotReplaceANullDacl()
    {
        var descriptor = new RawSecurityDescriptor(ControlFlags.DiscretionaryAclPresent, null, null, null, null);
        Assert.False(WindowsBackendProcessIdentityAccess.AddQueryAccess(descriptor));
        Assert.Null(descriptor.DiscretionaryAcl);
    }

    private static byte[] Bytes(GenericAce ace)
    {
        var bytes = new byte[ace.BinaryLength];
        ace.GetBinaryForm(bytes, 0);
        return bytes;
    }
}
