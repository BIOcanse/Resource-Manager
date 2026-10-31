using System.Security.Cryptography;
using System.Text.Json;
using ResourceManager.Installer;
using ResourceManager.Shared.ServiceHosting;
using ResourceManager.Shared.Desktop;

namespace Resource_Manager_APP.Tests;

public sealed class InstallerEntryTests
{
    [Fact]
    public void PackageCannotOmitTheResidentEvenWhenTheFileListOmitsItToo()
    {
        var root = Path.Combine(Path.GetTempPath(), "rm-missing-resident-" + Guid.NewGuid().ToString("N"));
        try
        {
            PackageSwitchTransactionTests.CreatePackage(root, "0.3.7");
            var resident = ResourceManager.Shared.Packages.ProductDesktopLayout.ResidentFor(root);
            File.Delete(resident);
            var manifestPath = Path.Combine(root, "release-manifest.json");
            var manifest = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(manifestPath))!;
            var relative = Path.GetRelativePath(root, resident).Replace('\\', '/');
            var files = manifest["files"]!.AsArray();
            files.Remove(files.Single(file => file!["path"]!.GetValue<string>() == relative));
            File.WriteAllText(manifestPath, manifest.ToJsonString());
            Assert.Contains(ResourceManager.Shared.Packages.ProductDesktopLayout.ResidentExecutableName,
                Assert.Throws<InvalidDataException>(() => PackageLayout.Verify(root)).Message);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

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
                "Install.exe", "Start.exe", "UninstallResourceManager.exe", "Bin/ResourceManager/ResourceManager.exe",
                "Bin/ResourceManager/wwwroot/index.html",
                "Bin/ResourceManagerNativeUi/ResourceManager.NativeUi.exe",
                "Bin/" + ResourceManager.Shared.Packages.ProductDesktopLayout.DirectoryName + "/" + ResourceManager.Shared.Packages.ProductDesktopLayout.ResidentExecutableName,
                "Bin/ResourceManagerLauncher/ResourceManager.Launcher.exe",
                "Internal/UpdateManager/ResourceManager.UpdateManager.exe",
                "Bin/ResourceManagerSessionAgent/ResourceManager.SessionAgent.exe", "runtime-modules.json"
            };
            var records = files.Select(relative =>
            {
                var full = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllText(full, relative == "runtime-modules.json" ? "{\"version\":1,\"modules\":[]}" : "fixture: " + relative);
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
            Assert.Equal(target, StartMenuShortcut.ReadTarget(shortcut));
            var managerShortcut = Path.Combine(root, "Resource Manager 更新管理.lnk");
            StartMenuShortcut.CreateManager(target, managerShortcut);
            Assert.True(new FileInfo(managerShortcut).Length > 0);
            Assert.Equal(target, StartMenuShortcut.ReadTarget(managerShortcut));
            Assert.Equal(Path.Combine(root, "ResourceManager", "Internal", "Management", "ResourceManager.UpdateManager.exe"),
                ResourceManager.Shared.Packages.UpdateManagerPaths.InstalledExecutable(Path.Combine(root, "ResourceManager")));
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
