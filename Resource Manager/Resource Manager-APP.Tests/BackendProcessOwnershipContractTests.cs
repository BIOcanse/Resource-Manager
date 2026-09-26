namespace Resource_Manager_APP.Tests;

public sealed class BackendProcessOwnershipContractTests
{
    [Fact]
    public void NativeUiIsAttachOnlyAndServiceUsesTheWtsTokenChain()
    {
        var appRoot = FindAppRoot();
        var backendSession = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src", "UI", "Core",
            "BackendConnection",
            "BackendServiceSession.cs"));
        var serviceOwner = File.ReadAllText(Path.Combine(
            appRoot,
            "Infrastructure",
            "ServiceHosting",
            "NativeUiLaunchHostedService.cs"));
        var userSessionBroker = File.ReadAllText(Path.Combine(
            appRoot,
            "Infrastructure",
            "ServiceHosting",
            "WindowsInteractiveUserSessionBroker.cs"));
        var program = File.ReadAllText(Path.Combine(appRoot, "Program.cs"));

        Assert.Contains("EnsureConnectedAsync", backendSession, StringComparison.Ordinal);
        Assert.DoesNotContain("Process.Start", backendSession, StringComparison.Ordinal);
        Assert.DoesNotContain(".Kill(", backendSession, StringComparison.Ordinal);
        Assert.DoesNotContain("ownedProcess", backendSession, StringComparison.Ordinal);

        Assert.DoesNotContain("Process.Start", serviceOwner, StringComparison.Ordinal);
        Assert.DoesNotContain("StopApplication", serviceOwner, StringComparison.Ordinal);
        Assert.DoesNotContain(".Kill(", serviceOwner, StringComparison.Ordinal);
        Assert.Contains("NativeUiProcessPresence.Conflicting", serviceOwner, StringComparison.Ordinal);

        Assert.Contains("WTSQueryUserToken", userSessionBroker, StringComparison.Ordinal);
        Assert.Contains("DuplicateTokenEx", userSessionBroker, StringComparison.Ordinal);
        Assert.Contains("CreateEnvironmentBlock", userSessionBroker, StringComparison.Ordinal);
        Assert.Contains("CreateProcessAsUserW", userSessionBroker, StringComparison.Ordinal);
        Assert.Contains(@"winsta0\default", userSessionBroker, StringComparison.Ordinal);
        Assert.DoesNotContain("UseShellExecute", userSessionBroker, StringComparison.Ordinal);

        Assert.Contains("WindowsServiceHelpers.IsWindowsService", program, StringComparison.Ordinal);
        Assert.Contains("UseWindowsService", program, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(
            appRoot,
            "Infrastructure",
            "Shell",
            "NativeUiLaunchHostedService.cs")));
    }

    [Fact]
    public void DevelopmentRunnerOwnsItsBackendOutsideNativeUi()
    {
        var repositoryRoot = FindRepositoryRoot();
        var runner = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "scripts", "Run-Development.ps1"));
        var backendStart = runner.IndexOf(
            "启动外层脚本拥有的调试后端",
            StringComparison.Ordinal);
        var uiStart = runner.IndexOf(
            "启动只附着主窗口",
            StringComparison.Ordinal);

        Assert.True(backendStart >= 0);
        Assert.True(uiStart > backendStart);
        Assert.Contains("--no-native-ui", runner, StringComparison.Ordinal);
        Assert.Contains("--startup-profile", runner, StringComparison.Ordinal);
        Assert.Contains("StartupProfile", runner, StringComparison.Ordinal);
        Assert.Contains("Wait-DevelopmentBackendReady", runner, StringComparison.Ordinal);
        Assert.Contains("Test-LoopbackPortInUse", runner, StringComparison.Ordinal);
        Assert.Contains("loopback-api-token", runner, StringComparison.Ordinal);
        Assert.Contains("X-Resource-Manager-Token", runner, StringComparison.Ordinal);
        Assert.Contains("System.Net.Http.HttpClient", runner, StringComparison.Ordinal);
        Assert.Contains("System.Net.HttpStatusCode]::OK", runner, StringComparison.Ordinal);
        Assert.DoesNotContain("Test-FrontendReadinessEvidence", runner, StringComparison.Ordinal);
        Assert.DoesNotContain("frontend-ready.json", runner, StringComparison.Ordinal);
        Assert.DoesNotContain("frontend-build.json", runner, StringComparison.Ordinal);
        Assert.Contains("WebViewDebugPort", runner, StringComparison.Ordinal);
        Assert.Contains("RESOURCE_MANAGER_WEBVIEW_REMOTE_DEBUGGING_PORT", runner, StringComparison.Ordinal);
        Assert.Contains(
            "$env:RESOURCE_MANAGER_PACKAGE_ROOT = $SoftwareRoot",
            runner,
            StringComparison.Ordinal);
        Assert.Contains(
            "Remove-Item Env:RESOURCE_MANAGER_PACKAGE_ROOT",
            runner,
            StringComparison.Ordinal);
        Assert.Contains(
            "if (-not $NoSelfElevate -and -not (Test-IsElevated))",
            runner,
            StringComparison.Ordinal);
        Assert.Contains("-WindowStyle Hidden", runner, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "(Test-PathUnderRoot $path) -or [string]::IsNullOrWhiteSpace($path)",
            runner,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Invoke-WebRequest", runner, StringComparison.Ordinal);
        Assert.DoesNotContain("$statusCode -eq 401", runner, StringComparison.Ordinal);
    }

    [Fact]
    public void DevelopmentStopUsesOwnedConsoleExitAndChecksEtwCleanup()
    {
        var runner = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "scripts", "Run-Development.ps1"));
        Assert.Contains("Invoke-DevelopmentBackendStop", runner, StringComparison.Ordinal);
        Assert.Contains(@"scripts\BackendLifetime\Stop-OwnedBackend.ps1", runner, StringComparison.Ordinal);
        Assert.Contains("CreationFileTimeUtc", runner, StringComparison.Ordinal);
        Assert.Contains("$receipt.stop.ExitCode -ne 0", runner, StringComparison.Ordinal);
        Assert.Contains("$null -ne $receipt.after", runner, StringComparison.Ordinal);
        Assert.Contains("-NativeUiOnly", runner, StringComparison.Ordinal);
        Assert.DoesNotContain("Stop-Process", runner, StringComparison.Ordinal);
        Assert.DoesNotContain("$backendProcess.Kill", runner, StringComparison.Ordinal);
    }

    private static string FindAppRoot(
        [System.Runtime.CompilerServices.CallerFilePath] string sourceFilePath = "")
    {
        var sourceDirectory = Path.GetDirectoryName(sourceFilePath)!;
        var appRoot = Path.GetFullPath(Path.Combine(
            sourceDirectory,
            "..",
            "Resource Manager-APP"));
        return File.Exists(Path.Combine(appRoot, "ResourceManager.App.csproj"))
            ? appRoot
            : throw new DirectoryNotFoundException(
                "Could not locate Resource Manager-APP from the test source path.");
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
            "scripts", "Run-Development.ps1"))
            ? repositoryRoot
            : throw new DirectoryNotFoundException(
                "Could not locate the repository root from the test source path.");
    }
}
