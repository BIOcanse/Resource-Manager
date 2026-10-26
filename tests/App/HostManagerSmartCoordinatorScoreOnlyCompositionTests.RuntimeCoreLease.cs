using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Domain.Settings;
using ResourceManager.App.Infrastructure.Optimization;
using ResourceManager.App.Domain.ResourceBreakdown;

namespace Resource_Manager_APP.Tests;

public sealed partial class HostManagerSmartCoordinatorScoreOnlyCompositionTests
{
    [Theory]
    [InlineData("software", AppOptimizationModes.Normal)]
    [InlineData("process", AppOptimizationModes.Normal)]
    [InlineData("path", AppOptimizationModes.Smart)]
    public async Task RuntimeCoreLeaseUsesExistingTransactionsAndRestoresOriginalRanges(string kind, string mode)
    {
        const int pid = 53101, sibling = 53102, other = 53103;
        const ulong birth = 133900000000000101;
        var topology = CreateAutomaticPlacementTopology();
        var facts = CreateCompleteProcessFacts((pid, birth, "probe", 80d, 20d, 5d),
            (sibling, birth + 1, "probe", 20d, 20d, 5d), (other, birth + 2, "other", 20d, 20d, 5d));
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(warm: true, scoreOnlyEnabled: false,
            processFactsSnapshot: facts, policyExecutionEnabled: false, automaticMemoryCleanupEnabled: false,
            optimizationMode: mode, optimizationCapabilities: OptimizationModeCapabilities.HardwarePlacement,
            cpuTopology: topology, cpuScoring: HostManagerTestPlanFactory.CreateCpuScoring(topology), automaticPlacementTopology: topology);
        var allowed = RecordCpuLeaseBindings(fixture);
        allowed[pid] = [1, 2]; // An external original range must never be widened by apply or restore.
        var target = kind switch
        {
            "software" => new RuntimeCpuLeaseTarget(SoftwareId: "probe"),
            "process" => new RuntimeCpuLeaseTarget(ProcessId: pid, ProcessStartKey: birth),
            _ => new RuntimeCpuLeaseTarget(ExecutablePath: facts.Processes[0].ExecutablePath)
        };
        var lease = fixture.Coordinator.CreateCpuLease(new(target, ["core:0"], true, true), "fixture-holder");
        Assert.Equal("pending", lease.State);
        for (var cycle = 0; cycle < 12; cycle++)
        {
            if (cycle != 0) fixture.PublishNextHostedCpuObservation();
            await fixture.RunRealtimeCycleAsync();
            var current = await fixture.Coordinator.ReadCpuLeaseAsync(lease.LeaseId, default);
            if (current?.State == "observed") break;
        }
        Assert.Equal([1U], allowed[pid]);
        if (kind == "software") Assert.Equal([1U], allowed[sibling]);
        else Assert.DoesNotContain(1U, allowed[sibling]);
        Assert.DoesNotContain(1U, allowed[other]);
        var observed = (await fixture.Coordinator.ReadCpuLeaseAsync(lease.LeaseId, default))!;
        Assert.Equal("observed", observed.State);
        Assert.Contains(observed.Effects, effect => effect.ProcessId == pid && effect.Role == "target" && effect.State == "observed");
        Assert.Contains(observed.Effects, effect => effect.ProcessId == other && effect.Role == "avoidance" && effect.State == "observed");
        var ended = fixture.Coordinator.WaitForCpuLeaseEndAsync(lease.LeaseId, default);
        Assert.True(fixture.Coordinator.DeleteCpuLease(lease.LeaseId));
        await ended.WaitAsync(TimeSpan.FromSeconds(5));
        for (var cycle = 0; cycle < 12 && fixture.StateStore.Current.AppliedPlacements.Count != 0; cycle++)
        { fixture.PublishNextHostedCpuObservation(); await fixture.RunRealtimeCycleAsync(); }
        Assert.Equal([1U, 2U], allowed[pid]);
        Assert.Equal([1U, 2U, 3U, 4U], allowed[other]);
        Assert.Null(await fixture.Coordinator.ReadCpuLeaseAsync(lease.LeaseId, default));
    }

