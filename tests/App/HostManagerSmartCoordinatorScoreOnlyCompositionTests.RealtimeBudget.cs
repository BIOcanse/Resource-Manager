using ResourceManager.App.Domain.Settings;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Optimization;

namespace Resource_Manager_APP.Tests;

public sealed partial class HostManagerSmartCoordinatorScoreOnlyCompositionTests
{
    [Theory]
    [InlineData("fixed", 7, 5, 0, 7U)]
    [InlineData("fixed", 7, 5, 41, 7U)]
    [InlineData("percent", 7, 5, 12, 1U)]
    [InlineData("percent", 7, 5, 41, 3U)]
    public async Task RealtimeBudgetRuleAndResolvedCountAreVisible(
        string mode, int count, int percent, int processCount, uint expected)
    {
        var facts = CreateRealtimeBudgetInventory(processCount);
        // Invalid runtime-state values prevent these identified rows becoming scheduling targets.
        facts = facts with { Processes = facts.Processes.Select((process, index) => index % 2 == 0
            ? process with { RuntimeState = "invalid-runtime-state" }
            : process).ToArray() };
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(
            warm: true, scoreOnlyEnabled: false, performanceLogEnabled: true,
            processFactsSnapshot: facts, realtimeActionBudget: count,
            realtimeActionBudgetMode: mode, realtimeActionBudgetPercent: percent,
            automaticMemoryCleanupEnabled: false);
        _ = await fixture.RunRealtimeCycleAsync();
        Assert.Equal(mode == "fixed" ? 1 : 2, fixture.ProcessFacts.ReadLatestCalls);
        Assert.Equal(0, fixture.ProcessFacts.CaptureCalls);
        if (mode == "percent")
            Assert.Equal(SchedulingProcessMetricMask.RuntimeState,
                fixture.ProcessFacts.ReadLatestRequests[0].RequestedMetricMask);
        var budget = (await fixture.Coordinator.GetDecisionDiagnosticsAsync(CancellationToken.None)).ActionBudget!;
        Assert.Equal(expected, budget.MaximumActionsThisCycle);
        Assert.Equal(mode, budget.Mode);
        Assert.Equal(count, budget.Count);
        Assert.Equal(percent, budget.Percent);
        Assert.Equal(mode == "fixed" ? count : percent, budget.Value);
        Assert.Equal(mode == "fixed" ? (int?)null : processCount, budget.ProcessCount);
        Assert.Null(budget.DeferredReason);
        Assert.True(budget.RealtimeCycle);
        var record = Assert.Single(fixture.DebugLogWriter.Records);
        var details = Assert.IsType<Dictionary<string, object?>>(record.Properties["details"]);
        Assert.Equal(budget, details["actionBudget"]);
        var counts = Assert.IsType<Dictionary<string, object?>>(record.Properties["counts"]);
        Assert.Equal(processCount, counts["sampleProcesses"]);
    }

    [Theory]
    [InlineData(AppOptimizationModes.Normal)]
    [InlineData(AppOptimizationModes.Smart)]
    public async Task FixedBudgetNeverReadsInventoryWhenSchedulingSampleIsUnavailable(string mode)
    {
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(
            warm: true, scoreOnlyEnabled: false, optimizationMode: mode,
            realtimeActionBudget: 7, automaticMemoryCleanupEnabled: false);
        fixture.MetricSampler.ObservationAvailable = false;
        fixture.ProcessFacts.ReadLatestHandler = _ => throw new InvalidOperationException("Unexpected inventory read.");
        _ = await fixture.RunRealtimeCycleAsync();
        Assert.Equal(0, fixture.ProcessFacts.ReadLatestCalls);
        var budget = (await fixture.Coordinator.GetDecisionDiagnosticsAsync(CancellationToken.None)).ActionBudget!;
        Assert.Equal(7U, budget.MaximumActionsThisCycle);
        Assert.Null(budget.ProcessCount);
    }

