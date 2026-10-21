using ResourceManager.Installer;
using ResourceManager.Shared.Packages;

namespace Resource_Manager_APP.Tests;

public sealed class RetainedInstallArchiveTests
{
    [Fact]
    public void ReinstallStoresTheRetainedZipInsideManagedDataForFutureUpdatesAndUninstalls()
    {
        var fixture = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "rm-retained-install-test-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            var source = Directory.CreateDirectory(Path.Combine(fixture, "source", "UserData")).Parent!.FullName;
            File.WriteAllText(Path.Combine(source, "UserData", "profile"), "original");
            var oldRoot = Directory.CreateDirectory(Path.Combine(fixture, "old")).FullName;
            var original = Path.Combine(oldRoot, "ResourceManager-user-data-fixture.zip");
            UserDataArchive.Create(source, original);
            Assert.Equal(original, UserDataArchive.RetainedArchiveAt(oldRoot));
            var fresh = Directory.CreateDirectory(Path.Combine(fixture, "fresh")).FullName;
            var saved = InstallerWorkflow.PreserveRetainedArchive(original, fresh);
            Assert.Equal(Path.Combine(fresh, "UserData", "Archives", Path.GetFileName(original)), saved);
            Assert.True(File.Exists(original));
            var nextZip = Path.Combine(fixture, "next-data.zip");
            UserDataArchive.Create(fresh, nextZip);
            var restored = Directory.CreateDirectory(Path.Combine(fixture, "restored")).FullName;
            UserDataArchive.Import(restored, nextZip);
            var retained = Path.Combine(restored, "UserData", "Archives", Path.GetFileName(original));
            UserDataArchive.Verify(retained);
            var imported = Directory.CreateDirectory(Path.Combine(fixture, "imported")).FullName;
            UserDataArchive.Import(imported, retained);
            Assert.Equal("original", File.ReadAllText(Path.Combine(imported, "UserData", "profile")));
        }
        finally { OwnedTestDirectoryPermissions.RestoreOwnerAccessForCleanup(fixture); Directory.Delete(fixture, true); }
    }
}
