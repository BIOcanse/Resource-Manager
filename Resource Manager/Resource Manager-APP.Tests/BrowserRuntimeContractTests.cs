using ResourceManager.App.Application.Components;
using ResourceManager.App.Application.Dependencies;

namespace Resource_Manager_APP.Tests;

public sealed class BrowserRuntimeContractTests
{
    [Fact]
    public void SharedRuntimeComponent_UsesOfficialEvergreenBootstrapper()
    {
        var dependency = OptionalDependencyCatalog.Find("shared-webview2-runtime");
        var component = ComponentCatalog.Find("shared-webview2-runtime");

        Assert.NotNull(dependency);
        Assert.Equal(
            "https://go.microsoft.com/fwlink/p/?LinkId=2124703",
            dependency.DownloadUrl);
        Assert.Equal("MicrosoftEdgeWebview2Setup.exe", dependency.InstallerFileName);
        Assert.True(dependency.RequiresExternalTermsAcknowledgement);
        Assert.True(dependency.RequiresElevation);

        Assert.NotNull(component);
        Assert.Contains(component.Capabilities, capability =>
            capability.Id.EndsWith(".webview2-runtime", StringComparison.Ordinal));
    }
}
