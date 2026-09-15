using ResourceManager.App.Domain.ResourceTable;
using ResourceManager.App.Application.SoftwareDiscovery;
using ResourceManager.App.Domain.ProcessAttribution;
using ResourceManager.App.Domain.Software;

namespace Resource_Manager_APP.Tests;

public sealed class PortableSoftwareObservationFactoryTests
{
    [Fact]
    public void TryCreate_AcceptsCatalogAttributionWithExecutablePath()
    {
        var process = CreateProcess(@"F:\Portable\Skyrim\SkyrimSE.exe");
        var attribution = new RuntimeSoftwareAttribution(
            "catalog:game-skyrim-special-edition",
            "The Elder Scrolls V: Skyrim Special Edition",
            SoftwareKinds.Game,
            SoftwareDisplayKinds.Game,
            [@"F:\Portable\Skyrim"]);

        var observation = PortableSoftwareObservationFactory.TryCreate(process, attribution);

        Assert.NotNull(observation);
        Assert.Equal("game-skyrim-special-edition", observation.CatalogEntryId);
        Assert.Equal(Path.GetFullPath(@"F:\Portable\Skyrim\SkyrimSE.exe"), observation.ExecutablePath);
        Assert.Equal(Path.GetFullPath(@"F:\Portable\Skyrim"), observation.SuggestedRootPath);
        Assert.False(observation.IdentityConfirmed);
        Assert.False(observation.RootPathConfirmed);
    }

    [Theory]
    [InlineData("windows-installed:skyrim", @"F:\Portable\Skyrim\SkyrimSE.exe")]
    [InlineData("catalog:game-skyrim-special-edition", null)]
    public void TryCreate_RejectsNonCatalogOrUnlocatedAttribution(string softwareId, string? executablePath)
    {
        var process = CreateProcess(executablePath);
        var attribution = new RuntimeSoftwareAttribution(
            softwareId,
            "Skyrim",
            SoftwareKinds.Game,
            SoftwareDisplayKinds.Game,
            []);

        Assert.Null(PortableSoftwareObservationFactory.TryCreate(process, attribution));
    }

    private static RuntimeProcessIdentity CreateProcess(string? executablePath)
    {
        return new RuntimeProcessIdentity(
            42,
            null,
            "SkyrimSE",
            executablePath,
            false,
            null,
            null,
            null,
            null,
            null,
            null);
    }
}
