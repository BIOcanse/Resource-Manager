using ResourceManager.App.Application.Software;
using ResourceManager.App.Domain.ProcessAttribution;
using ResourceManager.App.Domain.Software;
using ResourceManager.App.Infrastructure.SoftwareIssues;
using ResourceManager.App.Application.ProcessAttribution;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Infrastructure.ResourceBreakdown;
using ResourceManager.App.Domain.RuntimeSpecialization;

namespace Resource_Manager_APP.Tests;

[Collection(SoftwareIdentityCatalogProcessStateCollection.Name)]
public sealed class SoftwareRuntimeReminderTests
{
    [Fact]
    public async Task RuntimeIdentifiedGameAppearsInTheSoftwareList()
    {
        var runtime = new RuntimeSoftwareAttribution("runtime-game:fixture", "Game", SoftwareKinds.Game, SoftwareDisplayKinds.Game, [@"D:\Games\Game"]);
        var view = SoftwareRegistryViewTests.CreateRuntimeReminderView();
        Assert.Contains(await view.GetSoftwareAsync(null, default, [runtime]), item => item.Id == runtime.Id && item.Kind == SoftwareKinds.Game);
    }

    [Theory]
    [InlineData(SoftwareKinds.RuntimePackage)]
    [InlineData(SoftwareKinds.RuntimeProduct)]
    [InlineData(SoftwareKinds.RuntimeRoot)]
    public async Task RuntimeOnlySoftwareIsListedAndManualClassificationClearsTheReminder(string kind)
    {
        var runtime = new RuntimeSoftwareAttribution(kind == SoftwareKinds.RuntimeProduct
            ? "runtime-product:publisher-runtime-app" : "runtime:app", "Runtime App", kind, "other", []);
        var view = SoftwareRegistryViewTests.CreateRuntimeReminderView();
        var records = await view.GetSoftwareAsync(null, default, [runtime]);
        Assert.Equal(runtime.Name, Assert.Single(records, item => item.Id == runtime.Id).Name);
        var issues = new SoftwareIssueProjection([]);
        Assert.Equal(SoftwareIssueKinds.Unclassified, Assert.Single(Assert.Single(await issues.ProjectAsync(records, default),
            item => item.Id == runtime.Id).Issues!).Kind);
        foreach (var category in new[] { SoftwareKinds.Other, SoftwareKinds.Game, SoftwareKinds.HighPerformance })
        {
            var manual = new ManualSoftwareRecord("manual:app", "Chosen application name", category, [],
                runtime.Id, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
            view = SoftwareRegistryViewTests.CreateRuntimeReminderView([manual]);
            var classified = await view.GetSoftwareAsync(null, default, [runtime]);
            var record = Assert.Single(await issues.ProjectAsync(classified, default), item => item.Id == runtime.Id);
            Assert.Equal(category, record.Kind);
            Assert.Equal(runtime.Id, record.SourceSoftwareId);
            Assert.Empty(record.Issues!);

            // Use the registered view (the same input the production catalog gets),
            // without feeding observed runtime roots back into the known catalog.
            var registered = await view.GetSoftwareAsync(default);
            var resolver = new IdentityResolver(runtime);
            using var pipeline = new RuntimeProcessAttributionPipeline(registered, [], [],
                kind == SoftwareKinds.RuntimePackage ? resolver : NoIdentity.Instance,
                NoIdentity.Instance,
                kind == SoftwareKinds.RuntimeRoot ? resolver : NoIdentity.Instance,
                NoIdentity.Instance, SoftwareIdentityCatalogTestData.Empty, SoftwareIdentityCatalogTestData.Owner,
                new HashSet<string> { @"C:\Apps\Runtime\client.exe" });
            var attributed = pipeline.Match(new(1234, null, "client", @"C:\Apps\Runtime\client.exe", false,
                ProductName: kind == SoftwareKinds.RuntimeProduct ? "Runtime App" : null,
                CompanyName: kind == SoftwareKinds.RuntimeProduct ? "Publisher" : null));
            Assert.Equal(runtime.Id, attributed.Id);
            Assert.Equal(category, attributed.Kind);
            var fact = WindowsResourceBreakdownSampler.ProjectSchedulingAttribution(
                new SchedulingProcessFact(1234, 1, "client", null, "", "", "", "", 0,
                    SchedulingProcessMetricMask.None, 0, 0, 1, []), attributed, CompiledBaseScorePlan.Empty, null);
            Assert.Equal(category, fact.SoftwareKind);
            Assert.Equal(category == SoftwareKinds.Game ? 95d : category == SoftwareKinds.HighPerformance ? 85d : 35d,
                fact.BaseScore);
        }
    }

    [Fact]
    public async Task SameNameAndOverlappingRootDoNotHideRuntimeSoftware()
    {
        var runtime = new RuntimeSoftwareAttribution("runtime:app", "App", SoftwareKinds.RuntimeProduct, "other", [@"C:\Apps\App\Tool"]);
        var installed = new InstalledSoftwareEntry("installed:app", "App", null, null, @"C:\Apps\App",
            [@"C:\Apps\App"], null, @"HKLM\App");
        var view = SoftwareRegistryViewTests.CreateRuntimeReminderView(installed: [installed]);
        var records = await view.GetSoftwareAsync(null, default, [runtime]);
        Assert.Equal(2, records.Count(item => item.Id != RuntimeAttributionIds.ResourceManagerSelf));
        Assert.Equal(runtime.RootPaths, Assert.Single(records, item => item.Id == runtime.Id).RootPaths);
        Assert.Single(await view.RefreshSoftwareAsync(null, default, [runtime with { Id = installed.Id }]),
            item => item.Id != RuntimeAttributionIds.ResourceManagerSelf);

        var manual = new ManualSoftwareRecord("manual:launcher", "App", SoftwareKinds.Other,
            installed.RootPaths, installed.Id, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        view = SoftwareRegistryViewTests.CreateRuntimeReminderView([manual], [installed]);
        records = await view.GetSoftwareAsync(null, default, [runtime]);
        Assert.Equal(2, records.Count(item => item.Id != RuntimeAttributionIds.ResourceManagerSelf));
        Assert.Equal(SoftwareKinds.RuntimeProduct, Assert.Single(records, item => item.Id == runtime.Id).Kind);
    }

    [Fact]
    public async Task WindowsCategorySourceReachesTheSoftwareViewAndManualWins()
    {
        const string source = @"Windows GameConfigStore\Children\MatchedExeFullPath";
        var runtime = new RuntimeSoftwareAttribution("runtime:app", "App", SoftwareKinds.Game, "game", [], source);
        var fact = WindowsResourceBreakdownSampler.ProjectSchedulingAttribution(
            new SchedulingProcessFact(1234, 1, "client", null, "", "", "", "", 0,
                SchedulingProcessMetricMask.None, 0, 0, 1, []), runtime, CompiledBaseScorePlan.Empty, null);
        Assert.Equal(source, fact.SoftwareCategorySource);
        var view = SoftwareRegistryViewTests.CreateRuntimeReminderView();
        Assert.Equal(source, Assert.Single(await view.GetSoftwareAsync(null, default, [runtime]), item => item.Id == runtime.Id).CategorySource);
        var installed = new InstalledSoftwareEntry(runtime.Id, "App", null, null, null, [], null, @"HKLM\App");
        view = SoftwareRegistryViewTests.CreateRuntimeReminderView(installed: [installed]);
        Assert.Equal(source, Assert.Single(await view.RefreshSoftwareAsync(null, default, [runtime]), item => item.Id == runtime.Id).CategorySource);
        var manual = new ManualSoftwareRecord("manual:app", "App", SoftwareKinds.Other, [],
            runtime.Id, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        view = SoftwareRegistryViewTests.CreateRuntimeReminderView([manual], [installed]);
        var classified = Assert.Single(await view.GetSoftwareAsync(null, default, [runtime]), item => item.Id == runtime.Id);
        Assert.Equal(SoftwareKinds.Other, classified.Kind);
        Assert.Null(classified.CategorySource);
    }

    private sealed class IdentityResolver(RuntimeSoftwareAttribution identity)
        : IRuntimePackageIdentityResolver, IRuntimeRootIdentityResolver
    {
        public RuntimeAttributionObservation Observe(RuntimeProcessIdentity process) => RuntimeAttributionObservation.Matched(identity);
    }

    private sealed class NoIdentity : IRuntimePackageIdentityResolver, IRuntimeServiceIdentityResolver,
        IRuntimeRootIdentityResolver, IRuntimeSystemProcessClassifier
    {
        internal static readonly NoIdentity Instance = new();
        public RuntimeAttributionObservation Observe(RuntimeProcessIdentity process) => RuntimeAttributionObservation.NoMatch;
    }
}
