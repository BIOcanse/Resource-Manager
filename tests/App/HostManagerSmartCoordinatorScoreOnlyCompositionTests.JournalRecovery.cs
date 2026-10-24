using System.Reflection;
using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Domain.Settings;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Optimization;
using ResourceManager.App.Infrastructure.Optimization.Transactions;

namespace Resource_Manager_APP.Tests;

public sealed partial class HostManagerSmartCoordinatorScoreOnlyCompositionTests
{
    [Fact]
    public async Task BlockedJournalTargetLeavesOtherOwnedNormalRestorationRunning()
    {
        const int processId = 24_049;
        var started = CreateProcessStartedAtOrderedBeforeSoftwareTarget(processId);
        var startKey = checked((ulong)started.ToFileTime());
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(
            warm: true, scoreOnlyEnabled: false, automaticMemoryCleanupEnabled: false,
            optimizationMode: AppOptimizationModes.Normal,
            processRecoveryRead: id => id == processId
                ? RecoveryReadResult<ProcessInstanceRecoverySnapshot>.Found(new(id, started))
                : RecoveryReadResult<ProcessInstanceRecoverySnapshot>.Unavailable(5, "unknown target"));
        await fixture.SeedProcessOwnershipAsync(processId, startKey,
            CreateSoftwareIdOrderedAfterProcessTarget(processId, startKey),
            memoryPolicy: true, actionId: 900);
        await fixture.PreparePendingTransactionAsync();
        await RecoverJournalAsync(fixture);
        var current = 3U;
        fixture.ProcessPolicyWriter.ProcessReadHandler = id => new ProcessResourcePolicySnapshot(
            id, "fixture", @"c:\tests\fixture.exe", started, "Normal", 1, 1,
            MemoryPriority: current);
        fixture.ProcessPolicyWriter.BatchHandler = requests =>
        {
            var request = Assert.Single(requests);
            Assert.Equal(processId, request.ProcessId);
            current = Assert.IsType<uint>(request.MemoryPriority);
            return [new ProcessResourcePolicyBatchWriteResult(processId,
                [new ProcessResourcePolicyBatchFieldResult(
                    ProcessResourcePolicyBatchFields.MemoryPriority, true, "restored")])];
        };

        await fixture.Coordinator.RunOnceAsync(CancellationToken.None);

        Assert.Equal(5U, current);
        Assert.Equal(1, fixture.ProcessPolicyWriter.BatchCalls);
        Assert.Empty((await fixture.NativeTransactions.AppliedOwnership
            .ReadSnapshotAsync(CancellationToken.None)).Records);
        Assert.Equal((uint)NativeTransactionJournalPhase.RecoveryBlocked,
            Assert.Single((await fixture.ReadJournalSnapshotAsync()).Records).Phase);
    }

    [Fact]
    public async Task BlockedJournalRecordDoesNotStopCoordinatorSampling()
    {
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(
            warm: true, scoreOnlyEnabled: false, automaticMemoryCleanupEnabled: false,
            processRecoveryRead: _ => RecoveryReadResult<ProcessInstanceRecoverySnapshot>
                .Unavailable(5, "unavailable"));
        await fixture.PreparePendingTransactionAsync();
        await RecoverJournalAsync(fixture);
        Assert.Equal((uint)NativeTransactionJournalPhase.RecoveryBlocked,
            Assert.Single((await fixture.ReadJournalSnapshotAsync()).Records).Phase);
        var reads = fixture.MetricSampler.ReadLatestCalls;

        await fixture.Coordinator.RunOnceAsync(CancellationToken.None);

        Assert.True(fixture.MetricSampler.ReadLatestCalls > reads);
        Assert.Equal(HostManagerTransactionJournalRuntimeState.RecoveryRequired,
            fixture.NativeTransactions.JournalState);
        Assert.Equal((uint)NativeTransactionJournalPhase.RecoveryBlocked,
            Assert.Single((await fixture.ReadJournalSnapshotAsync()).Records).Phase);
    }