    [Fact]
    public async Task RuntimeCoreLeaseReportsWriteFailureAndNativeReason()
    {
        const int pid = 53111;
        const ulong birth = 133900000000000111;
        var topology = CreateAutomaticPlacementTopology();
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(warm: true, scoreOnlyEnabled: false,
            processFactsSnapshot: CreateCompleteProcessFacts(pid, birth, "probe", 80, 20, 5),
            policyExecutionEnabled: false, automaticMemoryCleanupEnabled: false,
            optimizationCapabilities: OptimizationModeCapabilities.HardwarePlacement,
            cpuTopology: topology, cpuScoring: HostManagerTestPlanFactory.CreateCpuScoring(topology), automaticPlacementTopology: topology);
        RecordCpuLeaseBindings(fixture);
        fixture.ProcessPolicyWriter.BatchHandler = requests => requests.Select(request => new ProcessResourcePolicyBatchWriteResult(
            request.ProcessId, [new ProcessResourcePolicyBatchFieldResult(ProcessResourcePolicyBatchFields.CpuSets, false, "access-denied") { ErrorCode = 5 }])).ToArray();
        var lease = fixture.Coordinator.CreateCpuLease(new(new(ProcessId: pid, ProcessStartKey: birth), ["core:0"], true, false), "fixture-holder");
        for (var cycle = 0; cycle < 8; cycle++)
        {
            if (cycle != 0) fixture.PublishNextHostedCpuObservation();
            await fixture.RunRealtimeCycleAsync();
            if ((await fixture.Coordinator.ReadCpuLeaseAsync(lease.LeaseId, default))?.State == "failed") break;
        }
        var failed = Assert.Single((await fixture.Coordinator.ReadCpuLeaseAsync(lease.LeaseId, default))!.Effects);
        Assert.Equal("failed", failed.State);
        Assert.Equal("cpu-placement-write-failed", failed.ReasonCode);
        Assert.Equal(5, failed.NativeErrorCode);
    }

    [Fact]
    public async Task RuntimeCoreLeaseEndsOnExactInstanceExitButNotOnIncompleteInventory()
    {
        const int pid = 53121;
        const ulong birth = 133900000000000121;
        var topology = CreateAutomaticPlacementTopology();
        var facts = CreateCompleteProcessFacts(pid, birth, "probe", 80, 20, 5);
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(warm: true, scoreOnlyEnabled: false,
            processFactsSnapshot: facts, policyExecutionEnabled: false, automaticMemoryCleanupEnabled: false,
            optimizationCapabilities: OptimizationModeCapabilities.HardwarePlacement,
            cpuTopology: topology, cpuScoring: HostManagerTestPlanFactory.CreateCpuScoring(topology), automaticPlacementTopology: topology);
        RecordCpuLeaseBindings(fixture);
        var lease = fixture.Coordinator.CreateCpuLease(new(new(ProcessId: pid, ProcessStartKey: birth), ["core:0"], true, false), "holder");
        fixture.ProcessFacts.SetSnapshot(facts with { HasUnidentifiedProcesses = true });
        await fixture.Coordinator.RunOnceAsync(default);
        Assert.NotNull(await fixture.Coordinator.ReadCpuLeaseAsync(lease.LeaseId, default));
        // The same PID's next instance cannot inherit the old lease.
        fixture.ProcessFacts.SetSnapshot(facts with { Processes = [facts.Processes[0] with { ProcessStartKey = birth + 1 }] });
        await fixture.Coordinator.RunOnceAsync(default);
        Assert.Null(await fixture.Coordinator.ReadCpuLeaseAsync(lease.LeaseId, default));
    }

