using Microsoft.Win32;
using ResourceManager.Shared.Packages;

namespace Resource_Manager_APP.Tests;

public sealed class ProductInstallationRegistrationTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void OwnedUninstallCommandRemovesObsoleteQuietScriptAndRemainsIdempotent(bool hasLegacyValues, bool externalManager)
    {
        using var hive = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        var keyPath = @"Software\ResourceManager.Registration.Tests\" + Guid.NewGuid().ToString("N");
        var root = Path.Combine(Path.GetTempPath(), "Resource Manager registration fixture");
        var manager = externalManager ? Path.Combine(Path.GetTempPath(), "ResourceManager legacy maintenance")
            : Path.Combine(root, "Internal", "Management");
        try
        {
            using var key = hive.CreateSubKey(keyPath);
            key.SetValue("DisplayVersion", "fixture-version");
            key.SetValue("UninstallString", "old-uninstall");
            key.SetValue("UnrelatedValue", "preserve");
            if (hasLegacyValues)
            {
                key.SetValue("QuietUninstallString", "powershell.exe -File \"" + Path.Combine(root, "scripts", "Register-ResourceManager.ps1") + "\"");
                key.SetValue("NoRemove", 1, RegistryValueKind.DWord);
            }
            for (var attempt = 0; attempt < 2; attempt++)
            {
                ProductInstallationRegistration.WriteUninstallCommand(key, manager);
                Assert.Equal("\"" + Path.Combine(manager, UninstallationPlan.ExecutableName) + "\"",
                    key.GetValue("UninstallString"));
                Assert.Null(key.GetValue("QuietUninstallString"));
                Assert.Null(key.GetValue("NoRemove"));
                Assert.Equal("fixture-version", key.GetValue("DisplayVersion"));
                Assert.Equal("preserve", key.GetValue("UnrelatedValue"));
                Assert.Equal(0, key.SubKeyCount);
            }
        }
        finally
        {
            hive.DeleteSubKey(keyPath, false);
        }
    }
}