    [Theory]
    [InlineData(NativeTransactionJournalRecoveryOutcome.EffectInvocationUncertain)]
    [InlineData(NativeTransactionJournalRecoveryOutcome.InvalidProof)]
    [InlineData(NativeTransactionJournalRecoveryOutcome.RetryableFailure)]
    internal async Task HostManagerRetiredSelfResourceRestartSettlesWithoutRestoring(
        NativeTransactionJournalRecoveryOutcome interrupted)
    {
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(
            warm: true, scoreOnlyEnabled: false, automaticMemoryCleanupEnabled: false);
        await fixture.PreparePendingTransactionAsync(NativeTransactionJournalScope.Resource);
        await ApplyJournalRecoveryAsync(fixture, interrupted);
        var runtime = Assert.IsType<HostManagerTransactionJournalRuntime>(
            ReadPrivateReference(fixture.NativeTransactions, "journal"));
        var plan = fixture.NativeTransactions.CurrentJournalPlan;
        await runtime.DisposeAsync();
        var reopened = await JournalRuntimeTestStorage.OpenAsync(
            runtime.DataRoot, plan.Recreate, plan.HotPublish, CancellationToken.None);
        typeof(HostManagerNativeActionTransactionRuntime)
            .GetField("journal", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(fixture.NativeTransactions, reopened);

        await RecoverJournalAsync(fixture);

        Assert.Empty((await fixture.ReadJournalSnapshotAsync()).Records);
        Assert.Equal(0, fixture.ProcessPolicyWriter.TotalCalls);
        Assert.Equal(HostManagerTransactionJournalRuntimeState.ReadyForExecution,
            fixture.NativeTransactions.JournalState);
    }

    [Fact]
    public async Task RecoveryOwnershipPayloadMismatchSettlesOnlyOlderRecord()
    {
        const int processId = 24_049;
        var started = CreateProcessStartedAtOrderedBeforeSoftwareTarget(processId);
        var startKey = checked((ulong)started.ToFileTime());
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(
            warm: true, scoreOnlyEnabled: false, automaticMemoryCleanupEnabled: false,
            processRecoveryRead: id => RecoveryReadResult<ProcessInstanceRecoverySnapshot>
                .Found(new(id, started)));
        await fixture.SeedProcessOwnershipAsync(processId, startKey,
            CreateSoftwareIdOrderedAfterProcessTarget(processId, startKey),
            memoryPolicy: true, actionId: 900);
        var ownership = Assert.Single((await fixture.NativeTransactions.AppliedOwnership
            .ReadSnapshotAsync(CancellationToken.None)).Records);
        await PrepareOwnershipJournalAsync(fixture, ownership, mismatchPayload: true);
        await ApplyJournalRecoveryAsync(fixture, NativeTransactionJournalRecoveryOutcome.AlreadyRestored);

        await RecoverJournalAsync(fixture);

        Assert.Empty((await fixture.ReadJournalSnapshotAsync()).Records);
        var retained = Assert.Single((await fixture.NativeTransactions.AppliedOwnership
            .ReadSnapshotAsync(CancellationToken.None)).Records);
        Assert.Equal(ownership.Payload.Slot, retained.Payload.Slot);
        Assert.Equal(ownership.RecordRevision, retained.RecordRevision);
        Assert.Equal(0, fixture.ProcessPolicyWriter.TotalCalls);
    }

    private static async Task RecoverJournalAsync(ScoreOnlyCoordinatorFixture fixture)
    {
        var admission = HostManagerCycleEffectAdmission.Create(scoreOnly: false);
        admission.InitializeBudgets(16, 16);
        var task = (Task)typeof(HostManagerSmartCoordinator)
            .GetMethod("RecoverNativeTransactionsAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(fixture.Coordinator, [admission, CancellationToken.None])!;
        await task;
        var result = task.GetType().GetProperty("Result")!.GetValue(task)!;
        Assert.False((bool)result.GetType().GetProperty("IsBlocked")!.GetValue(result)!);
    }

    private static async Task ApplyJournalRecoveryAsync(
        ScoreOnlyCoordinatorFixture fixture, NativeTransactionJournalRecoveryOutcome outcome)
    {
        HostManagerTransactionJournalAdmission? admission;
        var acquired = fixture.NativeTransactions.TryAcquireRecoveryAdmission(out admission) ||
            fixture.NativeTransactions.TryAcquireExecutionAdmission(out admission);
        Assert.True(acquired);
        await using (admission)
        {
            var snapshot = await admission!.ReadSnapshotAsync(CancellationToken.None);
            var record = Assert.Single(snapshot.Records);
            var now = Math.Max(record.UpdatedAtUtcMilliseconds,
                checked((ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
            var evidence = HostManagerTransactionJournalProjection.CreateRecoveryEvidence(
                record, snapshot.Header.JournalRevision, outcome, now,
                outcome == NativeTransactionJournalRecoveryOutcome.RetryableFailure ? now + 100 : 0,
                0, 0);
            Assert.Equal(NativeTransactionJournalStatus.Ok,
                await admission.ApplyRecoveryEvidenceAsync(evidence, CancellationToken.None));
        }
    }

    private static async Task PrepareOwnershipJournalAsync(
        ScoreOnlyCoordinatorFixture fixture, NativeAppliedOwnershipRecord ownership,
        bool mismatchPayload)
    {
        Assert.True(fixture.NativeTransactions.TryAcquireExecutionAdmission(out var admission));
        await using (admission)
        {
            var snapshot = await admission!.ReadSnapshotAsync(CancellationToken.None);
            var binding = HostManagerAppliedOwnershipProjection.CreatePayloadBinding(ownership);
            var identity = binding.ActionIdentity;
            identity.ActionId++;
            var now = checked((ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            var reference = HostManagerAppliedOwnershipProjection.CreatePayloadReference(ownership);
            var provenance = NativeTransactionJournalPayloadProvenance.Create(binding);
            var prepare = new NativeTransactionJournalPrepareInput
            {
                AbiVersion = NativeTransactionJournalAbi.Version,
                StructSize = NativeTransactionJournalSession.SizeOf<NativeTransactionJournalPrepareInput>(),
                ExpectedJournalRevision = snapshot.Header.JournalRevision,
                Identity = identity,
                Scope = (uint)binding.Scope,
                Disposition = (uint)binding.Disposition,
                DomainMask = (uint)binding.Domain,
                GradeValidMask = (uint)binding.GradeValidMask,
                ProcessFromGrade = binding.ProcessFromGrade,
                ProcessToGrade = binding.ProcessToGrade,
                PayloadKind = (uint)NativeTransactionJournalPayloadKind.Durable,
                PayloadSlot = mismatchPayload ? reference.Slot + 1 : reference.Slot,
                PayloadGeneration = reference.Generation,
                PayloadLength = reference.Length,
                PayloadDigestLow = reference.DigestLow,
                PayloadDigestHigh = reference.DigestHigh,
                PayloadProvenanceDigestLow = provenance.DigestLow,
                PayloadProvenanceDigestHigh = provenance.DigestHigh,
                NowUtcMilliseconds = now,
                MaximumRecoveryAttempts = 3,
                RecoveryDeadlineUtcMilliseconds = now + 60_000
            };
            Assert.Equal(NativeTransactionJournalStatus.Ok,
                await admission.PrepareAsync(prepare, CancellationToken.None));
        }
    }
}
