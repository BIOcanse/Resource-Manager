using System.Reflection;
using System.Text.Json;
using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Domain.Settings;
using ResourceManager.App.Infrastructure.GpuPlacement;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Optimization;
using static Resource_Manager_APP.Tests.GpuWindowLedgerTestData;
using static Resource_Manager_APP.Tests.GpuWindowActionExitRecordTests;

namespace Resource_Manager_APP.Tests;

public sealed partial class HostManagerSmartCoordinatorScoreOnlyCompositionTests
{
    internal static Task<bool> ReconcileWindowExit(HostManagerSmartCoordinator coordinator,
        HostManagerCycleEffectAdmission admission, CancellationToken token = default)
        => (Task<bool>)typeof(HostManagerSmartCoordinator).GetMethod("ReconcileExitedGpuWindowActionsAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(coordinator, [admission, token])!;

    private static HostManagerCycleEffectAdmission ExitAdmission(bool scoreOnly = false, uint count = 2)
    {
        var admission = HostManagerCycleEffectAdmission.Create(scoreOnly);
        admission.InitializeBudgets(1, Math.Max(1, count));
        if (count == 0 && !scoreOnly)
        {
            Assert.True(admission.TryAcquire(HostManagerCycleEffectKind.ExitedOwnershipReconciliation, out var permit));
            Assert.True(permit.TryReserveRecovery(1, out _));
        }
        return admission;
    }

    [Theory]
    [InlineData("exited", 2, 1)]
    [InlineData("target-live", 1, 0)]
    [InlineData("worker-live", 2, 0)]
    [InlineData("target-unavailable", 1, 0)]
    [InlineData("worker-unavailable", 2, 0)]
    [InlineData("score-only", 0, 0)]
    [InlineData("zero-budget", 0, 0)]
    public async Task WindowExitOwnerUsesOnlyRecordedIdentitiesAndExistingRecoveryAdmission(string scenario, int expectedReads, int expectedCommits)
    {
        var prepared = Prepared();
        var result = Unknown(prepared);
        var fact = GpuWindowActionRecord.Create(prepared, result);
        var path = Path.Combine(NewRoot("exit-" + scenario), "recovery.json");
        var committer = new ControlledCommitter();
        using var store = new JsonHostManagerRollbackStateStore(path, new FixedTime(), TimeSpan.FromHours(1),
            committer, WindowsHostManagerDurableRootManifestCommitter.Instance);
        var requested = new List<int>();
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(warm: true,
            rollbackStateStoreOverride: store, automaticMemoryCleanupEnabled: false, processRecoveryRead: pid =>
            {
                requested.Add(pid);
                var target = pid == prepared.Window.Request.ProcessId;
                Assert.True(target || pid == prepared.Worker.ProcessId);
                var label = target ? "target" : "worker";
                if (scenario == label + "-unavailable") return RecoveryReadResult<ProcessInstanceRecoverySnapshot>.Unavailable(5, "Denied.");
                return scenario == label + "-live" ? Live(pid, target ? prepared.Window.Request.CreationFileTimeUtc : prepared.Worker.CreationFileTimeUtc) : Gone();
            });
        var state = (await store.LoadAsync(CancellationToken.None)) with { AppliedPlacements = [Placement(fact)] };
        await store.SaveAsync(state, CancellationToken.None);
        var before = committer.Calls;
        var admission = ExitAdmission(scenario == "score-only", scenario == "zero-budget" ? 0U : 2U);
        Assert.True(await ReconcileWindowExit(fixture.Coordinator, admission));
        Assert.Equal(expectedReads, requested.Count);
        Assert.Equal(expectedCommits, committer.Calls - before);
        Assert.Equal(expectedCommits == 0, GpuActionFacts.HasUnsettledActions(ReadCanonical(path).AppliedPlacements));
        var saved = Assert.Single(ReadCanonical(path).AppliedPlacements[0].Records);
        Assert.True(GpuWindowActionRecord.TryRead(saved, out var decoded));
        Assert.Equal(result, decoded.Result);
        Assert.Equal(prepared, decoded.Prepared);
        output.WriteLine("windowExitOwner=" + JsonSerializer.Serialize(new { path, scenario, requested, fact = decoded,
            commits = committer.Calls - before, canonicalReadBeforeDispose = true }));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WindowExitOwnerRetainsOneSaveThroughCancellationAndCommitFailure(bool fail)
    {
        var path = Path.Combine(NewRoot("exit-save"), "recovery.json");
        using var committer = new PausedWindowCommitter();
        using var store = new JsonHostManagerRollbackStateStore(path, new FixedTime(), TimeSpan.FromHours(1),
            committer, WindowsHostManagerDurableRootManifestCommitter.Instance);
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(warm: true,
            rollbackStateStoreOverride: store, automaticMemoryCleanupEnabled: false, processRecoveryRead: _ => Gone());
        var original = GpuWindowActionRecord.Create(Prepared(), Unknown(Prepared()));
        var state = (await store.LoadAsync(CancellationToken.None)) with { AppliedPlacements = [Placement(original)] };
        await store.SaveAsync(state, CancellationToken.None);
        var before = committer.Calls;
        if (fail) committer.FailNext = HostManagerRollbackStateCommitOutcome.NotCommitted;
        committer.PauseNext();
        using var cancel = new CancellationTokenSource();
        var reconcile = ReconcileWindowExit(fixture.Coordinator, ExitAdmission(), cancel.Token);
        try
        {
            await committer.Entered.WaitAsync(TimeSpan.FromSeconds(5));
            var saving = CurrentWindowTask(fixture.Coordinator);
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reconcile);
            Assert.False(saving.IsCompleted);
            Assert.False(await SettleWindowCheckpoint(fixture.Coordinator));
            await Assert.ThrowsAsync<InvalidOperationException>(() => ReconcileWindowExit(fixture.Coordinator, ExitAdmission()));
            Assert.Equal(before + 1, committer.Calls);
            Assert.True(GpuActionFacts.HasUnsettledActions(ReadCanonical(path).AppliedPlacements));
            committer.Release();
            if (fail) await Assert.ThrowsAsync<HostManagerRollbackStateCommitException>(() => saving);
            else await saving.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(!fail, await SettleWindowCheckpoint(fixture.Coordinator));
            Assert.Equal(before + 1, committer.Calls);
            Assert.Equal(fail, GpuActionFacts.HasUnsettledActions(ReadCanonical(path).AppliedPlacements));
            output.WriteLine("windowExitSave=" + JsonSerializer.Serialize(new { path, fail, commits = committer.Calls - before,
                sameTask = true, canonical = ReadCanonical(path), canonicalReadBeforeDispose = true }));
        }
        finally { committer.Release(); }
    }

    [Fact]
    public async Task WindowExitOriginalNormalCycleSettlesBeforeFinalOwnershipCheck()
    {
        var path = Path.Combine(NewRoot("exit-normal-cycle"), "recovery.json");
        using var store = new JsonHostManagerRollbackStateStore(path, new FixedTime(), TimeSpan.FromHours(1));
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(warm: true,
            rollbackStateStoreOverride: store, scoreOnlyEnabled: false, optimizationMode: AppOptimizationModes.Normal,
            automaticMemoryCleanupEnabled: false, processRecoveryRead: _ => Gone());
        var state = (await store.ReserveNativeHostSessionIncarnationAsync(CancellationToken.None)) with
        { AppliedPlacements = [Placement(GpuWindowActionRecord.Create(Prepared(), Unknown(Prepared())))] };
        await store.SaveAsync(state, CancellationToken.None);
        await fixture.RunRealtimeCycleAsync();
        Assert.False(GpuActionFacts.HasUnsettledActions(ReadCanonical(path).AppliedPlacements));
        Assert.Equal(2, fixture.ProcessPolicyWriter.RecoveryReadCalls);
        var check = (Task)typeof(HostManagerSmartCoordinator).GetMethod("RequireNoAppliedOwnershipAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(fixture.Coordinator, [CancellationToken.None])!;
        await check;
        output.WriteLine("windowExitNormalCycle=" + JsonSerializer.Serialize(new { path,
            fixture.ProcessPolicyWriter.RecoveryReadCalls, canonical = ReadCanonical(path), canonicalReadBeforeDispose = true }));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WindowExitOwnerWriteBudgetDoesNotStarveLaterExitedRecords(bool unreadablePrefix)
    {
        var first = Prepared();
        var second = first with { Window = first.Window with { Request = first.Window.Request with
        { ProcessId = first.Window.Request.ProcessId + 1 } } };
        var last = first with { Window = first.Window with { Request = first.Window.Request with
        { ProcessId = first.Window.Request.ProcessId + 2 } } };
        var facts = new[] { first, second, last }.Select(value => GpuWindowActionRecord.Create(value, Unknown(value))).ToArray();
        var requested = new List<int>();
        var path = Path.Combine(NewRoot("exit-prefix"), "recovery.json");
        var committer = new ControlledCommitter();
        using var store = new JsonHostManagerRollbackStateStore(path, new FixedTime(), TimeSpan.FromHours(1),
            committer, WindowsHostManagerDurableRootManifestCommitter.Instance);
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(warm: true,
            rollbackStateStoreOverride: store, automaticMemoryCleanupEnabled: false, processRecoveryRead: pid =>
            {
                requested.Add(pid);
                if (pid == last.Window.Request.ProcessId || pid == last.Worker.ProcessId) return Gone();
                return unreadablePrefix ? RecoveryReadResult<ProcessInstanceRecoverySnapshot>.Unavailable(5, "Denied.")
                    : Live(pid, first.Window.Request.CreationFileTimeUtc);
            });
        var state = (await store.LoadAsync(CancellationToken.None)) with { AppliedPlacements = [Placement(facts)] };
        await store.SaveAsync(state, CancellationToken.None);
        var before = committer.Calls;
        var admission = ExitAdmission(count: 1);
        Assert.True(await ReconcileWindowExit(fixture.Coordinator, admission));
        Assert.Equal(new[] { first.Window.Request.ProcessId, second.Window.Request.ProcessId, last.Window.Request.ProcessId, last.Worker.ProcessId }, requested);
        Assert.Equal(0U, admission.RecoveryRemaining);
        Assert.Equal(before + 1, committer.Calls);
        var records = ReadCanonical(path).AppliedPlacements[0].Records;
        Assert.Equal(facts[0].Metadata!["windowActionPayload"], records[0].Metadata!["windowActionPayload"]);
        Assert.Equal(facts[1].Metadata!["windowActionPayload"], records[1].Metadata!["windowActionPayload"]);
        Assert.True(GpuWindowActionRecord.TryRead(records[2], out var settled));
        Assert.NotNull(settled.ExitSettlement);
        Assert.Equal(Unknown(last), settled.Result);
        output.WriteLine("windowExitPrefix=" + JsonSerializer.Serialize(new { path, unreadablePrefix, requested,
            commits = committer.Calls - before, canonical = ReadCanonical(path), canonicalReadBeforeDispose = true }));
    }
}