    [Fact]
    public async Task RuntimeCoreLeaseRegistrationSurvivesCoordinatorStopAndNewRun()
    {
        const int pid = 53131;
        const ulong birth = 133900000000000131;
        var topology = CreateAutomaticPlacementTopology();
        var store = new RuntimeCpuLeaseStore();
        var facts = CreateCompleteProcessFacts(pid, birth, "probe", 80, 20, 5);
        string id;
        await using (var first = await ScoreOnlyCoordinatorFixture.CreateAsync(warm: true, scoreOnlyEnabled: false,
            processFactsSnapshot: facts, policyExecutionEnabled: false, automaticMemoryCleanupEnabled: false,
            optimizationCapabilities: OptimizationModeCapabilities.HardwarePlacement, cpuLeaseStore: store,
            cpuTopology: topology, cpuScoring: HostManagerTestPlanFactory.CreateCpuScoring(topology), automaticPlacementTopology: topology))
        {
            RecordCpuLeaseBindings(first);
            id = first.Coordinator.CreateCpuLease(new(new(ProcessId: pid, ProcessStartKey: birth), ["core:0"], true, false), "holder").LeaseId;
            await first.Coordinator.RunOnceAsync(default);
            await first.Coordinator.StopAsync(default);
            var stopped = await first.Coordinator.ReadCpuLeaseAsync(id, default);
            Assert.NotNull(stopped);
            Assert.Equal("scheduler-stopped", stopped.ReasonCode);
        }
        await using var second = await ScoreOnlyCoordinatorFixture.CreateAsync(warm: true, scoreOnlyEnabled: false,
            processFactsSnapshot: facts, policyExecutionEnabled: false, automaticMemoryCleanupEnabled: false,
            optimizationCapabilities: OptimizationModeCapabilities.HardwarePlacement, cpuLeaseStore: store,
            cpuTopology: topology, cpuScoring: HostManagerTestPlanFactory.CreateCpuScoring(topology), automaticPlacementTopology: topology);
        var allowed = RecordCpuLeaseBindings(second);
        await second.Coordinator.RunOnceAsync(default);
        Assert.Equal([1U], allowed[pid]);
        Assert.Equal("observed", (await second.Coordinator.ReadCpuLeaseAsync(id, default))!.State);
        second.Coordinator.DeleteCpuLease(id);
    }