    [Theory]
    [InlineData(AppOptimizationModes.Normal, false, "process-inventory-unavailable")]
    [InlineData(AppOptimizationModes.Normal, true, "process-inventory-empty")]
    [InlineData(AppOptimizationModes.Smart, false, "process-inventory-unavailable")]
    [InlineData(AppOptimizationModes.Smart, true, "process-inventory-empty")]
    public async Task PercentageBudgetDefersUntilInventoryExistsThenRetries(
        string mode, bool empty, string reason)
    {
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(
            warm: true, scoreOnlyEnabled: false, performanceLogEnabled: true,
            optimizationMode: mode, realtimeActionBudgetMode: "percent",
            automaticMemoryCleanupEnabled: false);
        await fixture.PreparePendingTransactionAsync(NativeTransactionJournalScope.Resource);
        fixture.ProcessFacts.ReadLatestHandler = empty
            ? request => SchedulingProcessFactTestProjection.Project(CreateRealtimeBudgetInventory(0), request)
            : _ => null;
        var delay = await fixture.RunRealtimeCycleAsync();
        Assert.Equal(TimeSpan.FromMilliseconds(fixture.RuntimePlan.HostManager.SmartCoordinator.HotPublish
            .ReservationTimeoutMilliseconds), delay);
        Assert.Single((await fixture.ReadJournalSnapshotAsync()).Records);
        var deferred = (await fixture.Coordinator.GetDecisionDiagnosticsAsync(CancellationToken.None)).ActionBudget!;
        Assert.Equal("percent", deferred.Mode);
        Assert.Equal(32, deferred.Count);
        Assert.Equal(5, deferred.Percent);
        Assert.Equal(empty ? (int?)0 : null, deferred.ProcessCount);
        Assert.Null(deferred.MaximumActionsThisCycle);
        Assert.Equal(reason, deferred.DeferredReason);
        var record = Assert.Single(fixture.DebugLogWriter.Records);
        Assert.Equal("deferred", record.Properties["outcome"]);
        Assert.Equal(reason, record.Properties["message"]);
        Assert.Equal(deferred, Assert.IsType<Dictionary<string, object?>>(record.Properties["details"])["actionBudget"]);
        Assert.Equal(SchedulingProcessMetricMask.RuntimeState,
            Assert.Single(fixture.ProcessFacts.ReadLatestRequests).RequestedMetricMask);

        var publishedInventory = CreateRealtimeBudgetInventory(12);
        fixture.ProcessFacts.ReadLatestHandler = request =>
            SchedulingProcessFactTestProjection.Project(publishedInventory, request);
        _ = await fixture.RunRealtimeCycleAsync();
        Assert.Empty((await fixture.ReadJournalSnapshotAsync()).Records);
        var resolved = (await fixture.Coordinator.GetDecisionDiagnosticsAsync(CancellationToken.None)).ActionBudget!;
        Assert.Equal(1U, resolved.MaximumActionsThisCycle);
        Assert.Equal(12, resolved.ProcessCount);
        Assert.Null(resolved.DeferredReason);
    }

