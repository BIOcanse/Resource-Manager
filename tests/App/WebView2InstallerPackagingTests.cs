using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Resource_Manager_APP.Tests;

public sealed class WebView2InstallerPackagingTests
{
    [Theory]
    [InlineData(true, false, 0)]
    [InlineData(false, false, 1)]
    [InlineData(true, true, 1)]
    public async Task ImageRequiresInstallerAndStillRejectsDevelopmentScripts(
        bool includeInstaller, bool includeDevelopmentScript, int expectedExit)
    {
        var root = Path.Combine(Path.GetTempPath(), "rm-webview-package-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var paths = new List<string> { "backend/ResourceManager.exe", "backend/wwwroot/index.html",
                "backend/GpuPlacementShim/ResourceManager.GpuWindowAction.exe",
                "backend/GpuPlacementShim/ResourceManager.GpuPlacementPreparation.exe",
                "backend/GpuPlacementShim/ResourceManager.GpuPlacementExternal.exe",
                "backend/GpuPlacementShim/ResourceManager.GpuRendererExternal.exe",
                "native/ResourceManager.NativeUi.exe", "launcher/ResourceManager.Launcher.exe",
                "session-agent/ResourceManager.SessionAgent.exe",
                "src/Core/Native/PerformanceOverlay/bin/win-x64/ResourceManager.PerformanceOverlay.dll",
                "src/Core/Native/PerformanceOverlay/bin/win-x64/ResourceManager.VulkanPerformanceOverlayLayer.dll" };
            if (includeInstaller) paths.Add("native/InstallWebView2Runtime.ps1");
            paths.Add("desktop/" + ResourceManager.Shared.Packages.ProductDesktopLayout.ResidentExecutableName);
            if (includeDevelopmentScript) paths.Add("native/Development.ps1");
            foreach (var relative in paths)
            {
                var path = Path.Combine(root, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllTextAsync(path, "fixture");
            }
            await File.WriteAllTextAsync(
                Path.Combine(root, "src/Core/Native/PerformanceOverlay/ResourceManager.VulkanPerformanceOverlayLayer.json"),
                "{\"layer\":{\"name\":\"VK_LAYER_RESOURCE_MANAGER_performance_overlay\",\"library_path\":\"ResourceManager.VulkanPerformanceOverlayLayer.dll\"}}");
            var start = new ProcessStartInfo("powershell.exe")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File",
                Path.Combine(RepositoryRoot(), "scripts", "release", "New-ResourceManagerFinalImage.ps1"),
                "-BackendDirectory", Path.Combine(root, "backend"), "-NativeUiDirectory", Path.Combine(root, "native"),
                "-DesktopDirectory", Path.Combine(root, "desktop"),
                "-LauncherDirectory", Path.Combine(root, "launcher"),
                "-SessionAgentDirectory", Path.Combine(root, "session-agent"), "-FrontendModuleSourceRoot", root,
                "-OutputDirectory", Path.Combine(root, "image") })
                start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)); }
            catch (TimeoutException) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); throw; }
            var diagnostics = $"stdout={await output}; stderr={await error}";
            Assert.True(process.ExitCode == expectedExit, diagnostics);
            if (!includeInstaller)
            {
                Assert.Contains("Published input is incomplete", diagnostics);
                Assert.Contains("InstallWebView2Runtime.ps1", diagnostics);
            }
            else if (includeDevelopmentScript)
            {
                Assert.Contains("forbidden development artifact", diagnostics);
                Assert.Contains("ResourceManagerNativeUi/Development.ps1", diagnostics);
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static string RepositoryRoot([CallerFilePath] string file = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, "..", ".."));
}
