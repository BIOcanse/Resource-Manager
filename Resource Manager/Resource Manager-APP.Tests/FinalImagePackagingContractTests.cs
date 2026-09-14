using System.Diagnostics;
using System.Text.Json;

namespace Resource_Manager_APP.Tests;

public sealed class FinalImagePackagingContractTests
{
    [Fact]
    public async Task FixtureBuildsHashClosedCombinedImageAndRejectsTampering()
    {
        var scriptPath = Path.Combine(
            FindRepositoryRoot(),
            "scripts",
            "Test-ResourceManagerFinalImagePackaging.ps1");
        Assert.True(File.Exists(scriptPath), $"Packaging fixture not found: {scriptPath}");

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
            ?? throw new InvalidOperationException("Could not start the packaging fixture.");
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
            $"Packaging fixture failed with {process.ExitCode}. stdout={output} stderr={error}");
        using var document = JsonDocument.Parse(output);
        var root = document.RootElement;
        Assert.Equal(
            "resource-manager-final-image-packaging-test-v1",
            root.GetProperty("Contract").GetString());
        Assert.Equal(6, root.GetProperty("PayloadFileCount").GetInt32());
        Assert.True(root.GetProperty("MissingHelperRejected").GetBoolean());
        Assert.True(root.GetProperty("MissingInputHelperRejected").GetBoolean());
        Assert.True(root.GetProperty("ManifestHelperOmissionRejected").GetBoolean());
        Assert.True(root.GetProperty("HelperTamperRejected").GetBoolean());
        Assert.True(root.GetProperty("MissingPreparationRejected").GetBoolean());
        Assert.True(root.GetProperty("MissingInputPreparationRejected").GetBoolean());
        Assert.True(root.GetProperty("ManifestPreparationOmissionRejected").GetBoolean());
        Assert.True(root.GetProperty("PreparationTamperRejected").GetBoolean());
        Assert.True(root.GetProperty("MissingLauncherRejected").GetBoolean());
        Assert.True(root.GetProperty("TamperRejected").GetBoolean());
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
