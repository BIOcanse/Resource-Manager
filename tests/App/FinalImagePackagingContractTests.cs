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

            var built = await RunScriptAsync(builder, "-BackendDirectory", backend,
                "-NativeUiDirectory", ui, "-LauncherDirectory", launcher, "-OutputDirectory", image);
            Assert.Equal(0, built.ExitCode);
            var validated = await RunScriptAsync(validator, "-RootDirectory", image);
            Assert.Equal(0, validated.ExitCode);
            using (var result = JsonDocument.Parse(validated.Output))
            {
                Assert.True(result.RootElement.GetProperty("Valid").GetBoolean());
                Assert.Equal(9, result.RootElement.GetProperty("PayloadFileCount").GetInt32());
            }

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
                         (launcher, "ResourceManager.Launcher.exe")
                     })
            {
                var path = Path.Combine(folder, relative);
                var saved = File.ReadAllBytes(path);
                File.Delete(path);
                try
                {
                    var rejected = await RunScriptAsync(builder, "-BackendDirectory", backend,
                        "-NativeUiDirectory", ui, "-LauncherDirectory", launcher,
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
