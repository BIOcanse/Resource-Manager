using System.Diagnostics;
using System.Text.Json;

namespace Resource_Manager_APP.Tests;

public sealed class MachineDeploymentContractTests
{
    [Fact]
    public async Task PlanOnlyFixtureUsesScmAndHasNoMachineSideEffects()
    {
        var scriptPath = Path.Combine(
            FindRepositoryRoot(),
            "scripts",
            "Test-ResourceManagerMachineDeployment.ps1");
        Assert.True(File.Exists(scriptPath), $"Deployment fixture not found: {scriptPath}");

        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(scriptPath);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start deployment fixture.");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
            throw;
        }

        var output = await outputTask;
        var error = await errorTask;
        Assert.True(
            process.ExitCode == 0,
            $"Deployment fixture failed with {process.ExitCode}. stdout={output} stderr={error}");
        using var document = JsonDocument.Parse(output);
        var root = document.RootElement;
        Assert.Equal(
            "resource-manager-machine-deployment-contract-test-v1",
            root.GetProperty("Contract").GetString());
        Assert.Equal("ResourceManager.Service", root.GetProperty("ServiceName").GetString());
        Assert.False(root.GetProperty("PlanOnlyCreatedInstallRoot").GetBoolean());
        Assert.True(root.GetProperty("MachineRegistry").GetBoolean());
        Assert.True(root.GetProperty("LegacyScheduledTaskAbsent").GetBoolean());
        Assert.True(root.GetProperty("OwnershipMarker").GetBoolean());
        Assert.True(root.GetProperty("TamperedMarkerRejected").GetBoolean());
        Assert.True(root.GetProperty("RegistrationMismatchRejected").GetBoolean());
        Assert.True(root.GetProperty("ForeignRootRejected").GetBoolean());
        Assert.True(root.GetProperty("AlternateRootConflictRejected").GetBoolean());
        Assert.True(root.GetProperty("DeploymentOperationLock").GetBoolean());
        Assert.True(root.GetProperty("RollbackModes").GetBoolean());
        Assert.True(root.GetProperty("PostCommitBackupCleanup").GetBoolean());
        Assert.True(root.GetProperty("InteractiveUiVisible").GetBoolean());
        Assert.True(root.GetProperty("Passed").GetBoolean());
    }

    private static string FindRepositoryRoot(
        [System.Runtime.CompilerServices.CallerFilePath] string sourceFilePath = "")
    {
        var sourceDirectory = Path.GetDirectoryName(sourceFilePath)!;
        var repositoryRoot = Path.GetFullPath(Path.Combine(
            sourceDirectory,
            "..",
            ".."));
        return File.Exists(Path.Combine(
            repositoryRoot,
            "scripts",
            "Test-CurrentProductionContracts.ps1"))
            ? repositoryRoot
            : throw new DirectoryNotFoundException(
                "Could not locate the repository root from the test source path.");
    }
}
