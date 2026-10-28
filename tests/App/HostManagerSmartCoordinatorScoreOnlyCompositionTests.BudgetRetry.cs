using ResourceManager.App.Application.Settings;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Domain.Settings;
using ResourceManager.App.Infrastructure.Optimization;

namespace Resource_Manager_APP.Tests;

public sealed partial class HostManagerSmartCoordinatorScoreOnlyCompositionTests
{
    [Theory]
    [InlineData(false, "process-inventory-unavailable")]
    [InlineData(true, "process-inventory-empty")]
    public async Task NormalPercentBudgetRetriesAfterFixedCycleUntilInventoryArrives(
        bool empty, string deferredReason)
    {
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(
            warm: true,
            scoreOnlyEnabled: false,
            optimizationMode: AppOptimizationModes.Normal,
            automaticMemoryCleanupEnabled: false);
        fixture.PublicResourceManager.Available = false;
        var deadline = Assert.IsType<HostManagerVersionedWakeDeadline>(
            ReadPrivateField<HostManagerVersionedWakeDeadline>(fixture.Coordinator, "schedulingWakeDeadline"));

        await fixture.Coordinator.StartAsync(CancellationToken.None);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            HostManagerDecisionDiagnosticsSnapshot diagnostics;
            do
            {
                await Task.Delay(10, timeout.Token);
                diagnostics = await fixture.Coordinator.GetDecisionDiagnosticsAsync(timeout.Token);
            }
            while (diagnostics.ActionBudget?.Mode != AppRealtimeActionBudgetModes.Fixed
                || !deadline.Snapshot.HasDeadline);
            Assert.True(deadline.Snapshot.HasDeadline);
            Assert.Equal(0, fixture.ProcessFacts.ReadLatestCalls);

            var unavailableInventory = empty ? CreateRealtimeBudgetInventory(0) : null;
            var publishedInventory = CreateRealtimeBudgetInventory(12);
            var inventoryAvailable = 0;
            var inventoryReads = 0;
            fixture.ProcessFacts.ReadLatestHandler = request =>
            {
                Interlocked.Increment(ref inventoryReads);
                var inventory = Volatile.Read(ref inventoryAvailable) == 1
                    ? publishedInventory : unavailableInventory;
                return inventory is null ? null : SchedulingProcessFactTestProjection.Project(inventory, request);
            };
            var performance = AppSettingsDefaults.Create().Performance with
            {
                OptimizationMode = AppOptimizationModes.Normal,
                RealtimeActionBudgetMode = AppRealtimeActionBudgetModes.Percent
            };
            var percentPlan = HostManagerTestPlanFactory.CreatePlan(
                editProfile: root => root["hot_publish"]!["smart_coordinator"]!["reservation_timeout_ms"] = 100,
                performance: performance,
                planEpoch: 2);
            fixture.RuntimePlanProvider.Publish(fixture.RuntimePlan with { Version = 2, HostManager = percentPlan });
            do
            {
                await Task.Delay(10, timeout.Token);
                diagnostics = await fixture.Coordinator.GetDecisionDiagnosticsAsync(timeout.Token);
            }
            while (Volatile.Read(ref inventoryReads) < 2
                || diagnostics.ActionBudget?.DeferredReason != deferredReason
                || !deadline.Snapshot.HasDeadline);

            Assert.Equal(empty ? (int?)0 : null, diagnostics.ActionBudget!.ProcessCount);
            Assert.Null(diagnostics.ActionBudget.MaximumActionsThisCycle);
            Assert.Equal(1, fixture.ProcessFacts.ActiveSubscriptionCount);
            Assert.All(fixture.ProcessFacts.ReadLatestRequests, request =>
                Assert.Equal(SchedulingProcessMetricMask.RuntimeState, request.RequestedMetricMask));

            // Only change the latest observation. No plan/resource publication or foreground wake follows.
            Volatile.Write(ref inventoryAvailable, 1);
            do
            {
                await Task.Delay(10, timeout.Token);
                diagnostics = await fixture.Coordinator.GetDecisionDiagnosticsAsync(timeout.Token);
            }
            while (diagnostics.ActionBudget?.DeferredReason is not null
                || diagnostics.ActionBudget?.ProcessCount != 12
                || !deadline.Snapshot.HasDeadline);

            Assert.Equal(1U, diagnostics.ActionBudget!.MaximumActionsThisCycle);
            Assert.True(Volatile.Read(ref inventoryReads) >= 3);
        }
        finally
        {
            await fixture.Coordinator.StopAsync(CancellationToken.None);
        }
    }
}
