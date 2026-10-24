using ResourceManager.App.Application.Software;
using ResourceManager.App.Domain.ProcessAttribution;
using ResourceManager.App.Domain.Software;
using ResourceManager.App.Infrastructure.SoftwareIssues;

namespace Resource_Manager_APP.Tests;

public sealed class SoftwareRuntimeReminderTests
{
    [Theory]
    [InlineData(SoftwareKinds.RuntimePackage)]
    [InlineData(SoftwareKinds.RuntimeProduct)]
    [InlineData(SoftwareKinds.RuntimeRoot)]
    public async Task RuntimeOnlySoftwareIsListedAndManualClassificationClearsTheReminder(string kind)
    {
        var runtime = new RuntimeSoftwareAttribution("runtime:app", "Runtime App", kind, "Other", []);
        var records = SoftwareRegistryView.IncludeRuntimeSoftware([], [runtime], []);
        Assert.Equal(runtime.Name, Assert.Single(records).Name);
        var issues = new SoftwareIssueProjection([]);
        Assert.Equal(SoftwareIssueKinds.Unclassified, Assert.Single(Assert.Single(await issues.ProjectAsync(records, default)).Issues!).Kind);
        foreach (var category in new[] { SoftwareKinds.Other, SoftwareKinds.Game, SoftwareKinds.HighPerformance })
        {
            var manual = new ManualSoftwareRecord("manual:app", runtime.Name, category, category, "manual", [], [], "",
                runtime.Id, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
            var classified = SoftwareRegistryView.IncludeRuntimeSoftware([], [runtime], [manual]);
            var record = Assert.Single(await issues.ProjectAsync(classified, default));
            Assert.Equal(category, record.Kind);
            Assert.Empty(record.Issues!);
        }
    }

    [Fact]
    public void KnownCatalogSoftwareTakesPrecedenceAndRuntimeRootsArePreserved()
    {
        var runtime = new RuntimeSoftwareAttribution("runtime:app", "App", SoftwareKinds.RuntimeProduct, "Other", [@"C:\Apps\App"]);
        var known = new SoftwareRecord("catalog:app", "App", SoftwareKinds.Game, "Game", "installed", [], runtime.RootPaths, "",
            new(false, "None", "", ""), null, SoftwareIdentityId: "catalog:app");
        Assert.Equal(known, Assert.Single(SoftwareRegistryView.IncludeRuntimeSoftware([known], [runtime], [])));
        Assert.Equal(runtime.RootPaths, Assert.Single(SoftwareRegistryView.IncludeRuntimeSoftware([], [runtime], [])).RootPaths);
    }
}
