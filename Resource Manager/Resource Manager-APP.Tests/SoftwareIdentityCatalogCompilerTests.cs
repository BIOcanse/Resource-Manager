using ResourceManager.App.Domain.ProcessAttribution;
using ResourceManager.App.Domain.Software;
using ResourceManager.App.Domain.SoftwareIdentity;
using ResourceManager.App.Infrastructure.SoftwareIdentity;
using System.Text.Json;

namespace Resource_Manager_APP.Tests;

[Collection(SoftwareIdentityCatalogProcessStateCollection.Name)]
public sealed class SoftwareIdentityCatalogCompilerTests
{
    [Fact]
    public void BundledCatalog_CompilesAndContainsPopularCommonAndLocalCoverage()
    {
        var path = Path.Combine(
            AppContext.BaseDirectory,
            "Infrastructure",
            "Resources",
            "SoftwareIdentity",
            "software-identities.v1.json");
        using var stream = File.OpenRead(path);
        var document = JsonSerializer.Deserialize<SoftwareIdentityCatalogDocument>(stream, JsonSerializerOptions.Web);

        var catalog = new SoftwareIdentityCatalogCompiler(
            Assert.IsType<SoftwareIdentityCatalogDocument>(document),
            HostManagerTestPlanFactory.CreatePlan().SoftwareIdentityCatalog);

        Assert.True(catalog.Entries.Count >= 120);
        Assert.Equal(SoftwareKinds.Game, catalog.Entries.Single(entry => entry.Id == "game-counter-strike-2").Kind);
        Assert.Equal(SoftwareKinds.HighPerformance, catalog.Entries.Single(entry => entry.Id == "pro-blender").Kind);
        Assert.Equal(SoftwareKinds.Other, catalog.Entries.Single(entry => entry.Id == "app-google-chrome").Kind);
        Assert.Contains(catalog.Entries, entry => entry.Id == "game-skyrim-special-edition");
        Assert.Contains(catalog.Entries, entry => entry.Id == "app-xunlei");
        Assert.Contains(catalog.Entries, entry =>
            entry.Id == "app-notebook-fan-control"
            && entry.InstalledNames!.Contains("NoteBook FanControl"));
    }

    [Fact]
    public void MatchInstalledSoftware_UsesExactSteamAppIdAndInstalledName()
    {
        var catalog = SoftwareIdentityCatalogTestData.Create(CreateSkyrimEntry());

        var steamMatch = catalog.MatchInstalledSoftware(new InstalledSoftwareEntry(
            "windows-installed:steam-app-489830",
            "Unknown registry display name",
            null,
            "Valve",
            null,
            [],
            null,
            @"HKLM\Software\Steam App 489830"));
        var nameMatch = catalog.MatchInstalledSoftware(new InstalledSoftwareEntry(
            "windows-installed:skyrim",
            "The Elder Scrolls V: Skyrim Special Edition",
            null,
            "Bethesda",
            null,
            [],
            null,
            @"HKLM\Software\Skyrim"));

        Assert.Equal("game-skyrim-special-edition", steamMatch?.Id);
        Assert.Equal("game-skyrim-special-edition", nameMatch?.Id);
    }

    [Fact]
    public void MatchProcess_UsesExactExecutableOrProductEvidenceWithoutFuzzyPromotion()
    {
        var catalog = SoftwareIdentityCatalogTestData.Create(CreateSkyrimEntry());

        var executableMatch = catalog.MatchProcess(new RuntimeProcessIdentity(
            42,
            null,
            "SkyrimSE",
            @"F:\Portable\SkyrimSE.exe",
            false));
        var fuzzyName = catalog.MatchProcess(new RuntimeProcessIdentity(
            43,
            null,
            "Skyrim Special Edition Launcher",
            @"F:\Portable\Skyrim Special Edition Launcher.exe",
            false));

        Assert.Equal("game-skyrim-special-edition", executableMatch?.Id);
        Assert.Null(fuzzyName);
    }