    [Theory]
    [InlineData("fixed", false)]
    [InlineData("fixed", true)]
    [InlineData("percent", false)]
    [InlineData("percent", true)]
    public async Task SmartSampleFailureStillSettlesJournaledTransactions(string rule, bool missingProcessSample)
    {
        var inventory = CreateRealtimeBudgetInventory(12);
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(
            warm: true, scoreOnlyEnabled: false, performanceLogEnabled: true,
            optimizationMode: AppOptimizationModes.Smart, realtimeActionBudget: 7,
            realtimeActionBudgetMode: rule, processFactsSnapshot: inventory,
            automaticMemoryCleanupEnabled: false);
        await fixture.PreparePendingTransactionAsync(NativeTransactionJournalScope.Resource);
        Assert.Single((await fixture.ReadJournalSnapshotAsync()).Records);
        if (missingProcessSample)
            fixture.ProcessFacts.ReadLatestHandler = request => request.RequestedMetricMask == SchedulingProcessMetricMask.RuntimeState
                ? SchedulingProcessFactTestProjection.Project(inventory, request) : null;
        else
            fixture.MetricSampler.ObservationAvailable = false;

        _ = await fixture.RunRealtimeCycleAsync();

        Assert.Empty((await fixture.ReadJournalSnapshotAsync()).Records);
        var record = Assert.Single(fixture.DebugLogWriter.Records);
        Assert.Equal("deferred", record.Properties["outcome"]);
        Assert.Equal(missingProcessSample ? "scheduling-process-snapshot-unavailable" : "hardware-snapshot-unavailable",
            record.Properties["message"]);
        var budget = (await fixture.Coordinator.GetDecisionDiagnosticsAsync(CancellationToken.None)).ActionBudget!;
        Assert.Equal(rule == "fixed" ? 7U : 1U, budget.MaximumActionsThisCycle);
        Assert.Equal(rule == "fixed" ? (int?)null : 12, budget.ProcessCount);
        Assert.Equal((rule == "percent" ? 1 : 0) + (missingProcessSample ? 1 : 0), fixture.ProcessFacts.ReadLatestCalls);
    }

    [Fact]
    public async Task PercentageBudgetKeepsTheInventoryCountReadBeforeRecovery()
    {
        var budgetInventory = CreateRealtimeBudgetInventory(12);
        var schedulingInventory = CreateRealtimeBudgetInventory(41);
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(
            warm: true, scoreOnlyEnabled: false, performanceLogEnabled: true,
            realtimeActionBudgetMode: "percent", automaticMemoryCleanupEnabled: false);
        fixture.ProcessFacts.ReadLatestHandler = request => SchedulingProcessFactTestProjection.Project(
            request.RequestedMetricMask == SchedulingProcessMetricMask.RuntimeState ? budgetInventory : schedulingInventory,
            request);
        _ = await fixture.RunRealtimeCycleAsync();
        var budget = (await fixture.Coordinator.GetDecisionDiagnosticsAsync(CancellationToken.None)).ActionBudget!;
        Assert.Equal(12, budget.ProcessCount);
        Assert.Equal(1U, budget.MaximumActionsThisCycle);
        Assert.Equal(2, fixture.ProcessFacts.ReadLatestCalls);
        var counts = Assert.IsType<Dictionary<string, object?>>(Assert.Single(fixture.DebugLogWriter.Records).Properties["counts"]);
        Assert.Equal(41, counts["sampleProcesses"]);
    }

    [Theory]
    [InlineData("fixed")]
    [InlineData("percent")]
    public async Task NormalForegroundCycleKeepsFullCapacityWithoutInventoryOrHardware(string mode)
    {
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(
            warm: true, scoreOnlyEnabled: false, optimizationMode: AppOptimizationModes.Normal,
            realtimeActionBudget: 1, realtimeActionBudgetMode: mode, realtimeActionBudgetPercent: 1,
            automaticMemoryCleanupEnabled: false);
        fixture.MetricSampler.ObservationAvailable = false;
        fixture.ProcessFacts.ReadLatestHandler = _ => throw new InvalidOperationException("Unexpected inventory read.");
        await fixture.Coordinator.RunOnceAsync(CancellationToken.None);
        Assert.Empty(fixture.ProcessFacts.ReadLatestRequests);
        var budget = (await fixture.Coordinator.GetDecisionDiagnosticsAsync(CancellationToken.None)).ActionBudget!;
        Assert.Equal(fixture.Workspace.Capacity.ActionCapacity, budget.MaximumActionsThisCycle);
        Assert.False(budget.RealtimeCycle);
        Assert.Null(budget.ProcessCount);
    }

    private static SchedulingProcessFactSnapshot CreateRealtimeBudgetInventory(int processCount)
    {
        var startKey = checked((ulong)DateTimeOffset.UtcNow.AddMinutes(-5).ToFileTime());
        return CreateCompleteProcessFacts(Enumerable.Range(30_000, processCount)
            .Select(pid => (pid, startKey, $"software:budget:{pid}", 90d, 1d, 1d)).ToArray());
    }
}
