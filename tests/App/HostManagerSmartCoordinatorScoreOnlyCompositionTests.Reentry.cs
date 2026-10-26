using ResourceManager.App.Infrastructure.NativeCore;

namespace Resource_Manager_APP.Tests;

public sealed partial class HostManagerSmartCoordinatorScoreOnlyCompositionTests
{
    [Fact]
    public async Task HostedStopStartRecreatesWorkspaceAndObservationSubscriptions()
    {
        var reader = new PublicationControlledCpuResidencyReader();
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(
            warm: true,
            scoreOnlyEnabled: false,
            policyExecutionEnabled: false,
            processFactsSnapshot: CreateRoundProcessFacts(),
            memoryModePolicyEnabled: true,
            automaticMemoryCleanupEnabled: false,
            cpuCoreReader: reader);
        var trace = new OuterLoopTraceProbe();
        fixture.Coordinator.OuterLoopProbe = trace;
        NativeSmartCoordinatorWorkspace? previousWorkspace = null;

        for (var run = 1; run <= 2; run++)
        {
            await fixture.Coordinator.StartAsync(CancellationToken.None);
            try
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                while (reader.Subscriptions.Count < run)
                    await Task.Delay(1, deadline.Token);
                Assert.Equal(1, reader.ActiveSubscriptionCount);
                reader.Publish(null);
                await WaitForHostedScoreOnlyCycleAsync(trace, checked((ulong)run));
                var workspace = ReadPrivateField<NativeSmartCoordinatorWorkspace>(
                    fixture.Coordinator, "nativeWorkspace");
                Assert.NotNull(workspace);
                Assert.NotSame(previousWorkspace, workspace);
                previousWorkspace = workspace;
                Assert.False(fixture.Coordinator.ExecuteTask!.IsCompleted);
                Assert.NotNull(fixture.Coordinator.SchedulingAuthority.Compute);
                Assert.True(fixture.ProcessFacts.ActiveSubscriptionCount > 0);
            }
            finally
            {
                await fixture.Coordinator.StopAsync(CancellationToken.None)
                    .WaitAsync(TimeSpan.FromSeconds(10));
            }
            Assert.True(fixture.Coordinator.ExecuteTask!.IsCompletedSuccessfully);
            Assert.Equal(0, reader.ActiveSubscriptionCount);
            Assert.Equal(0, fixture.ProcessFacts.ActiveSubscriptionCount);
            Assert.Equal(0, fixture.MetricSampler.ActiveSubscriptionCount);
            AssertOwnedResourcesCleared(fixture.Coordinator);
        }
    }
}
