using System.Reflection;
using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Optimization;
using ResourceManager.App.Infrastructure.Optimization.Transactions;

namespace Resource_Manager_APP.Tests;

public sealed partial class HostManagerSmartCoordinatorScoreOnlyCompositionTests
{
    [Fact]
    public async Task FullCyclesRetainMidRoundOwnerThenForgetItWithoutTransferringTheGradeOnPidReuse()
    {
        const int pid = 43_125;
        var oldBirth = DateTimeOffset.UtcNow.AddMinutes(-5);
        var oldKey = checked((ulong)oldBirth.ToFileTime());
        var newKey = oldKey + 10;
        var raw = CreateCompleteProcessFacts(pid, oldKey, "software:round-race", 20, 10, 5);
        var capturedOld = raw with { HasSeparateFoundationPayloads = true,
            InventoryProcesses = raw.Processes, Processes = [], AttributionProcesses = [], EmittedCount = 0, SkippedCount = 1 };
        var capturedNew = capturedOld with
        { InventoryProcesses = [raw.Processes[0] with { ProcessStartKey = newKey }] };
        // The PID has already been reused after the first inventory capture.
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(warm: true,
            scoreOnlyEnabled: false, processFactsSnapshot: capturedOld, policyExecutionEnabled: true,
            automaticMemoryCleanupEnabled: false,
            processRecoveryRead: _ => RecoveryReadResult<ProcessInstanceRecoverySnapshot>.Found(
                new(pid, DateTimeOffset.FromFileTime((long)newKey))));
        var seeded = await fixture.SeedProcessOwnershipAsync(pid, oldKey, "software:round-race", false, 72_006,
            RaceCpuPayload(pid, oldKey));
        await fixture.Coordinator.RunOnceAsync(default);
        var retained = await fixture.NativeTransactions.AppliedOwnership.ReadSnapshotAsync(default);
        Assert.Equal(oldKey, Assert.Single(retained.Records).Primary.ProcessStartKey);
        Assert.Equal(0, fixture.ProcessPolicyWriter.RecoveryReadCalls);
        Assert.True(File.Exists(seeded.PayloadPath));
        fixture.ProcessFacts.SetSnapshot(capturedNew);
        await fixture.Coordinator.RunOnceAsync(default);
        var removed = await fixture.NativeTransactions.AppliedOwnership.ReadSnapshotAsync(default);
        Assert.Empty(removed.Records);
        Assert.Equal(retained.Header.LedgerRevision + 1, removed.Header.LedgerRevision);
        Assert.False(File.Exists(seeded.PayloadPath));
        var next = Assert.Single(fixture.Workspace.CurrentSnapshotRows.ToArray(), row => row.ProcessId == pid
            && row.ProcessStartKey == newKey);
        Assert.Equal(NativeSmartCoordinatorProcessGrade.Normal, next.AppliedProcessGrade);
        Assert.Equal(0U, next.ProcessPendingCount);
        await fixture.Coordinator.RunOnceAsync(default);
        Assert.Equal(removed.Header.LedgerRevision,
            (await fixture.NativeTransactions.AppliedOwnership.ReadSnapshotAsync(default)).Header.LedgerRevision);
        Assert.Equal(1, fixture.ProcessPolicyWriter.RecoveryReadCalls);
        Assert.Equal(0, fixture.ProcessPolicyWriter.BatchCalls);
        Assert.Empty((await fixture.ReadJournalSnapshotAsync()).Records);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OwnedGradeWithoutDecisionFactsCannotBeRestoredByUnconfirmedAbsence(bool inInventory)
    {
        const int pid = 43_124;
        var birth = DateTimeOffset.UtcNow.AddMinutes(-5);
        var key = checked((ulong)birth.ToFileTime());
        var live = CreateCompleteProcessFacts(pid, key, "software:unreadable-owner", 20, 10, 5);
        var facts = live with
        {
            HasSeparateFoundationPayloads = true,
            InventoryProcesses = inInventory ? live.Processes : [],
            AttributionProcesses = [], Processes = [],
            EnumeratedCount = inInventory ? 1U : 0U, EmittedCount = 0,
            SkippedCount = inInventory ? 1U : 0U
        };
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(warm: true,
            scoreOnlyEnabled: false, processFactsSnapshot: facts, policyExecutionEnabled: true,
            automaticMemoryCleanupEnabled: false,
            processRecoveryRead: _ => RecoveryReadResult<ProcessInstanceRecoverySnapshot>.Found(new(pid, birth)));
        var seeded = await fixture.SeedProcessOwnershipAsync(pid, key, "software:unreadable-owner", false, 72_005,
            RaceCpuPayload(pid, key));
        fixture.ProcessPolicyWriter.ProcessReadHandler = id => new(id, "owned", @"c:\tests\owned.exe", birth,
            "BelowNormal", 1, 1, MemoryPriority: 5, PowerThrottlingControlMask: 1, PowerThrottlingStateMask: 1);
        fixture.ProcessPolicyWriter.BatchHandler = requests => requests.Select(request =>
            new ProcessResourcePolicyBatchWriteResult(request.ProcessId,
                [new(ProcessResourcePolicyBatchFields.PriorityClass, true, "restored"),
                 new(ProcessResourcePolicyBatchFields.PowerThrottling, true, "restored")])).ToArray();
        await fixture.Coordinator.RunOnceAsync(default);
        Assert.Equal(NativeSmartCoordinatorRuntimeState.Unknown,
            Assert.Single(fixture.Workspace.CurrentSnapshotRows.ToArray(), row => row.ProcessId == pid).RuntimeState);
        Assert.Equal(0, fixture.ProcessPolicyWriter.BatchCalls);
        Assert.Equal(key, Assert.Single((await fixture.NativeTransactions.AppliedOwnership.ReadSnapshotAsync(default)).Records)
            .Primary.ProcessStartKey);
        Assert.True(File.Exists(seeded.PayloadPath));
        Assert.Empty((await fixture.ReadJournalSnapshotAsync()).Records);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OwnedPidReuseSettlesOnlyTheExitedIncarnationExactlyOnce(bool memoryPolicy)
    {
        const int pid = 43_121;
        var birth = DateTimeOffset.UtcNow.AddMinutes(-5).ToFileTime();
        var oldKey = checked((ulong)birth);
        var newKey = oldKey + 10;
        var current = CreateCompleteProcessFacts(pid, newKey, "software:owned-race", 20, 10, 5);
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(warm: true,
            scoreOnlyEnabled: false, processFactsSnapshot: CreateCompleteProcessFacts(),
            automaticMemoryCleanupEnabled: false, policyExecutionEnabled: false,
            processRecoveryRead: id => RecoveryReadResult<ProcessInstanceRecoverySnapshot>.Found(
                new(id, DateTimeOffset.FromFileTime((long)newKey))));
        await fixture.Coordinator.RunOnceAsync(default);
        var old = await fixture.SeedProcessOwnershipAsync(pid, oldKey, "software:owned-race", memoryPolicy, 72_001,
            memoryPolicy ? null : RaceCpuPayload(pid, oldKey));
        var next = await fixture.SeedProcessOwnershipAsync(pid, newKey, "software:owned-race", memoryPolicy, 72_002,
            memoryPolicy ? null : RaceCpuPayload(pid, newKey));
        var before = await fixture.NativeTransactions.AppliedOwnership.ReadSnapshotAsync(default);
        Assert.Equal(2, before.Records.Count);
        var nextPayload = File.ReadAllBytes(next.PayloadPath);
        var readsBefore = fixture.ProcessPolicyWriter.RecoveryReadCalls;

        var after = await ReconcileOwnedExitRaceAsync(fixture, current);
        var survivor = Assert.Single(after.Records);
        Assert.Equal(newKey, survivor.Primary.ProcessStartKey);
        Assert.Equal(72_002UL, survivor.OriginalBinding.ActionIdentity.ActionId);
        Assert.Equal(before.Header.LedgerRevision + 1, after.Header.LedgerRevision);
        Assert.False(File.Exists(old.PayloadPath));
        Assert.Equal(nextPayload, File.ReadAllBytes(next.PayloadPath));
        Assert.Equal(readsBefore + 1, fixture.ProcessPolicyWriter.RecoveryReadCalls);
        Assert.Equal(0, fixture.ProcessPolicyWriter.BatchCalls); // Never restore the old grade onto the new PID incarnation.
        Assert.Empty((await fixture.ReadJournalSnapshotAsync()).Records);

        var repeated = await ReconcileOwnedExitRaceAsync(fixture, current);
        Assert.Equal(after.Header.LedgerRevision, repeated.Header.LedgerRevision);
        Assert.Equal(newKey, Assert.Single(repeated.Records).Primary.ProcessStartKey);
        Assert.Equal(readsBefore + 1, fixture.ProcessPolicyWriter.RecoveryReadCalls);
        Assert.Equal(nextPayload, File.ReadAllBytes(next.PayloadPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SameIdentityReobservedAfterInventoryMissRetainsItsOwnedGrade(bool memoryPolicy)
    {
        const int pid = 43_122;
        var birth = DateTimeOffset.UtcNow.AddMinutes(-5);
        var key = checked((ulong)birth.ToFileTime());
        var live = CreateCompleteProcessFacts(pid, key, "software:reobserved", 20, 10, 5);
        var missing = live with { Processes = [], EnumeratedCount = 0, EmittedCount = 0 };
        RecoveryReadResult<ProcessInstanceRecoverySnapshot> recovery =
            RecoveryReadResult<ProcessInstanceRecoverySnapshot>.Found(new(pid, birth));
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(warm: true,
            scoreOnlyEnabled: false, processFactsSnapshot: CreateCompleteProcessFacts(),
            automaticMemoryCleanupEnabled: false, policyExecutionEnabled: false,
            processRecoveryRead: _ => recovery);
        await fixture.Coordinator.RunOnceAsync(default);
        var seeded = await fixture.SeedProcessOwnershipAsync(pid, key, "software:reobserved", memoryPolicy, 72_003,
            memoryPolicy ? null : RaceCpuPayload(pid, key));
        var payload = File.ReadAllBytes(seeded.PayloadPath);
        var before = await fixture.NativeTransactions.AppliedOwnership.ReadSnapshotAsync(default);
        var readsBefore = fixture.ProcessPolicyWriter.RecoveryReadCalls;

        // Absence at one observation is not an exit: recovery still sees the
        // very same live incarnation. An actual exited kernel object cannot revive.
        var missed = await ReconcileOwnedExitRaceAsync(fixture, missing);
        Assert.Equal(before.Header.LedgerRevision, missed.Header.LedgerRevision);
        Assert.Equal(key, Assert.Single(missed.Records).Primary.ProcessStartKey);
        var reobserved = await ReconcileOwnedExitRaceAsync(fixture, live);
        Assert.Equal(before.Header.LedgerRevision, reobserved.Header.LedgerRevision);
        Assert.Equal(readsBefore + 1, fixture.ProcessPolicyWriter.RecoveryReadCalls);
        Assert.Equal(payload, File.ReadAllBytes(seeded.PayloadPath));
        Assert.Equal(0, fixture.ProcessPolicyWriter.BatchCalls);

        recovery = RecoveryReadResult<ProcessInstanceRecoverySnapshot>.Unavailable(5, "denied");
        var unknown = await ReconcileOwnedExitRaceAsync(fixture, missing);
        Assert.Equal(before.Header.LedgerRevision, unknown.Header.LedgerRevision);
        Assert.Equal(payload, File.ReadAllBytes(seeded.PayloadPath));
        recovery = RecoveryReadResult<ProcessInstanceRecoverySnapshot>.NotFoundOrExited(87, "exited");
        var exited = await ReconcileOwnedExitRaceAsync(fixture, missing);
        Assert.Empty(exited.Records);
        Assert.Equal(before.Header.LedgerRevision + 1, exited.Header.LedgerRevision);
        Assert.False(File.Exists(seeded.PayloadPath));
        var repeated = await ReconcileOwnedExitRaceAsync(fixture, missing);
        Assert.Equal(exited.Header.LedgerRevision, repeated.Header.LedgerRevision);
        Assert.Equal(readsBefore + 3, fixture.ProcessPolicyWriter.RecoveryReadCalls);
        Assert.Empty((await fixture.ReadJournalSnapshotAsync()).Records);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OwnedMidRoundExitWaitsForAFullAbsentInventoryAndSettlesOnce(bool memoryPolicy)
    {
        const int pid = 43_123;
        var key = checked((ulong)DateTimeOffset.UtcNow.AddMinutes(-5).ToFileTime());
        var liveAtCapture = CreateCompleteProcessFacts(pid, key, "software:mid-round", 20, 10, 5);
        var absent = liveAtCapture with { Processes = [], EnumeratedCount = 0, EmittedCount = 0 };
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(warm: true,
            scoreOnlyEnabled: false, processFactsSnapshot: CreateCompleteProcessFacts(),
            automaticMemoryCleanupEnabled: false, policyExecutionEnabled: false,
            processRecoveryRead: _ => RecoveryReadResult<ProcessInstanceRecoverySnapshot>.NotFoundOrExited(87, "exited"));
        await fixture.Coordinator.RunOnceAsync(default);
        var seeded = await fixture.SeedProcessOwnershipAsync(pid, key, "software:mid-round", memoryPolicy, 72_004,
            memoryPolicy ? null : RaceCpuPayload(pid, key));
        var before = await fixture.NativeTransactions.AppliedOwnership.ReadSnapshotAsync(default);
        var readsBefore = fixture.ProcessPolicyWriter.RecoveryReadCalls;
        var capturedAlive = await ReconcileOwnedExitRaceAsync(fixture, liveAtCapture);
        Assert.Equal(before.Header.LedgerRevision, capturedAlive.Header.LedgerRevision);
        Assert.True(File.Exists(seeded.PayloadPath));
        var incomplete = await ReconcileOwnedExitRaceAsync(fixture, absent with { OverflowCount = 1 });
        Assert.Equal(before.Header.LedgerRevision, incomplete.Header.LedgerRevision);
        Assert.Equal(readsBefore, fixture.ProcessPolicyWriter.RecoveryReadCalls);
        var exited = await ReconcileOwnedExitRaceAsync(fixture, absent);
        Assert.Empty(exited.Records);
        Assert.Equal(before.Header.LedgerRevision + 1, exited.Header.LedgerRevision);
        Assert.False(File.Exists(seeded.PayloadPath));
        var repeated = await ReconcileOwnedExitRaceAsync(fixture, absent);
        Assert.Equal(exited.Header.LedgerRevision, repeated.Header.LedgerRevision);
        Assert.Equal(readsBefore + 1, fixture.ProcessPolicyWriter.RecoveryReadCalls);
        Assert.Equal(0, fixture.ProcessPolicyWriter.BatchCalls);
        Assert.Empty((await fixture.ReadJournalSnapshotAsync()).Records);
    }

    private static byte[] RaceCpuPayload(int pid, ulong startKey)
        => HostManagerProcessPolicyRollbackPayloadCodec.Encode(new(
            HostManagerProcessPolicyTransactionFields.CpuPolicy, pid, checked((long)startKey),
            BaselinePriorityClass: 0x20, BaselineMemoryPriority: 0,
            BaselinePowerControlMask: 0, BaselinePowerStateMask: 0, TargetMemoryPriority: 0));

    private static async Task<NativeAppliedOwnershipSnapshot> ReconcileOwnedExitRaceAsync(
        ScoreOnlyCoordinatorFixture fixture, SchedulingProcessFactSnapshot facts)
    {
        var effects = HostManagerCycleEffectAdmission.Create(scoreOnly: false);
        effects.InitializeBudgets(8, 8);
        Assert.True(effects.TryAcquire(HostManagerCycleEffectKind.ExitedOwnershipReconciliation, out var permit));
        Assert.True(fixture.NativeTransactions.TryAcquireExecutionAdmission(out var admission));
        await using (admission!)
            return await (Task<NativeAppliedOwnershipSnapshot>)typeof(HostManagerSmartCoordinator)
                .GetMethod("ReconcileExitedProcessOwnershipAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(fixture.Coordinator, [permit, facts, admission, CancellationToken.None])!;
    }
}
