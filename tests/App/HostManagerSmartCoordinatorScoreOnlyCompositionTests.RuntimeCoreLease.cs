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
            processFactsSnapshot: facts, automaticMemoryCleanupEnabled: false,
            optimizationMode: mode, optimizationCapabilities: OptimizationModeCapabilities.HardwarePlacement,
            cpuTopology: topology, cpuScoring: HostManagerTestPlanFactory.CreateCpuScoring(topology), automaticPlacementTopology: topology);
        var allowed = RecordCpuLeaseBindings(fixture);
        BeginCpuLeaseRunForManualCycles(fixture);
        allowed[pid] = [1, 2]; // An external original range must never be widened by apply or restore.
        var target = kind switch
        {
            "software" => new RuntimeCpuLeaseTarget(SoftwareId: "probe"),
            "process" => new RuntimeCpuLeaseTarget(ProcessId: pid, ProcessStartKey: birth),
            _ => new RuntimeCpuLeaseTarget(ExecutablePath: facts.Processes[0].ExecutablePath)
        };
        var leaseChanges = 0;
        fixture.CpuLeases.ActiveCpuLeasesChanged += () => leaseChanges++;
        Assert.False(fixture.CpuLeases.ActiveCpuLeasesExist);
        var lease = fixture.CpuLeases.CreateCpuLease(new(target, ["core:0"], true, true), "fixture-holder");
        Assert.Equal("pending", lease.State);
        Assert.True(fixture.CpuLeases.ActiveCpuLeasesExist);
        Assert.Equal(1, leaseChanges);
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
        var ended = fixture.CpuLeases.WaitForCpuLeaseEndAsync(lease.LeaseId, default);
        Assert.True(fixture.CpuLeases.DeleteCpuLease(lease.LeaseId));
        Assert.False(fixture.CpuLeases.ActiveCpuLeasesExist);
        Assert.Equal(2, leaseChanges);
        Assert.False(fixture.CpuLeases.DeleteCpuLease(lease.LeaseId));
        Assert.Equal(2, leaseChanges);
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
            automaticMemoryCleanupEnabled: false,
            optimizationCapabilities: OptimizationModeCapabilities.HardwarePlacement,
            cpuTopology: topology, cpuScoring: HostManagerTestPlanFactory.CreateCpuScoring(topology), automaticPlacementTopology: topology);
        RecordCpuLeaseBindings(fixture);
        BeginCpuLeaseRunForManualCycles(fixture);
        fixture.ProcessPolicyWriter.BatchHandler = requests => requests.Select(request => new ProcessResourcePolicyBatchWriteResult(
            request.ProcessId, [new ProcessResourcePolicyBatchFieldResult(ProcessResourcePolicyBatchFields.CpuSets, false, "access-denied") { ErrorCode = 5 }])).ToArray();
        var lease = fixture.CpuLeases.CreateCpuLease(new(new(ProcessId: pid, ProcessStartKey: birth), ["core:0"], true, false), "fixture-holder");
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
        Assert.True(fixture.CpuLeases.ActiveCpuLeasesExist);
    }

    [Fact]
    public async Task RuntimeCoreLeaseEndsOnExactInstanceExitButNotOnIncompleteInventory()
    {
        const int pid = 53121;
        const ulong birth = 133900000000000121;
        var topology = CreateAutomaticPlacementTopology();
        var facts = CreateCompleteProcessFacts(pid, birth, "probe", 80, 20, 5);
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(warm: true, scoreOnlyEnabled: false,
            processFactsSnapshot: facts, automaticMemoryCleanupEnabled: false,
            optimizationCapabilities: OptimizationModeCapabilities.HardwarePlacement,
            cpuTopology: topology, cpuScoring: HostManagerTestPlanFactory.CreateCpuScoring(topology), automaticPlacementTopology: topology);
        RecordCpuLeaseBindings(fixture);
        BeginCpuLeaseRunForManualCycles(fixture);
        var lease = fixture.CpuLeases.CreateCpuLease(new(new(ProcessId: pid, ProcessStartKey: birth), ["core:0"], true, false), "holder");
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
        var facts = CreateCompleteProcessFacts(pid, birth, "probe", 80, 20, 5);
        var residency = new PublicationControlledCpuResidencyReader();
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(warm: true, scoreOnlyEnabled: false,
            processFactsSnapshot: facts, automaticMemoryCleanupEnabled: false,
            optimizationCapabilities: OptimizationModeCapabilities.HardwarePlacement, cpuCoreReader: residency,
            cpuTopology: topology, cpuScoring: HostManagerTestPlanFactory.CreateCpuScoring(topology), automaticPlacementTopology: topology);
        var allowed = RecordCpuLeaseBindings(fixture);
        var id = fixture.CpuLeases.CreateCpuLease(new(new(ProcessId: pid, ProcessStartKey: birth), ["core:0"], true, false), "holder").LeaseId;
        AssertCpuLeaseStopped((await fixture.Coordinator.ReadCpuLeaseAsync(id, default))!);
        Assert.Equal("pending", fixture.CpuLeases.Get(id)!.Snapshot.State);

        await fixture.Coordinator.StartAsync(default);
        await residency.WaitForSubscriptionAsync();
        await fixture.Coordinator.RunOnceAsync(default);
        Assert.Equal([1U], allowed[pid]);
        Assert.Equal("observed", (await fixture.Coordinator.ReadCpuLeaseAsync(id, default))!.State);
        await fixture.Coordinator.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(10));
        AssertCpuLeaseStopped((await fixture.Coordinator.ReadCpuLeaseAsync(id, default))!);

        await fixture.Coordinator.StartAsync(default);
        await fixture.Coordinator.RunOnceAsync(default);
        Assert.Equal([1U], allowed[pid]);
        Assert.Equal("observed", (await fixture.Coordinator.ReadCpuLeaseAsync(id, default))!.State);
        fixture.CpuLeases.DeleteCpuLease(id);
        await fixture.Coordinator.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task RuntimeCoreLeaseStoppedReadCannotBeOverwrittenByDrainingForegroundCycle()
    {
        const int pid = 53132;
        const ulong birth = 133900000000000132;
        var topology = CreateAutomaticPlacementTopology();
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(warm: true, scoreOnlyEnabled: false,
            processFactsSnapshot: CreateCompleteProcessFacts(pid, birth, "probe", 80, 20, 5),
            automaticMemoryCleanupEnabled: false,
            optimizationCapabilities: OptimizationModeCapabilities.HardwarePlacement,
            cpuTopology: topology, cpuScoring: HostManagerTestPlanFactory.CreateCpuScoring(topology), automaticPlacementTopology: topology);
        RecordCpuLeaseBindings(fixture);
        BeginCpuLeaseRunForManualCycles(fixture);
        var id = fixture.CpuLeases.CreateCpuLease(new(new(ProcessId: pid, ProcessStartKey: birth), ["core:0"], true, false), "holder").LeaseId;
        var probe = new BlockingTransitionProbe(HostManagerSmartCoordinatorTransitionPoint.BeforeNativeCall);
        fixture.Coordinator.TransitionProbe = probe;
        var cycle = Task.Run(() => fixture.Coordinator.RunOnceAsync(default));
        await probe.Reached.WaitAsync(TimeSpan.FromSeconds(10));
        var stop = fixture.Coordinator.StopAsync(default);
        await probe.AdmissionClosed.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            Assert.False(stop.IsCompleted);
            AssertCpuLeaseStopped((await fixture.Coordinator.ReadCpuLeaseAsync(id, default))!);
        }
        finally { probe.Release(); }
        await cycle.WaitAsync(TimeSpan.FromSeconds(10));
        await stop.WaitAsync(TimeSpan.FromSeconds(10));
        // Prove the late foreground cycle wrote an observation, rather than merely testing an unchanged pending row.
        Assert.Equal("observed", fixture.CpuLeases.Get(id)!.Snapshot.State);
        AssertCpuLeaseStopped((await fixture.Coordinator.ReadCpuLeaseAsync(id, default))!);
    }

    private static void AssertCpuLeaseStopped(RuntimeCpuLeaseSnapshot snapshot)
    {
        Assert.Equal("unavailable", snapshot.State);
        Assert.Equal("scheduler-stopped", snapshot.ReasonCode);
    }

    private static void BeginCpuLeaseRunForManualCycles(ScoreOnlyCoordinatorFixture fixture)
    {
        // These fixture-owned runs invoke production cycles directly, without a competing background loop.
        // Lifecycle tests instead exercise the real StartAsync/StopAsync entry points.
        WritePrivateField(fixture.Coordinator, "lifecycleState", (int)HostManagerSmartCoordinatorLifecycleState.Running);
    }

    [Fact]
    public async Task RuntimeCoreLeaseReportsIntersectingLockConflictWithoutEmptyOrWidenedWrite()
    {
        const int pid = 53141;
        const ulong birth = 133900000000000141;
        var topology = CreateAutomaticPlacementTopology();
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(warm: true, scoreOnlyEnabled: false,
            processFactsSnapshot: CreateCompleteProcessFacts(pid, birth, "probe", 80, 20, 5),
            automaticMemoryCleanupEnabled: false,
            optimizationCapabilities: OptimizationModeCapabilities.HardwarePlacement,
            cpuTopology: topology, cpuScoring: HostManagerTestPlanFactory.CreateCpuScoring(topology), automaticPlacementTopology: topology);
        var allowed = RecordCpuLeaseBindings(fixture);
        BeginCpuLeaseRunForManualCycles(fixture);
        var first = fixture.CpuLeases.CreateCpuLease(new(new(ProcessId: pid, ProcessStartKey: birth), ["core:0"], true, false), "holder1");
        var second = fixture.CpuLeases.CreateCpuLease(new(new(ProcessId: pid, ProcessStartKey: birth), ["core:1"], true, false), "holder2");
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
            automaticMemoryCleanupEnabled: false,
            optimizationCapabilities: OptimizationModeCapabilities.HardwarePlacement,
            cpuTopology: topology, cpuScoring: HostManagerTestPlanFactory.CreateCpuScoring(topology), automaticPlacementTopology: topology);
        var allowed = RecordCpuLeaseBindings(fixture);
        BeginCpuLeaseRunForManualCycles(fixture);
        allowed[pid] = alreadyBound ? [1U] : [2U];
        var lease = fixture.CpuLeases.CreateCpuLease(new(new(ProcessId: pid, ProcessStartKey: birth), ["core:0"], true, false), "holder");
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
            automaticMemoryCleanupEnabled: false,
            optimizationCapabilities: OptimizationModeCapabilities.HardwarePlacement,
            cpuTopology: topology, cpuScoring: HostManagerTestPlanFactory.CreateCpuScoring(topology), automaticPlacementTopology: topology);
        var allowed = RecordCpuLeaseBindings(fixture);
        BeginCpuLeaseRunForManualCycles(fixture);
        allowed[peer] = [2, 3, 4];
        var lease = fixture.CpuLeases.CreateCpuLease(new(new(ProcessId: pid, ProcessStartKey: birth), ["core:0"], false, true), "holder");
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
            automaticMemoryCleanupEnabled: false,
            optimizationCapabilities: OptimizationModeCapabilities.HardwarePlacement,
            cpuTopology: topology, cpuScoring: HostManagerTestPlanFactory.CreateCpuScoring(topology), automaticPlacementTopology: topology);
        RecordCpuLeaseBindings(fixture);
        BeginCpuLeaseRunForManualCycles(fixture);
        var first = fixture.CpuLeases.CreateCpuLease(new(new(ProcessId: firstPid, ProcessStartKey: birth), ["core:0"], true, true), "one");
        var second = fixture.CpuLeases.CreateCpuLease(new(new(ProcessId: secondPid, ProcessStartKey: birth + 1), ["core:0"], true, true), "two");
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
