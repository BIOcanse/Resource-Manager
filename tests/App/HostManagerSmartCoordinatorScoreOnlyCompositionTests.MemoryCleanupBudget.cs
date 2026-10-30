using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Domain.Optimization.MemoryCleanup;
using ResourceManager.App.Infrastructure.Optimization;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;

namespace Resource_Manager_APP.Tests;

public sealed partial class HostManagerSmartCoordinatorScoreOnlyCompositionTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AutomaticMemoryCleanupBudgetOneCompletesBoundedRoundsOnlyWithKnownOutcomes(
        bool secondOutcomeKnown)
    {
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(
            warm: false,
            scoreOnlyEnabled: false);
        using var planner = new NativeAutomaticMemoryCleanupPlanner(
            new HostManagerMemoryCleanupRuntime(fixture.RuntimePlanProvider, fixture.DeploymentState));
        fixture.MemoryCleanupPlanner.PlanHandler = planner.Plan;
        var request = CreateAutomaticMemoryCleanupRequest(candidateCount: 2) with
        {
            OrdinaryMemoryFreeRatio = 0.15,
            PhysicalMemoryFreeRatio = 0.15
        };
        request = request with
        {
            Candidates = request.Candidates.Select((candidate, index) => candidate with
            {
                RuntimeState = HostManagerRuntimeStates.BackgroundProcess,
                CpuScore = index == 0 ? 20 : 10
            }).ToArray()
        };

        for (var round = 1; round <= 2; round++)
        {
            var outcomeKnown = round == 1 || secondOutcomeKnown;
            fixture.ProcessPolicyWriter.BatchHandler = requests => outcomeKnown
                ? requests.Select(static item => new ProcessResourcePolicyBatchWriteResult(
                    item.ProcessId,
                    [new ProcessResourcePolicyBatchFieldResult(
                        ProcessResourcePolicyBatchFields.TrimWorkingSet,
                        Succeeded: true,
                        "trimmed")])).ToArray()
                : [];
            var admission = HostManagerCycleEffectAdmission.Create(scoreOnly: false);
            admission.InitializeBudgets(1, 1);
            Assert.True(admission.TryAcquire(
                HostManagerCycleEffectKind.AutomaticMemoryCleanup, out var permit));

            var result = InvokeExecuteMemoryCleanup(
                fixture.Coordinator, permit, request, CancellationToken.None);
            planner.Complete(fixture.MemoryCleanupPlanner.LastFeedback);

            Assert.Equal(1U, fixture.MemoryCleanupPlanner.LastRequest!.MaximumDecisionCount);
            Assert.Equal(2, fixture.MemoryCleanupPlanner.LastRequest.Candidates.Count);
            Assert.Equal(2, result.CandidateCount);
            Assert.Equal(1, result.PlannedCount);
            Assert.Equal(1, result.RequestedCount);
            Assert.Equal(outcomeKnown
                ? HostManagerMemoryCleanupRoundDisposition.CompleteKnownFull
                : HostManagerMemoryCleanupRoundDisposition.BatchOutcomeUnknown, result.Disposition);
            Assert.Equal(0U, admission.NewPointOfNoReturnRemaining);
            Assert.Equal(request.Candidates[1].ProcessId,
                Assert.Single(fixture.ProcessPolicyWriter.BatchRequests).ProcessId);
        }
    }

    [Fact]
    public async Task AutomaticMemoryCleanupWithNoCycleBudgetDefersBeforePlanning()
    {
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(
            warm: false,
            scoreOnlyEnabled: false);
        var admission = HostManagerCycleEffectAdmission.Create(scoreOnly: false);
        admission.InitializeBudgets(1, 1);
        Assert.True(admission.TryAcquire(
            HostManagerCycleEffectKind.AutomaticMemoryCleanup, out var permit));
        Assert.True(permit.TryReserveExactNewPointOfNoReturn(1, out var spentBudget));
        using var spentBudgetScope = spentBudget!;
        spentBudget!.EnterPointOfNoReturn();

        var result = InvokeExecuteMemoryCleanup(
            fixture.Coordinator,
            permit,
            CreateAutomaticMemoryCleanupRequest(candidateCount: 2),
            CancellationToken.None);

        Assert.Equal(HostManagerMemoryCleanupRoundDisposition.DeferredByCycleBudget, result.Disposition);
        Assert.Equal(0, fixture.MemoryCleanupPlanner.PlanCalls);
        Assert.Equal(0, fixture.ProcessPolicyWriter.BatchCalls);
    }
}
