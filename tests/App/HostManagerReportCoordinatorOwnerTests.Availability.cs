using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.Optimization.Reports;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;

namespace Resource_Manager_APP.Tests;

public sealed partial class HostManagerReportCoordinatorOwnerTests
{
    [Fact]
    public async Task BackgroundEvaluationFailurePreservesCommittedReportsAndRecoveryClearsFailure()
    {
        var deployment = new HostManagerDeploymentState();
        var provider = new RuntimePlanProvider(deployment);
        provider.Publish(CompiledRuntimePlan.Default with { Version = 1, HostManager = HostManagerTestPlanFactory.CreatePlan() });
        var metrics = new CachedMetricSource();
        var resources = new CachedResourceSource();
        using var owner = new HostManagerReportCoordinatorOwner(provider,
            new HostManagerReportCoordinatorRuntime(deployment), new HostManagerRuntimeIdentity(),
            new NativeReportCoordinatorPersistenceStore(CreateDatabase()), metrics, resources,
            new UnavailableInterruptSource(), new EmptyProtectionService(),
            NullLogger<HostManagerReportCoordinatorOwner>.Instance, OperationScratchFixture.Create,
            new BackgroundProcessSource(), new RegisteredSoftware());
        await owner.StartAsync(CancellationToken.None);
        try
        {
            var observedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            for (var sample = 0; sample < 3; sample++)
            {
                resources.Publish(CreateSoftwareMemorySnapshot(41 + sample, observedAt.AddSeconds(10 * sample), 18));
                await owner.RefreshReportsAsync(CancellationToken.None);
            }
            var committed = await owner.GetReportsAsync(CancellationToken.None);
            Assert.Single(committed.Reports);
            Assert.Equal("completed", committed.Status.EvaluationState);
            Assert.NotEmpty(committed.Status.UnavailableRules);
            Assert.All(committed.Status.UnavailableRules, name => Assert.False(string.IsNullOrWhiteSpace(name)));
            Assert.Equal(committed.Status.ConfiguredRuleCount - committed.Status.AvailableRuleCount,
                committed.Status.UnavailableRules.Count);
            metrics.ReadFailure = new IOException("fixture evaluation failed");
            provider.Publish(provider.Current with { Version = 2 });
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (owner.GetStatus().EvaluationState != "failed") await Task.Delay(10, timeout.Token);
            var failed = await owner.GetReportsAsync(timeout.Token);
            Assert.Equal("IOException: fixture evaluation failed", failed.Status.EvaluationFailureReason);
            Assert.Equal(committed.Reports, failed.Reports);
            Assert.Equal(committed.CapturedAt, failed.CapturedAt);
            Assert.True(failed.Status.LastEvaluationAt >= committed.Status.LastEvaluationAt);
            // A trust command can publish reports without completing a new evaluation.
            await owner.TrustReportAsync(committed.Reports[0].Id, timeout.Token);
            Assert.Equal("failed", owner.GetStatus().EvaluationState);
            metrics.ReadFailure = null;
            var recovered = await owner.RefreshReportsAsync(timeout.Token);
            Assert.Equal("completed", recovered.Status.EvaluationState);
            Assert.Null(recovered.Status.EvaluationFailureReason);
        }
        finally { await owner.StopAsync(CancellationToken.None); }
    }
}
