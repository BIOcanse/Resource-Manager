using ResourceManager.App.Application.SoftwareIdentity;
using ResourceManager.App.Infrastructure.SoftwareIdentity;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Domain.ProcessAttribution;
using ResourceManager.App.Domain.Software;
using ResourceManager.App.Domain.SoftwareIdentity;
using ResourceManager.App.Domain.Optimization.Scoring;

namespace Resource_Manager_APP.Tests;

public sealed partial class RuntimeProcessAttributionPipelineTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RootTieDoesNotBlockExactCatalogExecutableOrProduct(bool product)
    {
        var catalog = SoftwareIdentityCatalogTestData.Create(new SoftwareIdentityCatalogEntry(
            "game-counter-strike-2", "Counter-Strike 2", SoftwareKinds.Game, "test",
            ExecutableNames: product ? [] : ["cs2.exe"],
            ProductNames: product ? ["Counter-Strike 2"] : []));
        using var pipeline = CreatePipeline([
            CreateSoftware("installed:first", "First unrelated label", [@"D:\Shared"]),
            CreateSoftware("installed:second", "Second unrelated label", [@"D:\Shared"])
        ], identityCatalog: catalog);
        var result = pipeline.Match(new RuntimeProcessIdentity(78780, null, "cs2",
            @"D:\Shared\cs2.exe", false, ProductName: product ? "Counter-Strike 2" : null));
        Assert.Equal("catalog:game-counter-strike-2", result.Id);
        Assert.Equal(SoftwareKinds.Game, result.Kind);
        Assert.Equal(95d, OptimizationRuntimeScoringDefaults.BaseScoreForKind(result.Kind));
    }

    [Fact]
    public void RootTieWithoutExactEvidenceKeepsUnattributedBaseScore()
    {
        using var pipeline = CreatePipeline([
            CreateSoftware("installed:first", "First unrelated label", [@"D:\Shared"]),
            CreateSoftware("installed:second", "Second unrelated label", [@"D:\Shared"])
        ]);
        var result = pipeline.Match(new RuntimeProcessIdentity(78781, null, "unknown",
            @"D:\Shared\unknown.exe", false));
        Assert.Equal(RuntimeAttributionIds.Unattributed, result.Id);
        Assert.Equal(SoftwareKinds.Unattributed, result.Kind);
        Assert.Equal(30d, OptimizationRuntimeScoringDefaults.BaseScoreForKind(result.Kind));
    }
    [Fact]
    public void RootTieDoesNotLetAnotherRootGuessOwnership()
    {
        using var pipeline = CreatePipeline([
            CreateSoftware("installed:first", "First unrelated label", [@"D:\Shared"]),
            CreateSoftware("installed:second", "Second unrelated label", [@"D:\Shared"])
        ], rootResolver: new MatchedRootIdentityResolver(new RuntimeSoftwareAttribution(
            "runtime-root:shared", "Shared", SoftwareKinds.RuntimeRoot, SoftwareDisplayKinds.General, [@"D:\Shared"])));
        var result = pipeline.Match(new RuntimeProcessIdentity(78782, null, "unknown", @"D:\Shared\unknown.exe", false));
        Assert.Equal(RuntimeAttributionIds.Unattributed, result.Id);
        Assert.Equal(30d, OptimizationRuntimeScoringDefaults.BaseScoreForKind(result.Kind));
    }

    [Fact]
    public void StrongInstalledIdentityTieStillBlocksExactCatalogExecutable()
    {
        var catalog = SoftwareIdentityCatalogTestData.Create(new SoftwareIdentityCatalogEntry(
            "game-counter-strike-2", "Counter-Strike 2", SoftwareKinds.Game, "test", ExecutableNames: ["cs2.exe"]));
        using var pipeline = CreatePipeline([
            CreateSoftware("installed:first", "Counter-Strike 2", [@"D:\Shared"]),
            CreateSoftware("installed:second", "Counter-Strike 2", [@"D:\Shared"])
        ], identityCatalog: catalog);
        var result = pipeline.Match(new RuntimeProcessIdentity(78783, null, "cs2", @"D:\Shared\cs2.exe", false,
            ProductName: "Counter-Strike 2"));
        Assert.Equal(RuntimeAttributionIds.Unattributed, result.Id);
    }
    [Fact]
    public void RootTieDoesNotBlockExactSteamAppIdObservation()
    {
        using var compiled = SoftwareIdentityCatalogTestData.Create(new SoftwareIdentityCatalogEntry(
            "game-counter-strike-2", "Counter-Strike 2", SoftwareKinds.Game, "test", SteamAppIds: [730]));
        // Process observations do not currently carry AppIDs. Supply the platform fact
        // at that boundary, after resolving it through the real native catalog matcher.
        var catalog = new SteamAppIdObservationCatalog(compiled);
        using var pipeline = CreatePipeline([
            CreateSoftware("installed:first", "First unrelated label", [@"D:\Shared"]),
            CreateSoftware("installed:second", "Second unrelated label", [@"D:\Shared"])
        ], identityCatalog: catalog);
        var result = pipeline.Match(new RuntimeProcessIdentity(78784, null, "unlisted",
            @"D:\Shared\unlisted.exe", false));
        Assert.Equal("catalog:game-counter-strike-2", result.Id);
        Assert.Equal(95d, OptimizationRuntimeScoringDefaults.BaseScoreForKind(result.Kind));
    }

    private sealed class SteamAppIdObservationCatalog(SoftwareIdentityCatalogCompiler catalog) :
        ISoftwareIdentityCatalog, INativeSoftwareIdentityProcessObserver
    {
        public string Version => catalog.Version;
        public IReadOnlyList<SoftwareIdentityCatalogEntry> Entries => catalog.Entries;
        public SoftwareIdentityCatalogEntry? MatchInstalledSoftware(InstalledSoftwareEntry software)
            => catalog.MatchInstalledSoftware(software);
        public SoftwareIdentityCatalogEntry? MatchProcess(RuntimeProcessIdentity process) => ObserveProcess(process).Entry;
        public PortableSoftwareIdentityMatch? MatchPortableProcess(RuntimeProcessIdentity process) => null;
        public NativeCatalogProcessObservation ObserveProcess(RuntimeProcessIdentity process)
        {
            var entry = MatchInstalledSoftware(new InstalledSoftwareEntry("steam-app-730", "Unknown installed label",
                null, null, null, [], null, @"HKLM\Software\Steam App 730"));
            return new(NativeCatalogProcessObservationStatus.Matched, Assert.IsType<SoftwareIdentityCatalogEntry>(entry),
                (ulong)NativeSoftwareIdentityEvidence.SteamAppId);
        }
    }
}