using ResourceManager.App.Domain.ProcessAttribution;
using ResourceManager.App.Domain.Software;
using ResourceManager.App.Domain.SoftwareIdentity;

namespace Resource_Manager_APP.Tests;

public sealed partial class RuntimeProcessAttributionPipelineTests
{
    [Fact]
    public void SteamLibraryFolderAloneDoesNotPromoteUnknownExecutablesToGames()
    {
        using var pipeline = CreatePipeline([CreateSoftware(
            "windows-installed:localmachine-registry32-software-microsoft-windows-currentversion-uninstall-steam", "Steam", [@"D:\Games\Steam"])],
            identityCatalog: SoftwareIdentityCatalogTestData.Create(new SoftwareIdentityCatalogEntry(
                "game-counter-strike-2", "Counter-Strike 2", SoftwareKinds.Game, "test", ExecutableNames: ["cs2.exe"])));
        var unknown = pipeline.Match(new(78776, null, "cs2-copy", @"D:\Games\Steam\steamapps\common\Fixture\cs2-copy.exe", false));
        Assert.Equal(RuntimeAttributionIds.Unattributed, unknown.Id);
        Assert.Equal(SoftwareKinds.Unattributed, unknown.Kind);
    }

    [Fact]
    public void SteamManifestGameRootOwnsGenericExecutablesAndItsHelpers()
    {
        const string root = @"D:\Games\Steam\steamapps\common\Counter-Strike Global Offensive";
        using var pipeline = CreatePipeline([
            CreateSoftware("windows-installed:localmachine-registry32-software-microsoft-windows-currentversion-uninstall-steam", "Steam", [@"D:\Games\Steam"]),
            CreateSoftware("steam-app-730", "Counter-Strike 2", [root]) with { Kind = SoftwareKinds.Game, SoftwareIdentityId = "game-counter-strike-2" }
        ]);
        var batch = pipeline.MatchBatch([
            new(78776, null, "game", root + @"\game\bin\win64\game.exe", false, StartKey: 10),
            new(78777, 78776, "helper", root + @"\game\bin\win64\helper.exe", false, StartKey: 20)
        ]);
        Assert.All(batch.SoftwareByProcessId.Values, value => { Assert.Equal("steam-app-730", value.Id); Assert.Equal(SoftwareKinds.Game, value.Kind); });
    }

    [Theory]
    [InlineData("cs2", @"D:\Games\Steam\steamapps\common\Counter-Strike Global Offensive\game\bin\win64\cs2.exe", "catalog:game-counter-strike-2")]
    [InlineData("steamwebhelper", @"D:\Games\Steam\bin\cef\cef.win64\steamwebhelper.exe", "windows-installed:localmachine-registry32-software-microsoft-windows-currentversion-uninstall-steam")]
    public void SteamSharedRootDoesNotHideExactGameOrClientHelper(string name, string path, string expected)
    {
        var catalog = SoftwareIdentityCatalogTestData.Create(new SoftwareIdentityCatalogEntry(
            "game-counter-strike-2", "Counter-Strike 2", SoftwareKinds.Game, "test", SteamAppIds: [730], ExecutableNames: ["cs2.exe"]));
        using var pipeline = CreatePipeline([
            CreateSoftware("windows-installed:localmachine-registry32-software-microsoft-windows-currentversion-uninstall-steam", "Steam", [@"D:\Games\Steam"]),
            CreateSoftware("windows-installed:localmachine-registry64-software-microsoft-windows-currentversion-uninstall-steam-app-2807960", "战地风云™ 6", [@"D:\Games\Steam"]) with { Kind = SoftwareKinds.Game },
            CreateSoftware("windows-installed:localmachine-registry64-software-microsoft-windows-currentversion-uninstall-steam-app-3132990", "黑神话：悟空 性能测试工具", [@"F:\SteamLibrary\steamapps\common\Black Myth Wukong Benchmark Tool", @"D:\Games\Steam"])
        ], identityCatalog: catalog);
        var result = pipeline.Match(new(78776, null, name, path, false, StartKey: 10));
        Assert.Equal(expected, result.Id);
        Assert.Equal(expected.StartsWith("catalog:") ? SoftwareKinds.Game : SoftwareKinds.Other, result.Kind);
    }
}
