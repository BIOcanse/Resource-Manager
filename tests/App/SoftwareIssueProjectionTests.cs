using ResourceManager.App.Application.SoftwareIssues;
using ResourceManager.App.Domain.Software;
using ResourceManager.App.Infrastructure.SoftwareIssues;

namespace Resource_Manager_APP.Tests;

public sealed class SoftwareIssueProjectionTests
{
    [Fact]
    public async Task ProjectionCombinesMultipleSourcesAndDeduplicatesStableIds()
    {
        var warning = Issue("shared", SoftwareIssueSeverities.Warning, dynamic: false);
        var critical = Issue("dynamic", SoftwareIssueSeverities.Critical, dynamic: true);
        var projection = new SoftwareIssueProjection([
            new StaticSource([
                new SoftwareIssueAssignment("software-a", warning),
                new SoftwareIssueAssignment("software-a", critical)
            ]),
            new StaticSource([
                new SoftwareIssueAssignment("software-a", warning)
            ])
        ]);

        var projected = await projection.ProjectAsync(
            [Software("software-a"), Software("software-b")],
            CancellationToken.None);

        Assert.Collection(
            projected[0].Issues!,
            issue => Assert.Equal("dynamic", issue.Id),
            issue => Assert.Equal("shared", issue.Id));
        Assert.Empty(projected[1].Issues!);
    }

    private static SoftwareRecord Software(string id)
        => new(
            id,
            id,
            SoftwareKinds.Other,
            "普通软件",
            "installed",
            [],
            [],
            string.Empty,
            new SoftwareOperationCapabilities(false, "None", "不可卸载", "test"),
            null);

    private static SoftwareIssueTag Issue(
        string id,
        string severity,
        bool dynamic)
        => new(
            id,
            dynamic
                ? SoftwareIssueKinds.AbnormalMemoryUsage
                : SoftwareIssueKinds.DependencyIssue,
            severity,
            dynamic
                ? SoftwareIssueSources.DynamicReport
                : SoftwareIssueSources.StaticCatalog,
            id,
            id,
            dynamic,
            [],
            []);

    private sealed class StaticSource(
        IReadOnlyList<SoftwareIssueAssignment> assignments)
        : ISoftwareIssueSource
    {
        public Task<IReadOnlyList<SoftwareIssueAssignment>> CollectAsync(
            IReadOnlyList<SoftwareRecord> software,
            CancellationToken cancellationToken)
            => Task.FromResult(assignments);
    }
}