    [Fact]
    public void MatchProcess_ReturnsNullWhenIndependentEvidenceConflicts()
    {
        var catalog = SoftwareIdentityCatalogTestData.Create(
            new SoftwareIdentityCatalogEntry(
                "game-one",
                "Game One",
                SoftwareKinds.Game,
                "test",
                ExecutableNames: ["ExactGame.exe"]),
            new SoftwareIdentityCatalogEntry(
                "pro-two",
                "Pro Two",
                SoftwareKinds.HighPerformance,
                "test",
                ProductNames: ["Exact Product"]));

        var match = catalog.MatchProcess(new RuntimeProcessIdentity(
            44,
            null,
            "ExactGame",
            @"D:\Apps\ExactGame.exe",
            false,
            ProductName: "Exact Product"));

        Assert.Null(match);
    }

    [Fact]
    public void MatchPortableProcess_GradesExecutableOnlyAndProductConfirmedEvidence()
    {
        var catalog = SoftwareIdentityCatalogTestData.Create(CreateSkyrimEntry());

        var candidate = catalog.MatchPortableProcess(new RuntimeProcessIdentity(
            45,
            null,
            "SkyrimSE",
            @"F:\Portable\SkyrimSE.exe",
            false));
        var confirmed = catalog.MatchPortableProcess(new RuntimeProcessIdentity(
            46,
            null,
            "SkyrimSE",
            @"F:\Portable\SkyrimSE.exe",
            false,
            ProductName: "The Elder Scrolls V: Skyrim Special Edition"));

        Assert.Equal(PortableSoftwareIdentityConfidence.Candidate, candidate?.Confidence);
        Assert.Equal(PortableSoftwareIdentityConfidence.Confirmed, confirmed?.Confidence);
    }

    [Fact]
    public void MatchPortableProcess_RejectsConflictingProductMetadata()
    {
        var catalog = SoftwareIdentityCatalogTestData.Create(CreateSkyrimEntry());

        var match = catalog.MatchPortableProcess(new RuntimeProcessIdentity(
            47,
            null,
            "SkyrimSE",
            @"F:\Portable\SkyrimSE.exe",
            false,
            ProductName: "Unrelated Product"));

        Assert.Null(match);
    }

    [Theory]
    [InlineData("game.exe")]
    [InlineData("launcher.exe")]
    [InlineData("python.exe")]
    public void Constructor_RejectsAmbiguousExecutableAliases(string executableName)
    {
        var document = new SoftwareIdentityCatalogDocument(
            "1.0.0",
            [new SoftwareIdentityCatalogEntry(
                "unsafe-entry",
                "Unsafe",
                SoftwareKinds.Game,
                "test",
                ExecutableNames: [executableName])]);

        Assert.Throws<InvalidDataException>(() => new SoftwareIdentityCatalogCompiler(
            document,
            HostManagerTestPlanFactory.CreatePlan().SoftwareIdentityCatalog));
    }

    [Fact]
    public void Constructor_RejectsAliasesSharedByDifferentEntries()
    {
        var document = new SoftwareIdentityCatalogDocument(
            "1.0.0",
            [
                new SoftwareIdentityCatalogEntry(
                    "one",
                    "One",
                    SoftwareKinds.Game,
                    "test",
                    InstalledNames: ["Exact Name"]),
                new SoftwareIdentityCatalogEntry(
                    "two",
                    "Two",
                    SoftwareKinds.HighPerformance,
                    "test",
                    InstalledNames: ["Exact Name"])
            ]);

        Assert.Throws<InvalidDataException>(() => new SoftwareIdentityCatalogCompiler(
            document,
            HostManagerTestPlanFactory.CreatePlan().SoftwareIdentityCatalog));
    }

    private static SoftwareIdentityCatalogEntry CreateSkyrimEntry()
    {
        return new SoftwareIdentityCatalogEntry(
            "game-skyrim-special-edition",
            "The Elder Scrolls V: Skyrim Special Edition",
            SoftwareKinds.Game,
            "test",
            SteamAppIds: [489830],
            InstalledNames: ["The Elder Scrolls V: Skyrim Special Edition"],
            ExecutableNames: ["SkyrimSE.exe"],
            ProductNames: ["The Elder Scrolls V: Skyrim Special Edition"]);
    }
}
