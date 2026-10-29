using System.Diagnostics;
using System.Text.Json;
using System.Xml.Linq;

namespace Resource_Manager_APP.Tests;

public sealed class NativeUiApplicationManifestTests
{
    [Fact]
    public void SourceManifestRequiresAnElevatedAdministratorWithoutUiAccessOrAutoElevation()
    {
        var manifestPath = Path.Combine(FindNativeUiRoot(), "app.manifest");
        var document = XDocument.Load(manifestPath);
        var requestedLevel = Assert.Single(
            document.Descendants(),
            element => element.Name.LocalName == "requestedExecutionLevel");

        Assert.Equal("requireAdministrator", (string?)requestedLevel.Attribute("level"));
        Assert.Equal("false", (string?)requestedLevel.Attribute("uiAccess"));
        Assert.DoesNotContain(
            document.Descendants(),
            element => element.Name.LocalName == "autoElevate");
    }

    [Fact]
    public async Task BuiltAppHostEmbedsRequireAdministratorManifest()
    {
        var appRoot = FindAppRoot();
#if DEBUG
        const string configuration = "Debug";
#else
        const string configuration = "Release";
#endif
        // A registered validation workset supplies its exact built apphost;
        // ordinary in-tree builds keep their existing output location.
        var executablePath = Environment.GetEnvironmentVariable("RESOURCE_MANAGER_NATIVE_UI_TEST_APPHOST") ?? Path.Combine(
            FindNativeUiRoot(),
            "bin",
            configuration,
            "net10.0-windows",
            "ResourceManager.NativeUi.exe");
        var scriptPath = Path.Combine(
            Path.GetFullPath(Path.Combine(appRoot, "..", "..")),
            "scripts",
            "validation",
            "Test-WindowsExecutableManifest.ps1");
        Assert.True(File.Exists(executablePath), $"Native UI apphost not found: {executablePath}");
        Assert.True(File.Exists(scriptPath), $"Manifest validator not found: {scriptPath}");

        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in new[]
        {
            "-NoProfile",
            "-ExecutionPolicy",
            "Bypass",
            "-File",
            scriptPath,
            "-ExecutablePath",
            executablePath,
            "-ExpectedExecutionLevel",
            "requireAdministrator"
        })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start the manifest validator.");
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
            $"Manifest validator failed with {process.ExitCode}. stdout={output} stderr={error}");
        using var result = JsonDocument.Parse(output);
        Assert.Equal(
            "requireAdministrator",
            result.RootElement.GetProperty("RequestedExecutionLevel").GetString());
        Assert.True(result.RootElement.GetProperty("Valid").GetBoolean());
    }

    private static string FindAppRoot(
        [System.Runtime.CompilerServices.CallerFilePath] string sourceFilePath = "")
    {
        var sourceDirectory = Path.GetDirectoryName(sourceFilePath)!;
        var appRoot = Path.GetFullPath(Path.Combine(
            sourceDirectory,
            "..",
            "..",
            "src",
            "Core"));
        return File.Exists(Path.Combine(appRoot, "ResourceManager.App.csproj"))
            ? appRoot
            : throw new DirectoryNotFoundException(
                "Could not locate Resource Manager-APP from the test source path.");
    }

    private static string FindNativeUiRoot() =>
        Path.GetFullPath(Path.Combine(FindAppRoot(), "..", "..", "src", "UI", "Core"));
}