    [Fact]
    public async Task RuntimeCoreLeaseReportsIntersectingLockConflictWithoutEmptyOrWidenedWrite()
    {
        const int pid = 53141;
        const ulong birth = 133900000000000141;
        var topology = CreateAutomaticPlacementTopology();
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(warm: true, scoreOnlyEnabled: false,
            processFactsSnapshot: CreateCompleteProcessFacts(pid, birth, "probe", 80, 20, 5),
            policyExecutionEnabled: false, automaticMemoryCleanupEnabled: false,
            optimizationCapabilities: OptimizationModeCapabilities.HardwarePlacement,
            cpuTopology: topology, cpuScoring: HostManagerTestPlanFactory.CreateCpuScoring(topology), automaticPlacementTopology: topology);
        var allowed = RecordCpuLeaseBindings(fixture);
        var first = fixture.Coordinator.CreateCpuLease(new(new(ProcessId: pid, ProcessStartKey: birth), ["core:0"], true, false), "holder1");
        var second = fixture.Coordinator.CreateCpuLease(new(new(ProcessId: pid, ProcessStartKey: birth), ["core:1"], true, false), "holder2");
        await fixture.Coordinator.RunOnceAsync(default);
        Assert.Equal([1U, 2U, 3U, 4U], allowed[pid]);
        foreach (var lease in new[] { first, second })
        {
            var effect = Assert.Single((await fixture.Coordinator.ReadCpuLeaseAsync(lease.LeaseId, default))!.Effects);
            Assert.Equal("failed", effect.State);
            Assert.Equal("cpu-lock-conflict", effect.ReasonCode);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RuntimeCoreLeaseReadbackDistinguishesAlreadyBoundFromDisjointOriginalRange(bool alreadyBound)
    {
        const int pid = 53151;
        const ulong birth = 133900000000000151;
        var topology = CreateAutomaticPlacementTopology();
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(warm: true, scoreOnlyEnabled: false,
            processFactsSnapshot: CreateCompleteProcessFacts(pid, birth, "probe", 80, 20, 5),
            policyExecutionEnabled: false, automaticMemoryCleanupEnabled: false,
            optimizationCapabilities: OptimizationModeCapabilities.HardwarePlacement,
            cpuTopology: topology, cpuScoring: HostManagerTestPlanFactory.CreateCpuScoring(topology), automaticPlacementTopology: topology);
        var allowed = RecordCpuLeaseBindings(fixture);
        allowed[pid] = alreadyBound ? [1U] : [2U];
        var lease = fixture.Coordinator.CreateCpuLease(new(new(ProcessId: pid, ProcessStartKey: birth), ["core:0"], true, false), "holder");
        await fixture.Coordinator.RunOnceAsync(default);
        var effect = Assert.Single((await fixture.Coordinator.ReadCpuLeaseAsync(lease.LeaseId, default))!.Effects);
        Assert.Equal(alreadyBound ? "observed" : "failed", effect.State);
        Assert.Equal(alreadyBound ? null : "cpu-placement-outside-original-range", effect.ReasonCode);
        Assert.Equal(alreadyBound ? new[] { 1U } : new[] { 2U }, allowed[pid]);
        Assert.Empty(fixture.StateStore.Current.AppliedPlacements);
    }

    [Fact]
    public async Task RuntimeCoreLeaseExclusiveOnlyKeepsOwnerRangeAndReportsAlreadyAvoidedPeer()
    {
        const int pid = 53161, peer = 53162;
        const ulong birth = 133900000000000161;
        var topology = CreateAutomaticPlacementTopology();
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(warm: true, scoreOnlyEnabled: false,
            processFactsSnapshot: CreateCompleteProcessFacts((pid, birth, "probe", 80d, 20d, 5d), (peer, birth + 1, "peer", 20d, 20d, 5d)),
            policyExecutionEnabled: false, automaticMemoryCleanupEnabled: false,
            optimizationCapabilities: OptimizationModeCapabilities.HardwarePlacement,
            cpuTopology: topology, cpuScoring: HostManagerTestPlanFactory.CreateCpuScoring(topology), automaticPlacementTopology: topology);
        var allowed = RecordCpuLeaseBindings(fixture);
        allowed[peer] = [2, 3, 4];
        var lease = fixture.Coordinator.CreateCpuLease(new(new(ProcessId: pid, ProcessStartKey: birth), ["core:0"], false, true), "holder");
        await fixture.Coordinator.RunOnceAsync(default);
        Assert.Equal([1U, 2U, 3U, 4U], allowed[pid]);
        var observed = (await fixture.Coordinator.ReadCpuLeaseAsync(lease.LeaseId, default))!;
        Assert.Equal("observed", observed.State);
        Assert.All(observed.Effects, effect => Assert.False(effect.ChangedByManager));
        Assert.Empty(fixture.StateStore.Current.AppliedPlacements);
    }

    [Fact]
    public async Task RuntimeCoreLeaseSharedReservationDoesNotClaimExclusiveEffect()
    {
        const int firstPid = 53171, secondPid = 53172;
        const ulong birth = 133900000000000171;
        var topology = CreateAutomaticPlacementTopology();
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(warm: true, scoreOnlyEnabled: false,
            processFactsSnapshot: CreateCompleteProcessFacts((firstPid, birth, "one", 80d, 20d, 5d), (secondPid, birth + 1, "two", 20d, 20d, 5d)),
            policyExecutionEnabled: false, automaticMemoryCleanupEnabled: false,
            optimizationCapabilities: OptimizationModeCapabilities.HardwarePlacement,
            cpuTopology: topology, cpuScoring: HostManagerTestPlanFactory.CreateCpuScoring(topology), automaticPlacementTopology: topology);
        RecordCpuLeaseBindings(fixture);
        var first = fixture.Coordinator.CreateCpuLease(new(new(ProcessId: firstPid, ProcessStartKey: birth), ["core:0"], true, true), "one");
        var second = fixture.Coordinator.CreateCpuLease(new(new(ProcessId: secondPid, ProcessStartKey: birth + 1), ["core:0"], true, true), "two");
        await fixture.Coordinator.RunOnceAsync(default);
        foreach (var lease in new[] { first, second })
        {
            var current = (await fixture.Coordinator.ReadCpuLeaseAsync(lease.LeaseId, default))!;
            Assert.Equal("failed", current.State);
            Assert.Contains(current.Effects, effect => effect.Role == "target" && effect.ReasonCode == "exclusive-reservation-shared-with-other-target");
        }
    }

    [Fact]
    public async Task RuntimeCoreLeaseOwnerIgnoresPublicationDelegateCapturedBeforeStop()
    {
        var residency = new PublicationControlledCpuResidencyReader();
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(warm: true,
            cpuCoreReader: residency, automaticMemoryCleanupEnabled: false);
        await fixture.Coordinator.StartAsync(default);
        await residency.WaitForSubscriptionAsync();
        var captured = ((Action<CompiledRuntimePlan>)fixture.RuntimePlanProvider.GetType()
            .GetField("Published", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(fixture.RuntimePlanProvider)!)
            .GetInvocationList().Cast<Action<CompiledRuntimePlan>>().Single(callback => ReferenceEquals(callback.Target, fixture.Coordinator));
        await fixture.Coordinator.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(10));
        var deadline = ReadPrivateField<HostManagerVersionedWakeDeadline>(fixture.Coordinator, "schedulingWakeDeadline");
        var before = deadline.Snapshot;
        captured(fixture.RuntimePlan);
        Assert.Equal(before, deadline.Snapshot);
    }
}
