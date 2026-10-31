using ResourceManager.NativeUi;

namespace Resource_Manager_APP.Tests;

public sealed class NativeUiStartupTests
{
    [Fact]
    public void ProductionFrontendUsesOnlyTheStandardWebView2Host()
    {
        var appRoot = FindAppRoot();
        var nativeUiRoot = FindNativeUiRoot(appRoot);
        var project = File.ReadAllText(Path.Combine(
            nativeUiRoot,
            "ResourceManager.NativeUi.csproj"));
        var mainForm = File.ReadAllText(Path.Combine(nativeUiRoot, "Shell", "MainForm.cs"));
        var webViewHost = File.ReadAllText(Path.Combine(
            nativeUiRoot,
            "WebView",
            "WebView2FrontendHost.cs"));
        var webViewFlow = File.ReadAllText(Path.Combine(
            nativeUiRoot,
            "Shell",
            "MainForm.WebView.cs"));

        Assert.Contains("Microsoft.Web.WebView2", project, StringComparison.Ordinal);
        Assert.DoesNotContain("Vortice", project, StringComparison.Ordinal);
        Assert.Contains("private readonly IFrontendHost", mainForm, StringComparison.Ordinal);
        Assert.Contains("new WebView2FrontendHost", mainForm, StringComparison.Ordinal);
        Assert.DoesNotContain("FrontendHostFactory", mainForm, StringComparison.Ordinal);
        Assert.Contains("Microsoft.Web.WebView2.WinForms.WebView2", webViewHost, StringComparison.Ordinal);
        Assert.DoesNotContain("CompositionController", webViewHost, StringComparison.Ordinal);
        Assert.DoesNotContain("CaptureLoadedAssetSnapshotAsync", webViewHost, StringComparison.Ordinal);
        Assert.DoesNotContain("FrontendAssetIdentity", webViewFlow, StringComparison.Ordinal);
        Assert.DoesNotContain("frontend-build.json", webViewFlow, StringComparison.Ordinal);
        Assert.DoesNotContain("FrontendReadinessEvidenceStore", mainForm, StringComparison.Ordinal);
        Assert.Contains("args.IsTrustedDocument", webViewFlow, StringComparison.Ordinal);
        Assert.Contains("ShowFrontendReady();", webViewFlow, StringComparison.Ordinal);
        Assert.Contains("NavigationStarting += HandleNavigationStarting", webViewHost, StringComparison.Ordinal);
        Assert.Contains("WebMessageReceived += HandleWebMessageReceived", webViewHost, StringComparison.Ordinal);
        Assert.Contains("documentPolicy.IsTrustedDocument(args.Source)", webViewHost, StringComparison.Ordinal);
        Assert.Contains("documentPolicy.IsTrustedResourceRequest(args.Request.Uri)", webViewHost, StringComparison.Ordinal);

        foreach (var relativePath in new[]
                 {
                     Path.Combine("WebView", "CompositionWebViewHost.cs"),
                     Path.Combine("Runtime", "FrontendHostFactory.cs"),
                     Path.Combine("Runtime", "IFrontendHost.cs"),
                     Path.Combine("Runtime", "IFrontendSurface.cs")
                 })
        {
            Assert.False(File.Exists(Path.Combine(nativeUiRoot, relativePath)));
        }

        foreach (var relativePath in new[]
                 {
                     Path.Combine("Rendering", "FinalOutput"),
                     Path.Combine("Runtime", "RmWebRuntime"),
                     Path.Combine("Runtime", "UnboundWebView")
                 })
        {
            var directory = Path.Combine(nativeUiRoot, relativePath);
            Assert.False(
                Directory.Exists(directory)
                && Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Any(),
                $"Retired frontend source remains under {relativePath}.");
        }
    }

    [Theory]
    [InlineData("--background-startup", "ensure-running")]
    [InlineData("--show-existing", "show")]
    [InlineData("--show-existing-passive", "show-passive")]
    [InlineData("--show-existing-passive-secondary", "show-passive-secondary")]
    [InlineData("--exit-existing", "exit")]
    [InlineData("--SHOW-EXISTING", "show")]
    public void ParseCommand_MapsEachPersistedEntryToItsSingleInstanceCommand(string argument, string command)
    {
        Assert.Equal(command, NativeUiLaunchOptions.ParseCommand([argument]));
    }

    [Fact]
    public void ParseCommand_DirectStartAndTaskManagerDebuggerShow()
    {
        Assert.Equal("show", NativeUiLaunchOptions.ParseCommand([]));
        // The IFEO Debugger appends taskmgr's own path after its marker.
        Assert.Equal("show", NativeUiLaunchOptions.ParseCommand(
            ["--show-existing", "--resource-manager-task-manager-replacement", @"C:\Windows\System32\Taskmgr.exe"]));
    }

    [Fact]
    public void NativeUiDoesNotOwnMachineOrLogonStartupRegistration()
    {
        var nativeUiRoot = FindNativeUiRoot(FindAppRoot());
        var program = File.ReadAllText(Path.Combine(nativeUiRoot, "Program.cs"));
        var options = File.ReadAllText(Path.Combine(
            nativeUiRoot,
            "Configuration",
            "NativeUiLaunchOptions.cs"));

        Assert.DoesNotContain("UserLogonStartupTaskRegistration", program, StringComparison.Ordinal);
        Assert.DoesNotContain("register-user-logon-startup", options, StringComparison.Ordinal);
        Assert.DoesNotContain("unregister-user-logon-startup", options, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(
            nativeUiRoot,
            "SystemIntegration",
            "UserLogonStartupTaskRegistration.cs")));
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

    private static string FindNativeUiRoot(string appRoot) =>
        Path.GetFullPath(Path.Combine(appRoot, "..", "..", "src", "UI", "Core"));
}
