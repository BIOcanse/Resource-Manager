using ResourceManager.App.Application.SoftwareIssues;
using ResourceManager.App.Domain.Software;
using ResourceManager.App.Infrastructure.SoftwareIssues;

namespace Resource_Manager_APP.Tests;

public sealed class OptimizationReportSoftwareIssueSourceTests
{
    [Fact]
    public async Task MemorySignalTargetsNonGameAndDisappearsWithReportSnapshot()
    {
        var signals = new MutableSignalSource(new OptimizationSoftwareIssueSnapshot(
            DateTimeOffset.UtcNow,
            [Signal(
                "memory-report",
                SoftwareIssueKinds.AbnormalMemoryUsage,
                "内存占用异常",
                softwareId: "app")])) ;
        var source = new OptimizationReportSoftwareIssueSource(
            signals,
            new StaticIssueCatalog());

        var active = await source.CollectAsync(
            [Software("app", SoftwareKinds.Other), Software("game", SoftwareKinds.Game)],
            CancellationToken.None);
        signals.Snapshot = OptimizationSoftwareIssueSnapshot.Empty;
        var cleared = await source.CollectAsync(
            [Software("app", SoftwareKinds.Other)],
            CancellationToken.None);

        Assert.Equal("app", Assert.Single(active).SoftwareId);
        Assert.Empty(cleared);
    }

    [Fact]
    public async Task MemorySignalNeverLabelsGame()
    {
        var source = new OptimizationReportSoftwareIssueSource(
            new MutableSignalSource(new OptimizationSoftwareIssueSnapshot(
                DateTimeOffset.UtcNow,
                [Signal(
                    "memory-report",
                    SoftwareIssueKinds.AbnormalMemoryUsage,
                    "内存占用异常",
                    softwareId: "game")])) ,
            new StaticIssueCatalog());

        var assignments = await source.CollectAsync(
            [Software("game", SoftwareKinds.Game)],
            CancellationToken.None);

        Assert.Empty(assignments);
    }

    [Fact]
    public async Task InterruptSignalsUseExactArtifactOwnerAndMergeSameKind()
    {
        const string driverPath = @"C:\Apps\Fixture\driver.sys";
        var source = new OptimizationReportSoftwareIssueSource(
            new MutableSignalSource(new OptimizationSoftwareIssueSnapshot(
                DateTimeOffset.UtcNow,
                [
                    Signal(
                        "interrupt-count",
                        SoftwareIssueKinds.ExcessiveSystemInterrupts,
                        "造成过多系统中断",
                        artifactPath: driverPath),
                    Signal(
                        "interrupt-cpu",
                        SoftwareIssueKinds.ExcessiveSystemInterrupts,
                        "造成过多系统中断",
                        artifactPath: driverPath)
                ])),
            new StaticIssueCatalog(("fixture-identity", driverPath)));

        var assignments = await source.CollectAsync(
            [Software("fixture", SoftwareKinds.Other, "fixture-identity")],
            CancellationToken.None);

        var issue = Assert.Single(assignments).Issue;
        Assert.Equal(SoftwareIssueKinds.ExcessiveSystemInterrupts, issue.Kind);
        Assert.Equal(2, issue.ReportIds.Count);
        Assert.True(issue.Dynamic);
    }

    [Fact]
    public async Task AmbiguousArtifactOwnershipProducesNoLabel()
    {
        const string driverPath = @"C:\Shared\driver.sys";
        var source = new OptimizationReportSoftwareIssueSource(
            new MutableSignalSource(new OptimizationSoftwareIssueSnapshot(
                DateTimeOffset.UtcNow,
                [Signal(
                    "interrupt",
                    SoftwareIssueKinds.LongSystemInterrupts,
                    "造成长系统中断",
                    artifactPath: driverPath)])),
            new StaticIssueCatalog());

        var assignments = await source.CollectAsync(
            [
                Software("one", SoftwareKinds.Other) with { RootPaths = [@"C:\Shared"] },
                Software("two", SoftwareKinds.Other) with { RootPaths = [@"C:\Shared"] }
            ],
            CancellationToken.None);

        Assert.Empty(assignments);
    }

    private static OptimizationSoftwareIssueSignal Signal(
        string reportId,
        string kind,
        string label,
        string? softwareId = null,
        string? artifactPath = null)
        => new(
            reportId,
            kind,
            SoftwareIssueSeverities.Warning,
            label,
            reportId,
            softwareId,
            artifactPath);

    private static SoftwareRecord Software(
        string id,
        string kind,
        string? identityId = null)
        => new(
            id,
            id,
            kind,
            kind,
            "installed",
            [],
            [],
            string.Empty,
            new SoftwareOperationCapabilities(false, "None", "不可卸载", "test"),
            null,
            SoftwareIdentityId: identityId);

    private sealed class MutableSignalSource(
        OptimizationSoftwareIssueSnapshot snapshot)
        : IOptimizationSoftwareIssueSignalSource
    {
        public OptimizationSoftwareIssueSnapshot Snapshot { get; set; } = snapshot;

        public OptimizationSoftwareIssueSnapshot ReadSoftwareIssueSnapshot()
            => Snapshot;
    }

    private sealed class StaticIssueCatalog(
        params (string IdentityId, string ArtifactPath)[] ownership)
        : ISoftwareIssueCatalog
    {
        public string Version => "test";

        public IReadOnlyList<SoftwareIssueCatalogEntry> Entries => [];

        public ValueTask<IReadOnlyList<SoftwareIssueTag>> GetIssuesAsync(
            string softwareIdentityId,
            CancellationToken cancellationToken)
            => ValueTask.FromResult<IReadOnlyList<SoftwareIssueTag>>([]);

        public bool OwnsArtifactPath(
            string softwareIdentityId,
            string artifactPath)
            => ownership.Any(item =>
                item.IdentityId == softwareIdentityId
                && item.ArtifactPath.Equals(
                    artifactPath,
                    StringComparison.OrdinalIgnoreCase));
    }
}
