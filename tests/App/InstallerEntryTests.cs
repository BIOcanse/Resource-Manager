using System.Security.Cryptography;
using System.Text.Json;
using ResourceManager.Installer;
using ResourceManager.Shared.ServiceHosting;

namespace Resource_Manager_APP.Tests;

public sealed class InstallerEntryTests
{
    [Fact]
    public void PackagePreflightChecksEveryMemberAndRejectsUnexpectedScripts()
    {
        var root = Path.Combine(Path.GetTempPath(), "rm-install-fixture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "Config"));
            var files = new[]
            {
                "Install.exe", "Start.exe", "Bin/ResourceManager/ResourceManager.exe",
                "Bin/ResourceManager/wwwroot/index.html",
                "Bin/ResourceManagerNativeUi/ResourceManager.NativeUi.exe",
                "Bin/ResourceManagerLauncher/ResourceManager.Launcher.exe"
            };
            var records = files.Select(relative =>
            {
                var full = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllText(full, "fixture: " + relative);
                return new { path = relative, length = new FileInfo(full).Length,
                    sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(full))) };
            }).ToArray();
            File.WriteAllText(Path.Combine(root, "release-manifest.json"), JsonSerializer.Serialize(new
            {
                sourceCommit = "fixture", version = "0.0.1", files = records
            }));
            Assert.Equal("0.0.1", PackageLayout.Verify(root).Version);
            File.WriteAllText(Path.Combine(root, "Install.cmd"), "unexpected");
            Assert.Throws<InvalidDataException>(() => PackageLayout.Verify(root));
            File.Delete(Path.Combine(root, "Install.cmd"));
            File.AppendAllText(Path.Combine(root, "Start.exe"), "tampered");
            Assert.Throws<InvalidDataException>(() => PackageLayout.Verify(root));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void ShellShortcutCanBeCreatedWithoutRunningAProduct()
    {
        var root = Path.Combine(Path.GetTempPath(), "rm-shortcut-fixture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var target = Path.Combine(root, "Start.exe");
            var shortcut = Path.Combine(root, "Resource Manager.lnk");
            File.WriteAllText(target, "fixture");
            StartMenuShortcut.Create(target, shortcut);
            Assert.True(new FileInfo(shortcut).Length > 0);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void LauncherServiceActionDoesNotRegisterAMissingService()
    {
        var name = "ResourceManager.AbsentLauncherTest." + Guid.NewGuid().ToString("N");
        Assert.Throws<System.ComponentModel.Win32Exception>(() =>
            WindowsServiceRegistration.StartExisting(name, Path.Combine(Path.GetTempPath(), "fixture.exe"), false));
        Assert.Null(WindowsServiceRegistration.Read(name));
    }
}
