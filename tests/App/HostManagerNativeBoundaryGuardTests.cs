using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;

namespace Resource_Manager_APP.Tests;

public sealed class HostManagerNativeBoundaryGuardTests
{
    [Fact]
    public void DeploymentSettlementGuard_RejectsStaleSuccessAndFailure()
    {
        Assert.Throws<InvalidOperationException>(() =>
            HostManagerDeploymentAttemptSettlementGuard.RequireApplied(
                HostManagerDeploymentAttemptSettlement.Stale,
                HostManagerModuleKind.SharedResources));
        Assert.Throws<InvalidOperationException>(() =>
            HostManagerDeploymentAttemptSettlementGuard.RequireFailed(
                HostManagerDeploymentAttemptSettlement.Stale,
                HostManagerModuleKind.SharedResources));
    }

    [Fact]
    public void BestEffortCleanup_AttemptsEveryMemberAfterFailures()
    {
        var first = new RecordingDisposable(throws: true);
        var second = new RecordingDisposable(throws: true);
        var third = new RecordingDisposable(throws: false);

        var failures = HostManagerBestEffortCleanup.DisposeAll(first, second, third);

        Assert.Equal(2, failures.Count);
        Assert.Equal(1, first.DisposeCount);
        Assert.Equal(1, second.DisposeCount);
        Assert.Equal(1, third.DisposeCount);
    }

    private sealed class RecordingDisposable(bool throws) : IDisposable
    {
        public int DisposeCount { get; private set; }

        public void Dispose()
        {
            DisposeCount++;
            if (throws)
            {
                throw new InvalidOperationException("Synthetic cleanup failure.");
            }
        }
    }
}
