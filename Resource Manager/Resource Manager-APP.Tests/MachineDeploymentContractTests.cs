using System.Diagnostics;
using System.Text.Json;
using Microsoft.Win32;

namespace Resource_Manager_APP.Tests;

public sealed class MachineDeploymentContractTests
{
    [Fact]
    public async Task PlanOnlyFixtureUsesScmAndHasNoMachineSideEffects()
    {
        var repository = FindRepositoryRoot();
        var installer = Path.Combine(repository, "scripts", "Deploy-Machine.ps1");
        Assert.True(File.Exists(installer));
        var root = Path.Combine(Path.GetTempPath(), $"ResourceManager.MachineDeployment.Contract.{Guid.NewGuid():N}");
        var image = Path.Combine(root, "image");
        var install = Path.Combine(root, "install");
        var registrationBefore = ReadMachineInstallRoot();
        try
        {
            Directory.CreateDirectory(Path.Combine(image, "ResourceManager"));
            Directory.CreateDirectory(Path.Combine(image, "ResourceManagerNativeUi"));
            File.WriteAllBytes(Path.Combine(image, "ResourceManager", "ResourceManager.exe"), [1, 2, 3]);
            File.WriteAllBytes(Path.Combine(image, "ResourceManagerNativeUi", "ResourceManager.NativeUi.exe"), [4, 5, 6]);
            File.WriteAllText(Path.Combine(image, "resource-manager-final-image.manifest.json"), "{}");

            var startInfo = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (var argument in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", installer,
                         "-PlanOnly", "-SkipPublish", "-InstallRoot", install, "-SourceImageRoot", image })
                startInfo.ArgumentList.Add(argument);
            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Could not start the plan-only deployment fixture.");
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await process.WaitForExitAsync(timeout.Token);
            var output = await outputTask;
            var error = await errorTask;
            Assert.True(process.ExitCode == 0, $"Plan-only failed: stdout={output} stderr={error}");

            using var document = JsonDocument.Parse(output);
            var plan = document.RootElement;
            Assert.Equal("resource-manager-machine-deployment-v1", plan.GetProperty("Contract").GetString());
            Assert.Equal("ResourceManager.Service", plan.GetProperty("ServiceName").GetString());
            Assert.Equal(Path.GetFullPath(install), plan.GetProperty("InstallRoot").GetString());
            Assert.Equal(Path.Combine(install, "Current", "ResourceManager", "ResourceManager.exe"),
                plan.GetProperty("BackendPath").GetString());
            Assert.StartsWith("Registry::HKEY_LOCAL_MACHINE\\", plan.GetProperty("MachineRegistryRoot").GetString());
            Assert.False(Directory.Exists(install));
            Assert.Equal(registrationBefore, ReadMachineInstallRoot());
        }
        finally
        {
            var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            Assert.StartsWith(temp, Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static string? ReadMachineInstallRoot()
    {
        using var registry = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = registry.OpenSubKey(@"Software\ResourceManager");
        return key?.GetValue("InstallRoot") as string;
    }

    private static string FindRepositoryRoot(
        [System.Runtime.CompilerServices.CallerFilePath] string sourceFilePath = "")
    {
        var repositoryRoot = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFilePath)!, "..", ".."));
        return File.Exists(Path.Combine(repositoryRoot, "scripts", "Deploy-Machine.ps1"))
            ? repositoryRoot
            : throw new DirectoryNotFoundException("Could not locate the deployment script from the test source path.");
    }
}
