using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Resource_Manager_APP.Tests;

public sealed class FinalImagePackagingContractTests
{
    [Fact]
    public async Task FixtureBuildsHashClosedCombinedImageAndRejectsTampering()
    {
        var repository = FindRepositoryRoot();
        var builder = Path.Combine(repository, "scripts", "release", "New-ResourceManagerFinalImage.ps1");
        var validator = Path.Combine(repository, "scripts", "validation", "Test-ResourceManagerFinalImage.ps1");
        var root = Path.Combine(Path.GetTempPath(), $"ResourceManager.FinalImage.Contract.{Guid.NewGuid():N}");
        var backend = Path.Combine(root, "backend");
        var ui = Path.Combine(root, "ui");
        var launcher = Path.Combine(root, "launcher");
        var image = Path.Combine(root, "image");
        var sessionAgent = Path.Combine(root, "session-agent");
        try
        {
            foreach (var relative in new[]
                     {
                         "ResourceManager.exe", "wwwroot/index.html",
                         "GpuPlacementShim/ResourceManager.GpuWindowAction.exe",
                         "GpuPlacementShim/ResourceManager.GpuPlacementPreparation.exe",
                         "GpuPlacementShim/ResourceManager.GpuPlacementExternal.exe",
                         "GpuPlacementShim/ResourceManager.GpuRendererExternal.exe"
                     })
                WriteFixtureFile(backend, relative);
            WriteFixtureFile(ui, "ResourceManager.NativeUi.exe");
            WriteFixtureFile(ui, "InstallWebView2Runtime.ps1");
            WriteFixtureFile(launcher, "ResourceManager.Launcher.exe");
            WriteFixtureFile(sessionAgent, "ResourceManager.SessionAgent.exe");
            WriteFixtureFile(root, "src/Core/Native/PerformanceOverlay/bin/win-x64/ResourceManager.PerformanceOverlay.dll");
            WriteFixtureFile(root, "src/Core/Native/PerformanceOverlay/bin/win-x64/ResourceManager.VulkanPerformanceOverlayLayer.dll");
            var layerPath = Path.Combine(root, "src/Core/Native/PerformanceOverlay/ResourceManager.VulkanPerformanceOverlayLayer.json");
            Directory.CreateDirectory(Path.GetDirectoryName(layerPath)!);
            File.WriteAllText(layerPath, "{\"layer\":{\"name\":\"VK_LAYER_RESOURCE_MANAGER_performance_overlay\",\"library_path\":\"ResourceManager.VulkanPerformanceOverlayLayer.dll\"}}");

            var built = await RunScriptAsync(builder, "-BackendDirectory", backend,
                "-NativeUiDirectory", ui, "-LauncherDirectory", launcher, "-SessionAgentDirectory", sessionAgent,
                "-FrontendModuleSourceRoot", root, "-OutputDirectory", image);
            Assert.True(built.ExitCode == 0, built.Error + built.Output);
            var validated = await RunScriptAsync(validator, "-RootDirectory", image);
            Assert.True(validated.ExitCode == 0, validated.Error + validated.Output);
            using (var result = JsonDocument.Parse(validated.Output))
            {
                Assert.True(result.RootElement.GetProperty("Valid").GetBoolean());
                Assert.Equal(14, result.RootElement.GetProperty("PayloadFileCount").GetInt32());
            }

            using (var catalog = JsonDocument.Parse(File.ReadAllText(Path.Combine(image, "runtime-modules.json"))))
            {
                var modules = catalog.RootElement.GetProperty("modules").EnumerateArray().ToArray();
                Assert.Equal(2, modules.Length);
                foreach (var module in modules)
                {
                    var asset = module.GetProperty("image");
                    var relative = asset.GetProperty("relativePath").GetString()!;
                    Assert.StartsWith("ResourceManagerNativeUi/Modules/", relative);
                    var path = Path.Combine(image, relative);
                    Assert.Equal(new FileInfo(path).Length, asset.GetProperty("length").GetInt64());
                    Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))), asset.GetProperty("sha256").GetString());
                }
            }

            var imageCatalog = ResourceManager.Shared.Packages.PackageRuntimeCatalog.Load(image);
            Assert.Equal(2, imageCatalog.StartupModules.Count);
            Assert.Contains(Path.Combine(image, "ResourceManagerSessionAgent", "ResourceManager.SessionAgent.exe"), imageCatalog.InfrastructureImages);
            var package = Path.Combine(root, "package");
            Directory.CreateDirectory(package);
            var releaseFiles = new List<object>();
            using (var imageManifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(image, "resource-manager-final-image.manifest.json"))))
            {
                foreach (var entry in imageManifest.RootElement.GetProperty("files").EnumerateArray())
                {
                    var relative = entry.GetProperty("path").GetString()!;
                    var destination = Path.Combine(package, "Bin", relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    File.Copy(Path.Combine(image, relative), destination);
                    releaseFiles.Add(new { path = "Bin/" + relative, length = new FileInfo(destination).Length,
                        sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(destination))) });
                }
            }
            var releaseComposer = Path.Combine(repository, "scripts", "release", "Write-ResourceManagerReleaseRuntimeModules.ps1");
            var composed = await RunScriptAsync(releaseComposer, "-ImageRoot", image, "-PackageRoot", package);
            Assert.True(composed.ExitCode == 0, composed.Error + composed.Output);
            var catalogPath = Path.Combine(package, "runtime-modules.json");
            releaseFiles.Add(new { path = "runtime-modules.json", length = new FileInfo(catalogPath).Length,
                sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(catalogPath))) });
            File.WriteAllText(Path.Combine(package, "release-manifest.json"), JsonSerializer.Serialize(new { files = releaseFiles }));
            var installedCatalog = ResourceManager.Shared.Packages.PackageRuntimeCatalog.Load(package);
            Assert.Equal(2, installedCatalog.StartupModules.Count);
            Assert.All(installedCatalog.StartupModules, module => Assert.StartsWith("Bin/", module.Image.RelativePath));
            var retainedData = Path.Combine(root, "retained-data");
            var profiles = Path.Combine(retainedData, "UserData", "SoftwareProfiles");
            Directory.CreateDirectory(profiles);
            var legacyPath = Path.Combine(profiles, "performance-overlay.local.json");
            const string legacy = "{\"software\":[{\"softwareId\":\"injected\",\"enabled\":true,\"mode\":\"injected\"},{\"softwareId\":\"disabled\",\"enabled\":false,\"mode\":\"injected\"}]}";
            File.WriteAllText(legacyPath, legacy);
            ResourceManager.Frontend.Packaging.LegacyStartupModuleMigration.Migrate(package, retainedData);
            var genericPath = Path.Combine(profiles, "startup-modules.local.json");
            using (var migrated = JsonDocument.Parse(File.ReadAllText(genericPath)))
            {
                var selections = migrated.RootElement.GetProperty("software").EnumerateArray().ToArray();
                Assert.Empty(selections.Single(item => item.GetProperty("softwareId").GetString() == "disabled").GetProperty("moduleIds").EnumerateArray());
                Assert.Equal(installedCatalog.StartupModules.Select(module => module.Id),
                    selections.Single(item => item.GetProperty("softwareId").GetString() == "injected").GetProperty("moduleIds").EnumerateArray().Select(item => item.GetString()));
            }
            Assert.Equal(legacy, File.ReadAllText(legacyPath));
            const string emptySelection = "{\"version\":1,\"software\":[]}";
            File.WriteAllText(genericPath, emptySelection);
            ResourceManager.Frontend.Packaging.LegacyStartupModuleMigration.Migrate(package, retainedData);
            Assert.Equal(emptySelection, File.ReadAllText(genericPath));
            File.AppendAllText(installedCatalog.AssetPath(installedCatalog.StartupModules[0].Image), "tampered");
            Assert.Throws<InvalidDataException>(() => ResourceManager.Shared.Packages.PackageRuntimeCatalog.Load(package));

            var helper = Path.Combine(image, "ResourceManager", "GpuPlacementShim", "ResourceManager.GpuWindowAction.exe");
            var originalHelper = File.ReadAllBytes(helper);
            File.AppendAllText(helper, "tampered");
            Assert.NotEqual(0, (await RunScriptAsync(validator, "-RootDirectory", image)).ExitCode);
            File.WriteAllBytes(helper, originalHelper);

            var manifestPath = Path.Combine(image, "resource-manager-final-image.manifest.json");
            var originalManifest = File.ReadAllBytes(manifestPath);
            var manifest = JsonNode.Parse(originalManifest)!.AsObject();
            var entries = manifest["files"]!.AsArray();
            entries.Remove(entries.Single(item => item!["path"]!.GetValue<string>() ==
                "ResourceManager/GpuPlacementShim/ResourceManager.GpuWindowAction.exe"));
            File.WriteAllText(manifestPath, manifest.ToJsonString());
            Assert.NotEqual(0, (await RunScriptAsync(validator, "-RootDirectory", image)).ExitCode);
            File.WriteAllBytes(manifestPath, originalManifest);
            Assert.Equal(0, (await RunScriptAsync(validator, "-RootDirectory", image)).ExitCode);

            foreach (var (folder, relative) in new[]
                     {
                         (backend, "GpuPlacementShim/ResourceManager.GpuWindowAction.exe"),
                         (backend, "GpuPlacementShim/ResourceManager.GpuPlacementPreparation.exe"),
                         (backend, "GpuPlacementShim/ResourceManager.GpuPlacementExternal.exe"),
                         (backend, "GpuPlacementShim/ResourceManager.GpuRendererExternal.exe"),
                         (launcher, "ResourceManager.Launcher.exe"),
                         (sessionAgent, "ResourceManager.SessionAgent.exe")
                     })
            {
                var path = Path.Combine(folder, relative);
                var saved = File.ReadAllBytes(path);
                File.Delete(path);
                try
                {
                    var rejected = await RunScriptAsync(builder, "-BackendDirectory", backend,
                        "-NativeUiDirectory", ui, "-LauncherDirectory", launcher, "-SessionAgentDirectory", sessionAgent,
                        "-FrontendModuleSourceRoot", root,
                        "-OutputDirectory", Path.Combine(root, $"rejected-{Guid.NewGuid():N}"));
                    Assert.NotEqual(0, rejected.ExitCode);
                    Assert.Contains("Published input is incomplete", rejected.Error + rejected.Output);
                }
                finally { File.WriteAllBytes(path, saved); }
            }
        }
        finally
        {
            var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            Assert.StartsWith(temp, Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static void WriteFixtureFile(string root, string relative)
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, relative);
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunScriptAsync(string script, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script }.Concat(arguments))
            startInfo.ArgumentList.Add(argument);
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start the image fixture.");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw;
        }
        return (process.ExitCode, await outputTask, await errorTask);
    }

    private static string FindRepositoryRoot(
        [System.Runtime.CompilerServices.CallerFilePath] string sourceFilePath = "")
    {
        var repositoryRoot = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFilePath)!, "..", ".."));
        return File.Exists(Path.Combine(repositoryRoot, "scripts", "release", "New-ResourceManagerFinalImage.ps1"))
            ? repositoryRoot
            : throw new DirectoryNotFoundException("Could not locate the image builder from the test source path.");
    }
}
