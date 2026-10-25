using System.Diagnostics;
using System.Text.Json;
using Microsoft.Win32;

namespace Resource_Manager_APP.Tests;

public sealed class MachineDeploymentContractTests
{
    [Fact]
    public async Task BackendAdministrationAndServiceEnvironmentUseTheSameExplicitDataRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "rm-deployment-environment-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var backend = Path.Combine(root, "backend.cmd");
            File.WriteAllText(backend, "@echo off\r\necho %RESOURCE_MANAGER_PACKAGE_ROOT%\r\necho %~1\r\n");
            var module = Path.Combine(FindRepositoryRoot(), "scripts", "ResourceManager.MachineDeployment.ps1");
            var script = Path.Combine(root, "fixture.ps1");
            File.WriteAllText(script, """
                param([string]$Module,[string]$Backend,[string]$InstallRoot)
                $ErrorActionPreference='Stop'
                . $Module
                $plan=[pscustomobject]@{BackendPath=$Backend;InstallRoot=$InstallRoot;ServiceName='ResourceManager.Fixture'}
                $env:RESOURCE_MANAGER_PACKAGE_ROOT='previous-parent-root'
                $output=Invoke-ResourceManagerBackendAdministration -Plan $plan -Arguments @('--initialize-trusted-access')
                $lines=@($output.Trim() -split '\r?\n')
                function Test-ResourceManagerServiceExists { return $false }
                function Invoke-ResourceManagerNativeCommand { param($FilePath,$Arguments) return '' }
                function Get-ItemPropertyValue { return @('OTHER_SETTING=preserved','resource_manager_package_root=stale') }
                function New-ItemProperty {
                    param($LiteralPath,$Name,$Value,$PropertyType,[switch]$Force)
                    $script:registration=[pscustomobject]@{path=$LiteralPath;name=$Name;value=$Value;type=$PropertyType}
                }
                Set-ResourceManagerMachineServiceRegistration -Plan $plan
                [pscustomobject]@{childRoot=$lines[0];operation=$lines[1];restoredRoot=$env:RESOURCE_MANAGER_PACKAGE_ROOT;registration=$script:registration}|ConvertTo-Json -Compress
                """);
            var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script, "-Module", module, "-Backend", backend, "-InstallRoot", root })
                start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await process.WaitForExitAsync(timeout.Token);
            var output = await outputTask;
            Assert.True(process.ExitCode == 0, output + await errorTask);
            using var result = JsonDocument.Parse(output);
            Assert.Equal(root, result.RootElement.GetProperty("childRoot").GetString());
            Assert.Equal("--initialize-trusted-access", result.RootElement.GetProperty("operation").GetString());
            Assert.Equal("previous-parent-root", result.RootElement.GetProperty("restoredRoot").GetString());
            var registration = result.RootElement.GetProperty("registration");
            Assert.Equal(@"Registry::HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\ResourceManager.Fixture", registration.GetProperty("path").GetString());
            Assert.Equal("Environment", registration.GetProperty("name").GetString());
            Assert.Equal("MultiString", registration.GetProperty("type").GetString());
            Assert.Equal(new[] { "OTHER_SETTING=preserved", "RESOURCE_MANAGER_PACKAGE_ROOT=" + root },
                registration.GetProperty("value").EnumerateArray().Select(item => item.GetString()));
        }
        finally
        {
            File.Delete(Path.Combine(root, "fixture.ps1"));
            File.Delete(Path.Combine(root, "backend.cmd"));
            Directory.Delete(root);
        }
    }

    [Fact]
    public void LocalPublishAndDeploymentUseTheSameArtifactRoot()
    {
        var repository = FindRepositoryRoot();
        var installer = File.ReadAllText(Path.Combine(repository, "scripts", "Deploy-Machine.ps1"));
        var publisherPath = Path.Combine(repository, "scripts", "release", "Publish-ResourceManagerLocal.ps1");
        Assert.True(File.Exists(publisherPath));
        var publisher = File.ReadAllText(publisherPath);

        Assert.Contains(@"scripts\release\Publish-ResourceManagerLocal.ps1", installer, StringComparison.Ordinal);
        Assert.Contains(@"artifacts\local-publish\ResourceManagerFinal", installer, StringComparison.Ordinal);
        Assert.Contains(@"artifacts\local-publish", publisher, StringComparison.Ordinal);
        Assert.DoesNotContain(@"Resource Manager\Bin", installer, StringComparison.Ordinal);
    }

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
            Directory.CreateDirectory(Path.Combine(image, "ResourceManagerSessionAgent"));
            File.WriteAllBytes(Path.Combine(image, "ResourceManagerSessionAgent", "ResourceManager.SessionAgent.exe"), [7, 8, 9]);
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
            Assert.Equal(Path.Combine(image, "ResourceManagerSessionAgent", "ResourceManager.SessionAgent.exe"),
                plan.GetProperty("SourceSessionAgentPath").GetString());
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
