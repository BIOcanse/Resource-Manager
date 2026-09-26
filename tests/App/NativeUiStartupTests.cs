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
        Assert.Contains("private readonly WebView2FrontendHost", mainForm, StringComparison.Ordinal);
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
        Assert.Contains("documentPolicy.IsTrustedApiRequest(args.Request.Uri)", webViewHost, StringComparison.Ordinal);

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

    [Fact]
    public void Parse_BackgroundStartupEnsuresExistingInstanceWithoutShowingWindow()
    {
        var options = NativeUiLaunchOptions.Parse(["--background-startup"]);

        Assert.True(options.StartInBackground);
        Assert.Equal("ensure-running", options.SingleInstanceCommand);
    }

    [Fact]
    public void Parse_PassiveSecondaryShowUsesDedicatedSingleInstanceCommand()
    {
        var options = NativeUiLaunchOptions.Parse(["--show-existing-passive-secondary"]);

        Assert.False(options.StartInBackground);
        Assert.Equal("show-passive-secondary", options.SingleInstanceCommand);
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
