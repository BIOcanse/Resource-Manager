using Microsoft.Win32;
using ResourceManager.NativeUi.SystemIntegration;

namespace Resource_Manager_APP.Tests;

public sealed class LoginStartupRegistrationTests
{
    [Fact]
    public void ActualIsolatedRunValueIsTheOnlyStateAndForeignValuesArePreserved()
    {
        // The isolated Run key must not end in CurrentVersion\Run: security products revert its deletion.
        const string runKey = "StartupRun";
        var name = @"Software\ResourceManager.Tests.LoginStartup\" + Guid.NewGuid().ToString("N");
        using var hive = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        try
        {
            using var root = hive.CreateSubKey(name);
            var executable = Path.GetFullPath("test frontend.exe");
            var owner = new LoginStartupRegistration(executable, runKey);
            Assert.False(owner.Read(root));
            Assert.True(owner.Set(root, true));
            using var run = root.OpenSubKey(runKey, writable: true)!;
            Assert.Equal($"\"{executable}\" --background-startup", run.GetValue(LoginStartupRegistration.ValueName));
            Assert.True(new LoginStartupRegistration(executable, runKey).Read(root));
            Assert.False(owner.Set(root, false));
            Assert.Null(run.GetValue(LoginStartupRegistration.ValueName));
            run.SetValue(LoginStartupRegistration.ValueName, "foreign.exe --action");
            Assert.Throws<InvalidDataException>(() => owner.Read(root));
            Assert.Throws<InvalidDataException>(() => owner.Set(root, true));
            Assert.Throws<InvalidDataException>(() => owner.Set(root, false));
            Assert.Equal("foreign.exe --action", run.GetValue(LoginStartupRegistration.ValueName));
        }
        finally { hive.DeleteSubKeyTree(name, throwOnMissingSubKey: false); }
    }
}
