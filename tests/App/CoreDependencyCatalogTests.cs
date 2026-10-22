using ResourceManager.App.Application.Components;
using ResourceManager.App.Application.Dependencies;

namespace Resource_Manager_APP.Tests;

public sealed class CoreDependencyCatalogTests
{
    [Theory]
    [InlineData("notebook-fancontrol-provider")]
    [InlineData("msi-afterburner")]
    [InlineData("intel-pcm-provider")]
    [InlineData("windows-performance-toolkit")]
    [InlineData("latencymon")]
    [InlineData("amd-ryzen-master-monitoring-sdk")]
    public void RetiredWrappersCannotBeOfferedOrInstalled(string id)
    {
        Assert.Null(ComponentCatalog.Find(id));
        Assert.Null(OptionalDependencyCatalog.Find(id));
    }

    [Fact]
    public void CoreIsBundledAndOnlyItsSharedSignedDriverHasAnInstaller()
    {
        Assert.Null(OptionalDependencyCatalog.Find("librehardwaremonitor-provider"));
        Assert.True(ComponentCatalog.Find("librehardwaremonitor-provider")!.IsBundled);
        var driver = OptionalDependencyCatalog.Find("hardware-sensors-pawnio-provider")!;
        var existing = OptionalDependencyCatalog.Find("amd-smu-pawnio-provider")!;
        Assert.True(driver.RequiresElevation);
        Assert.Equal(existing.ReleaseSource!.Owner, driver.ReleaseSource!.Owner);
        Assert.Equal(existing.ReleaseSource.Repository, driver.ReleaseSource.Repository);
        Assert.Equal(existing.ReleaseSource.VerifiedTag, driver.ReleaseSource.VerifiedTag);
        Assert.Equal(existing.ReleaseSource.VerifiedAssetName, driver.ReleaseSource.VerifiedAssetName);
        Assert.Null(driver.PayloadSource); // Official core already embeds its signed modules.
        Assert.DoesNotContain(driver.InstalledProbeRelativePaths,
            path => path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
    }
}
