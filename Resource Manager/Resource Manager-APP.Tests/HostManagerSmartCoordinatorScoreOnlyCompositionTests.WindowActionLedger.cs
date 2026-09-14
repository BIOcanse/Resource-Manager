using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Domain.Settings;
using ResourceManager.App.Infrastructure.GpuPlacement;
using ResourceManager.App.Infrastructure.GpuPlacement.WindowExecution;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Optimization;
using static Resource_Manager_APP.Tests.GpuWindowLedgerTestData;

namespace Resource_Manager_APP.Tests;

public sealed partial class HostManagerSmartCoordinatorScoreOnlyCompositionTests
{
    internal static Task<HostManagerRollbackStateDocument> InvokeWindowOwner(
        HostManagerSmartCoordinator coordinator, string method, params object[] arguments)
    {
        try
        {
            return Assert.IsAssignableFrom<Task<HostManagerRollbackStateDocument>>(typeof(HostManagerSmartCoordinator)
                .GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(coordinator, arguments));
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            return Task.FromException<HostManagerRollbackStateDocument>(exception.InnerException);
        }
    }

    [Fact]
    public async Task WindowLedgerOwnerImmediatelyPersistsPreparationAndActualResultAtTheSameStoreTime()
    {
        var path = Path.Combine(NewRoot("owner-roundtrip"), "recovery.json");
        var prepared = Prepared();
        var policy = Policy(prepared);
        using (var store = new JsonHostManagerRollbackStateStore(path, new FixedTime(), TimeSpan.FromHours(1)))
        {
            await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(warm: true,
                rollbackStateStoreOverride: store, automaticMemoryCleanupEnabled: false);
            var state = await store.ReserveNativeHostSessionIncarnationAsync(CancellationToken.None);
            state = state with { AppliedPlacements = [Placement(policy)] };
            await store.SaveAsync(state, CancellationToken.None);
            var key = HostManagerPlacementReceiptKey.Create(state.AppliedPlacements[0]);
            var pending = await InvokeWindowOwner(fixture.Coordinator, "SaveGpuWindowPreparationAsync",
                state, key, prepared, CancellationToken.None);
            var disk = ReadCanonical(path);
            var fact = Assert.Single(disk.AppliedPlacements[0].Records, GpuWindowActionRecord.IsActionFact);
            Assert.True(GpuWindowActionRecord.TryRead(fact, out var decoded));
            Assert.Null(decoded.Result);
            Assert.Single(state.AppliedPlacements[0].Records);
            Assert.Equal(2, pending.AppliedPlacements[0].Records.Count);
            await Assert.ThrowsAsync<InvalidOperationException>(() => InvokeWindowOwner(fixture.Coordinator,
                "SaveGpuWindowPreparationAsync", pending, key, prepared with
                { Worker = prepared.Worker with { ProcessId = 30000 } }, CancellationToken.None));

            var final = await InvokeWindowOwner(fixture.Coordinator, "SaveGpuWindowResultAsync",
                key, fact.RecordId, Unknown(prepared), CancellationToken.None);
            var finalFact = Assert.Single(ReadCanonical(path).AppliedPlacements[0].Records, GpuWindowActionRecord.IsActionFact);
            Assert.True(GpuWindowActionRecord.TryRead(finalFact, out var saved));
            Assert.Equal(Unknown(prepared), saved.Result);
            Assert.True(GpuWindowActionRecord.TryRead(fact, out var stillPending));
            Assert.Null(stillPending.Result);
            Assert.True(GpuActionFacts.HasUnsettledActions(final.AppliedPlacements));
            Assert.Empty(fixture.StateStore.Current.AppliedPlacements);
            output.WriteLine("windowLedger=" + JsonSerializer.Serialize(new { path, pending = fact, final = finalFact, canonicalReadBeforeDispose = true }));
        }
        using var reopened = new JsonHostManagerRollbackStateStore(path, new FixedTime(), TimeSpan.FromHours(1));
        var loaded = await reopened.LoadAsync(CancellationToken.None);
        Assert.True(GpuWindowActionRecord.BlocksProcess(loaded.AppliedPlacements, "changed-adapter",
            prepared.Window.Request.ProcessId, (ulong)prepared.Window.Request.CreationFileTimeUtc));
        Assert.False(GpuWindowActionRecord.BlocksProcess(loaded.AppliedPlacements, loaded.AppliedPlacements[0].TargetId,
            prepared.Window.Request.ProcessId, (ulong)prepared.Window.Request.CreationFileTimeUtc + 1));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task WindowLedgerFailedOwnerSavePreservesTheActualCanonicalAuthority(bool finalWrite, bool ambiguous)
    {
        var path = Path.Combine(NewRoot("owner-commit-failure"), "recovery.json");
        var prepared = Prepared();
        var committer = new ControlledCommitter();
        using (var store = new JsonHostManagerRollbackStateStore(path, new FixedTime(), TimeSpan.FromHours(1),
                   committer, WindowsHostManagerDurableRootManifestCommitter.Instance))
        {
            await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(warm: true,
                rollbackStateStoreOverride: store, automaticMemoryCleanupEnabled: false);
            var state = await store.ReserveNativeHostSessionIncarnationAsync(CancellationToken.None);
            state = state with { AppliedPlacements = [Placement(Policy(prepared))] };
            await store.SaveAsync(state, CancellationToken.None);
            var key = HostManagerPlacementReceiptKey.Create(state.AppliedPlacements[0]);
            if (finalWrite) state = await InvokeWindowOwner(fixture.Coordinator, "SaveGpuWindowPreparationAsync",
                state, key, prepared, CancellationToken.None);
            var before = File.ReadAllBytes(path);
            var calls = committer.Calls;
            committer.FailNext = ambiguous ? HostManagerRollbackStateCommitOutcome.CommitAmbiguous : HostManagerRollbackStateCommitOutcome.NotCommitted;
            var error = await Assert.ThrowsAsync<HostManagerRollbackStateCommitException>(() => finalWrite
                ? InvokeWindowOwner(fixture.Coordinator, "SaveGpuWindowResultAsync", key,
                    GpuWindowActionRecord.Create(prepared).RecordId, Restored(prepared), CancellationToken.None)
                : InvokeWindowOwner(fixture.Coordinator, "SaveGpuWindowPreparationAsync", state, key, prepared, CancellationToken.None));
            Assert.Equal(ambiguous ? HostManagerRollbackStateCommitOutcome.CommitAmbiguous : HostManagerRollbackStateCommitOutcome.NotCommitted, error.Outcome);
            Assert.Equal(calls + 1, committer.Calls);
            if (!ambiguous) Assert.Equal(before, File.ReadAllBytes(path));
            var disk = ReadCanonical(path);
            Assert.Equal(finalWrite || ambiguous, GpuActionFacts.HasUnsettledActions(disk.AppliedPlacements));
            foreach (var fact in disk.AppliedPlacements.SelectMany(item => item.Records).Where(GpuWindowActionRecord.IsActionFact))
            {
                Assert.True(GpuWindowActionRecord.TryRead(fact, out var decoded));
                Assert.Null(decoded.Result);
            }
            output.WriteLine("windowLedgerFailure=" + JsonSerializer.Serialize(new { path, finalWrite, ambiguous, outcome = error.Outcome, canonicalReadBeforeDispose = true }));
        }
        using var reopened = new JsonHostManagerRollbackStateStore(path, new FixedTime(), TimeSpan.FromHours(1));
        Assert.Equal(finalWrite || ambiguous,
            GpuActionFacts.HasUnsettledActions((await reopened.LoadAsync(CancellationToken.None)).AppliedPlacements));
    }

    [Theory]
    [InlineData("pid")]
    [InlineData("birth")]
    [InlineData("resource")]
    [InlineData("missing-policy")]
    [InlineData("invalid-policy")]
    public async Task WindowLedgerPreparationRequiresTheOriginalExactGpuPolicyOwner(string mismatch)
    {
        var path = Path.Combine(NewRoot("owner-mismatch"), "recovery.json");
        using var store = new JsonHostManagerRollbackStateStore(path, new FixedTime(), TimeSpan.FromHours(1));
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(warm: true,
            rollbackStateStoreOverride: store, automaticMemoryCleanupEnabled: false);
        var prepared = Prepared();
        var policy = Policy(prepared);
        if (mismatch is "pid" or "birth") policy = policy with
        { Metadata = new Dictionary<string, string>(policy.Metadata!) { [mismatch == "pid" ? "processId" : "processStartKey"] = "555" } };
        if (mismatch == "invalid-policy") policy = policy with
        { Metadata = new Dictionary<string, string>(policy.Metadata!) { ["appliedValue"] = "!" } };
        var placement = Placement(policy);
        if (mismatch == "resource") placement = placement with { ResourceKind = OptimizationResourceKinds.Cpu };
        if (mismatch == "missing-policy") placement = placement with { Records = [new("other", "other")] };
        var state = (await store.ReserveNativeHostSessionIncarnationAsync(CancellationToken.None)) with { AppliedPlacements = [placement] };
        await store.SaveAsync(state, CancellationToken.None);
        var before = File.ReadAllBytes(path);
        await Assert.ThrowsAsync<InvalidOperationException>(() => InvokeWindowOwner(fixture.Coordinator,
            "SaveGpuWindowPreparationAsync", state, HostManagerPlacementReceiptKey.Create(placement), prepared, CancellationToken.None));
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WindowLedgerNormalRecoveryRestoresPolicyButRetainsItsIndependentActionFact(bool completed)
    {
        var path = Path.Combine(NewRoot("normal-policy-recovery"), "recovery.json");
        var prepared = Prepared();
        var fact = GpuWindowActionRecord.Create(prepared, completed ? Restored(prepared) : null);
        RecoveryReadResult<ProcessInstanceRecoverySnapshot> ReadRetainedTarget(int processId)
        {
            Assert.Equal(prepared.Window.Request.ProcessId, processId);
            return RecoveryReadResult<ProcessInstanceRecoverySnapshot>.Found(
                new(processId, DateTimeOffset.FromFileTime(prepared.Window.Request.CreationFileTimeUtc)));
        }
        using (var store = new JsonHostManagerRollbackStateStore(path, TimeProvider.System, TimeSpan.FromMinutes(1)))
        {
            await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(warm: true,
                scoreOnlyEnabled: false, policyExecutionEnabled: false, automaticMemoryCleanupEnabled: false,
                optimizationMode: AppOptimizationModes.Normal, rollbackStateStoreOverride: store,
                processRecoveryRead: ReadRetainedTarget);
            var runtime = new D3d11ProxyShimRuntime(new GpuPolicyEnvironment(fixture.Root));
            Assert.True(runtime.TryWritePolicy("window-recovery", null, [1, 3]));
            var policy = Assert.IsType<HostManagerAppliedRecord>(runtime.CapturePolicyRecord("window-recovery", [2]));
            var state = await store.ReserveNativeHostSessionIncarnationAsync(CancellationToken.None);
            await store.SaveAsync(state with { AppliedPlacements = [Placement(fact, policy)] }, CancellationToken.None);
            Assert.True(runtime.TryApplyPolicyRecord(policy));
            _ = await fixture.RunRealtimeCycleAsync();
            var disk = ReadCanonical(path);
            var retained = Assert.Single(Assert.Single(disk.AppliedPlacements).Records);
            Assert.Equal(JsonSerializer.Serialize(fact), JsonSerializer.Serialize(retained));
            Assert.Equal(new byte[] { 1, 3 }, runtime.ReadPolicy("window-recovery"));
            Assert.False(GpuActionFacts.HasPlacementEffects(disk.AppliedPlacements));
            Assert.Equal(!completed, GpuActionFacts.HasUnsettledActions(disk.AppliedPlacements));
            Assert.Equal(0, fixture.ProcessPolicyWriter.BatchCalls);
            _ = await fixture.RunRealtimeCycleAsync();
            Assert.Equal(JsonSerializer.Serialize(fact), JsonSerializer.Serialize(
                Assert.Single(Assert.Single(ReadCanonical(path).AppliedPlacements).Records)));
            Assert.Equal(completed ? 0 : 1, fixture.ProcessPolicyWriter.RecoveryReadCalls);
            output.WriteLine("windowLedgerPolicyRecovery=" + JsonSerializer.Serialize(new { path, completed, retained }));
        }
        using var reopened = new JsonHostManagerRollbackStateStore(path, TimeProvider.System, TimeSpan.FromMinutes(1));
        await using var restarted = await ScoreOnlyCoordinatorFixture.CreateAsync(warm: true, scoreOnlyEnabled: false,
            policyExecutionEnabled: false, automaticMemoryCleanupEnabled: false,
            optimizationMode: AppOptimizationModes.Normal, rollbackStateStoreOverride: reopened,
            processRecoveryRead: ReadRetainedTarget);
        _ = await restarted.RunRealtimeCycleAsync();
        Assert.Equal(completed ? 0 : 1, restarted.ProcessPolicyWriter.RecoveryReadCalls);
        Assert.Single(Assert.Single(ReadCanonical(path).AppliedPlacements).Records);
        Assert.True(Assert.IsType<bool>(typeof(HostManagerSmartCoordinator).GetField("legacyStatePrepared",
            BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(restarted.Coordinator)));
        var validation = typeof(HostManagerSmartCoordinator).GetMethod("RequireNoAppliedOwnershipAsync",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        var checking = Assert.IsAssignableFrom<Task>(validation.Invoke(restarted.Coordinator, [CancellationToken.None]));
        if (completed) await checking;
        else Assert.Contains("window action remains unconfirmed", (await Assert.ThrowsAsync<InvalidOperationException>(() => checking)).Message);
    }

    [Fact]
    public void WindowLedgerFactsDoNotConsumeRecoveryBudgetOrPreventOtherRecordsBeingSelected()
    {
        var coordinator = (HostManagerSmartCoordinator)RuntimeHelpers.GetUninitializedObject(typeof(HostManagerSmartCoordinator));
        var method = typeof(HostManagerSmartCoordinator).GetMethod("SelectPlacementRecordsForRecovery", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var fact = GpuWindowActionRecord.Create(Prepared());
        IReadOnlyList<HostManagerAppliedPlacementReceipt> records =
            [Placement(fact), Placement(fact, new("test", "a"), new("test", "b")) with { TargetId = "other" }];
        for (var index = 0; index < 4; index++)
        {
            var selected = Assert.IsAssignableFrom<IReadOnlyList<HostManagerAppliedPlacementReceipt>>(method.Invoke(coordinator, [records, 1U]));
            Assert.Equal(index % 2 == 0 ? "a" : "b", Assert.Single(Assert.Single(selected).Records).RecordId);
        }
        Assert.Equal(4, records.Sum(item => item.Records.Count));
    }
}
