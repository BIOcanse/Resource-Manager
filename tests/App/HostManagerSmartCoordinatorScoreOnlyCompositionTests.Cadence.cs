using ResourceManager.App.Infrastructure.NativeCore;

namespace Resource_Manager_APP.Tests;

public sealed partial class HostManagerSmartCoordinatorScoreOnlyCompositionTests
{
    [Fact]
    public async Task NormalWakeIsThirtySecondsAndManualEventSchedulesTenFiveSecondCycles()
    {
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(
            warm: true, automaticMemoryCleanupEnabled: false);

        Assert.Equal(TimeSpan.FromSeconds(30), await fixture.RunRealtimeCycleAsync());
        Assert.Equal(0U, fixture.Workspace.Snapshot.EventBoostRoundsRemaining);
        Assert.Equal(TimeSpan.FromSeconds(5), await fixture.RunRealtimeCycleAsync("manual"));
        Assert.Equal(10U, fixture.Workspace.Snapshot.EventBoostRoundsRemaining);

        for (uint completed = 1; completed <= 10; completed++)
        {
            Assert.Equal(TimeSpan.FromSeconds(completed < 10 ? 5 : 30),
                await fixture.RunRealtimeCycleAsync());
            var snapshot = fixture.Workspace.Snapshot;
            Assert.Equal(10 - completed, snapshot.EventBoostRoundsRemaining);
            Assert.Equal(completed < 10,
                snapshot.Flags.HasFlag(NativeSmartCoordinatorSnapshotFlags.EventBoostActive));
            var diagnostics = await fixture.Coordinator.GetDecisionDiagnosticsAsync(CancellationToken.None);
            Assert.NotNull(diagnostics.NativeCoordinator);
            Assert.Equal(snapshot.EventBoostRoundsRemaining,
                diagnostics.NativeCoordinator.EventBoostRoundsRemaining);
            Assert.Equal(snapshot.WakeAfterMilliseconds,
                diagnostics.NativeCoordinator.WakeAfterMilliseconds);
        }
    }
}
