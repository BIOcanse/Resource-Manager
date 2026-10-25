using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Domain.CpuTopology;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Domain.Settings;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Optimization;

namespace Resource_Manager_APP.Tests;

public sealed partial class HostManagerSmartCoordinatorScoreOnlyCompositionTests
{
    [Fact]
    public async Task LiveAffinityReceiptWithUnreadablePathIsNotSettledAsAnExitedThread()
    {
        const int pid = 42_921;
        var startedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        var start = checked((ulong)startedAt.ToFileTime());
        var record = new HostManagerAppliedRecord(HostManagerAppliedRecordKinds.CpuAffinity, "live-affinity",
            new Dictionary<string, string>
            {
                ["owner"] = "host-manager-automatic-placement-v1", ["processId"] = pid.ToString(),
                ["processStartKey"] = start.ToString(), ["processName"] = "fixture",
                ["executablePath"] = @"C:\tests\fixture.exe", ["previousAffinityMask"] = "0x1",
                ["appliedAffinityMask"] = "0x2"
            });
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(warm: true, scoreOnlyEnabled: false);
        var placement = new HostManagerAppliedPlacementReceipt(HostManagerTargetIdentity.CreateProcessTargetId(pid, start), "software:live-process", "cpu", [record], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        fixture.StateStore.Current = fixture.StateStore.Current with { AppliedPlacements = [placement] };
        fixture.ProcessPolicyWriter.PlacementReadHandler = (_, _) =>
            RecoveryReadResult<ProcessPlacementRecoverySnapshot>.Found(new(pid, startedAt, "fixture", null, 2, null, null, null));
        var admission = HostManagerCycleEffectAdmission.Create(false);
        admission.InitializeBudgets(1, 1);
        Assert.True(admission.TryAcquire(HostManagerCycleEffectKind.NativeActionTransaction, out var permit));
        Assert.True(permit.TryReserveOwnedNativeRestore(out var priorRestore));
        using (priorRestore) priorRestore!.EnterPointOfNoReturn();
        var task = (Task<bool>)typeof(HostManagerSmartCoordinator).GetMethod("RunPreparedAutomaticPlacementCycleAsync",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(fixture.Coordinator,
                [admission, fixture.StateStore.Current, Array.Empty<HostManagerPlacementDesired>(), new[] { placement }, CancellationToken.None,
                    fixture.ProcessPolicyWriter.CreateThreadIdealProcessorReadCycle()])!;
        Assert.True(await task);
        Assert.Equal(record, Assert.Single(Assert.Single(fixture.StateStore.Current.AppliedPlacements).Records));
        Assert.Equal(1U, admission.NewPointOfNoReturnRemaining);
        Assert.Equal(0U, admission.RecoveryRemaining);
    }

    [Fact]
    public async Task ExitedThreadReceiptsSettleTogetherWithoutSpendingActionBudget()
    {
        const int pid = 42_920;
        var start = checked((ulong)DateTimeOffset.UtcNow.AddMinutes(-5).ToFileTime());
        var records = Enumerable.Range(1, 8).Select(i => new HostManagerAppliedRecord(
            HostManagerAppliedRecordKinds.CpuThreadIdealProcessor, $"exited-thread-{i}", new Dictionary<string, string>
            {
                ["owner"] = "host-manager-automatic-placement-v1", ["processId"] = pid.ToString(),
                ["processStartKey"] = start.ToString(), ["threadId"] = (10000 + i).ToString(),
                ["threadStartKey"] = (start + (ulong)i).ToString(),
                ["previousProcessorGroup"] = "0", ["previousProcessorNumber"] = "0",
                ["appliedProcessorGroup"] = "0", ["appliedProcessorNumber"] = "1"
            })).ToArray();
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(warm: true, scoreOnlyEnabled: false);
        var placement = new HostManagerAppliedPlacementReceipt(HostManagerTargetIdentity.CreateProcessTargetId(pid, start), "software:exited-game", "cpu", records, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        fixture.StateStore.Current = fixture.StateStore.Current with { AppliedPlacements = [placement] };
        fixture.ProcessPolicyWriter.IdealReadHandler = (_, _, _) =>
            RecoveryReadResult<ThreadIdealProcessorPolicySnapshot>.NotFoundOrExited(87, "thread exited");
        fixture.ProcessPolicyWriter.IdealWriteHandler = (_, _) => throw new InvalidOperationException("Exited thread needs no restore write.");
        var admission = HostManagerCycleEffectAdmission.Create(false);
        admission.InitializeBudgets(1, 1);
        var task = (Task<bool>)typeof(HostManagerSmartCoordinator).GetMethod("RunPreparedAutomaticPlacementCycleAsync",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(fixture.Coordinator,
                [admission, fixture.StateStore.Current, Array.Empty<HostManagerPlacementDesired>(), new[] { placement }, CancellationToken.None,
                    fixture.ProcessPolicyWriter.CreateThreadIdealProcessorReadCycle()])!;
        Assert.True(await task);
        Assert.Empty(fixture.StateStore.Current.AppliedPlacements);
        Assert.Equal(1U, admission.NewPointOfNoReturnRemaining);
        Assert.Equal(1U, admission.RecoveryRemaining);
        Assert.Equal(0, fixture.ProcessPolicyWriter.IdealProcessorWriteCalls);
    }

    [Fact]
    public async Task IdleMemoryTransactionsProgressWhileForegroundGameKeepsItsPlacement()
    {
        const int gamePid = 42_910;
        var startedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        var start = checked((ulong)startedAt.ToFileTime());
        var inputs = Enumerable.Range(0, 5).Select(i => (gamePid + i, start,
            i == 0 ? "software:foreground-game" : $"software:idle-memory:{i}", i == 0 ? 100d : 35d,
            i == 0 ? 95d : 0d, 5d)).ToArray();
        var facts = CreateCompleteProcessFacts(inputs);
        facts = facts with { Processes = facts.Processes.Select(p => p.ProcessId == gamePid
            ? p with { RuntimeState = HostManagerRuntimeStates.ForegroundFocused,
                HasVisibleWindow = true, ForegroundFocused = true, SoftwareKind = "game" }
            : p).ToArray() };
        var topology = CreateAutomaticPlacementTopology();
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(warm: true,
            scoreOnlyEnabled: false, processFactsSnapshot: facts, memoryModePolicyEnabled: true,
            automaticMemoryCleanupEnabled: false, cpuUsagePercent: 100,
            processRecoveryRead: pid => RecoveryReadResult<ProcessInstanceRecoverySnapshot>.Found(new(pid, startedAt)),
            cpuCoreReader: new RecordingCpuCoreResidencyReader(() => CpuCoreResidencyTestValues.Create(95,
                DateTimeOffset.UtcNow, CpuCoreResidencyTestValues.Process(gamePid, start, ("core:0", 95)))),
            cpuTopology: topology, cpuScoring: HostManagerTestPlanFactory.CreateCpuScoring(topology),
            automaticPlacementTopology: topology,
            realtimeActionBudget: 1);
        fixture.MetricSampler.SetSnapshot(CreateHardwareSnapshot(memoryUsagePercent: 100, cpuUsagePercent: 100));
        var states = inputs.ToDictionary(x => x.Item1, x => new ProcessResourcePolicySnapshot(x.Item1,
            "fixture", @"C:\tests\fixture.exe", startedAt, "Normal", 1, 1, MemoryPriority: 5,
            PowerThrottlingControlMask: 0, PowerThrottlingStateMask: 0));
        fixture.ProcessPolicyWriter.ProcessReadHandler = pid => states[pid];
        var memoryWrites = new List<int>();
        var policyWrites = 0;
        fixture.ProcessPolicyWriter.BatchHandler = requests => requests.Select(request =>
        {
            var state = states[request.ProcessId];
            var fields = new List<ProcessResourcePolicyBatchFieldResult>();
            if (request.MemoryPriority is uint memory)
            {
                Assert.NotEqual(gamePid, request.ProcessId);
                state = state with { MemoryPriority = memory };
                fields.Add(new(ProcessResourcePolicyBatchFields.MemoryPriority, true, "memory applied"));
                memoryWrites.Add(request.ProcessId);
            }
            if (request.PriorityClass is { } priority)
            {
                state = state with { PriorityClass = priority };
                fields.Add(new(ProcessResourcePolicyBatchFields.PriorityClass, true, "priority applied"));
                policyWrites++;
            }
            if (request.PowerControlMask is uint control)
            {
                state = state with { PowerThrottlingControlMask = control, PowerThrottlingStateMask = request.PowerStateMask };
                fields.Add(new(ProcessResourcePolicyBatchFields.PowerThrottling, true, "power applied"));
            }
            states[request.ProcessId] = state;
            return new ProcessResourcePolicyBatchWriteResult(request.ProcessId, fields);
        }).ToArray();
        var threads = new Dictionary<int, ThreadIdealProcessorPolicySnapshot>
        {
            [10001] = new(gamePid, start, 10001, start + 1, new(0, 0), 0, 15, []),
            [10002] = new(gamePid, start, 10002, start + 2, new(0, 0), 0, 15, [])
        };
        fixture.ProcessPolicyWriter.IdealThreadsReadHandler = (pid, _, _) =>
            RecoveryReadResult<IReadOnlyList<ThreadIdealProcessorPolicySnapshot>>.Found(
                pid == gamePid ? threads.Values.ToArray() : []);
        fixture.ProcessPolicyWriter.PlacementReadHandler = (pid, _) =>
            RecoveryReadResult<ProcessPlacementRecoverySnapshot>.Found(new(pid, startedAt,
                "fixture", @"C:\tests\fixture.exe", null, [1, 2, 3, 4], null, null));
        fixture.ProcessPolicyWriter.IdealReadHandler = (_, _, tid) =>
            RecoveryReadResult<ThreadIdealProcessorPolicySnapshot>.Found(threads[tid]);
        fixture.ProcessPolicyWriter.IdealWriteHandler = (expected, desired) =>
        {
            Assert.Equal(expected, threads[expected.ThreadId]);
            threads[expected.ThreadId] = expected with { IdealProcessor = desired };
            return new(true, "game hint applied");
        };
        var sawMemoryAfterPlacement = false;
        var idleScoredRounds = 0;
        var idleDemotionRounds = 0;
        var idleMemoryDirectiveRounds = 0;
        for (var cycle = 0; cycle < 12; cycle++)
        {
            var memoryBefore = memoryWrites.Count;
            var actionBefore = memoryWrites.Count + policyWrites + fixture.ProcessPolicyWriter.IdealProcessorWriteCalls;
            var hadPlacement = fixture.StateStore.Current.AppliedPlacements.Count != 0;
            fixture.PublishNextHostedCpuObservation();
            await fixture.RunRealtimeCycleAsync();
            var authority = fixture.Coordinator.SchedulingAuthority;
            Assert.True(authority.Availability == HostManagerSchedulingAuthorityAvailability.Ready, authority.UnavailableReason);
            var scores = authority.Compute!.Cpu!.Scores.Where(s => s.Kind == NativeComputeScoringOutputKind.ProcessCpu).ToArray();
            if (scores.Length == 5 && scores.Where(s => s.ProcessId != gamePid).All(s => s.Score == 0)) idleScoredRounds++;
            if (fixture.Workspace.CurrentSnapshotRows.ToArray().Any(row =>
                row.RowKind == NativeSmartCoordinatorSnapshotRowKind.Process && row.ProcessId != gamePid
                && row.DesiredProcessGrade != NativeSmartCoordinatorProcessGrade.Normal)) idleDemotionRounds++;
            if (fixture.Coordinator.NonAdaptedMemoryModeProjection!.Processes.Any(
                p => p.ProcessId != gamePid && p.Action == HostManagerNonAdaptedMemoryProcessAction.Optimize)) idleMemoryDirectiveRounds++;
            Assert.InRange(memoryWrites.Count + policyWrites + fixture.ProcessPolicyWriter.IdealProcessorWriteCalls - actionBefore, 0, 1);
            sawMemoryAfterPlacement |= hadPlacement && memoryWrites.Count > memoryBefore;
        }
        Assert.True(fixture.ProcessPolicyWriter.IdealProcessorWriteCalls > 0);
        Assert.True(sawMemoryAfterPlacement, $"No fresh memory transaction after placement; memory writes={memoryWrites.Count}.");
        Assert.All(states.Where(p => p.Key != gamePid), p => Assert.Equal(3U, p.Value.MemoryPriority));
        Assert.Equal(12, idleScoredRounds);
        Assert.Equal(12, idleDemotionRounds);
        Assert.Equal(12, idleMemoryDirectiveRounds);
        Assert.True(policyWrites > 0, "Idle processes never progressed from scoring to CPU demotion.");
        var owners = await fixture.NativeTransactions.AppliedOwnership.ReadSnapshotAsync(CancellationToken.None);
        Assert.Equal(4, owners.Records.Count(r => r.OriginalBinding.DomainMask == (uint)NativeTransactionJournalDomain.PhysicalMemory));
        Assert.NotEmpty(fixture.StateStore.Current.AppliedPlacements);
    }
}
