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
    public async Task BlockedNativeTargetIsSkippedWhileSameBatchRestoresHealthyPeer()
    {
        const int blockedId = 24_048, healthyId = 24_049;
        var started = CreateProcessStartedAtOrderedBeforeSoftwareTarget(healthyId);
        var startKey = checked((ulong)started.ToFileTime());
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(
            warm: true, scoreOnlyEnabled: false, automaticMemoryCleanupEnabled: false,
            optimizationMode: AppOptimizationModes.Normal,
            processRecoveryRead: id => id == blockedId
                ? RecoveryReadResult<ProcessInstanceRecoverySnapshot>.Unavailable(5, "blocked target")
                : RecoveryReadResult<ProcessInstanceRecoverySnapshot>.Found(new(id, started)));
        await fixture.SeedProcessOwnershipAsync(blockedId, startKey, "software:blocked-native", false, 900);
        var baseline = HostManagerProcessPolicyRollbackPayloadCodec.Encode(new HostManagerProcessPolicyRollbackPayload(
            HostManagerProcessPolicyTransactionFields.CpuPolicy, healthyId, checked((long)startKey),
            BaselinePriorityClass: 0x20, BaselineMemoryPriority: 0,
            BaselinePowerControlMask: 0, BaselinePowerStateMask: 0, TargetMemoryPriority: 0));
        await fixture.SeedProcessOwnershipAsync(healthyId, startKey, "software:healthy-native", false, 902, baseline);
        var owners = await fixture.NativeTransactions.AppliedOwnership.ReadSnapshotAsync(CancellationToken.None);
        var blocked = Assert.Single(owners.Records, record => record.Primary.ProcessId == blockedId);
        await PrepareOwnershipJournalAsync(fixture, blocked, mismatchPayload: false);
        await ApplyJournalRecoveryAsync(fixture, NativeTransactionJournalRecoveryOutcome.InvalidProof);
        var current = new ProcessResourcePolicySnapshot(healthyId, "fixture", @"c:\tests\fixture.exe",
            started, "BelowNormal", 1, 1, MemoryPriority: 5,
            PowerThrottlingControlMask: 1, PowerThrottlingStateMask: 1);
        fixture.ProcessPolicyWriter.ProcessReadHandler = id =>
        {
            Assert.Equal(healthyId, id);
            return current;
        };
        fixture.ProcessPolicyWriter.BatchHandler = requests =>
        {
            var request = Assert.Single(requests);
            Assert.Equal(healthyId, request.ProcessId);
            current = current with
            {
                PriorityClass = request.PriorityClass ?? current.PriorityClass,
                PowerThrottlingControlMask = request.PowerControlMask ?? current.PowerThrottlingControlMask,
                PowerThrottlingStateMask = request.PowerStateMask ?? current.PowerThrottlingStateMask
            };
            var fields = new List<ProcessResourcePolicyBatchFieldResult>();
            if (request.PriorityClass is not null)
                fields.Add(new(ProcessResourcePolicyBatchFields.PriorityClass, true, "applied peer"));
            if (request.PowerControlMask is not null)
                fields.Add(new(ProcessResourcePolicyBatchFields.PowerThrottling, true, "applied peer"));
            return [new ProcessResourcePolicyBatchWriteResult(healthyId, fields)];
        };
        await fixture.Coordinator.RunOnceAsync(CancellationToken.None);

        Assert.Equal(1, fixture.ProcessPolicyWriter.BatchCalls);
        Assert.Equal("Normal", current.PriorityClass);
        Assert.Equal(0U, current.PowerThrottlingControlMask);
        Assert.Equal(0U, current.PowerThrottlingStateMask);
        Assert.Equal((uint)blockedId, Assert.Single((await fixture.NativeTransactions.AppliedOwnership
            .ReadSnapshotAsync(CancellationToken.None)).Records).Primary.ProcessId);
        Assert.Equal((uint)blockedId, Assert.Single((await fixture.ReadJournalSnapshotAsync()).Records).Identity.ProcessId);
    }

    [Fact]
    public async Task BlockedOwnedMemoryTargetIsSkippedBeforeHealthyPeerRestoresInSameBatch()
    {
        const int blockedId = 24_048, healthyId = 24_049;
        var started = CreateProcessStartedAtOrderedBeforeSoftwareTarget(healthyId);
        var startKey = checked((ulong)started.ToFileTime());
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(
            warm: true, scoreOnlyEnabled: false, automaticMemoryCleanupEnabled: false,
            optimizationMode: AppOptimizationModes.Normal,
            processRecoveryRead: id => id == blockedId
                ? RecoveryReadResult<ProcessInstanceRecoverySnapshot>.Unavailable(5, "blocked target")
                : RecoveryReadResult<ProcessInstanceRecoverySnapshot>.Found(new(id, started)));
        await fixture.SeedProcessOwnershipAsync(blockedId, startKey, "software:blocked-memory", true, 900);
        await fixture.SeedProcessOwnershipAsync(healthyId, startKey,
            CreateSoftwareIdOrderedAfterProcessTarget(healthyId, startKey), true, 902);
        var ownership = await fixture.NativeTransactions.AppliedOwnership.ReadSnapshotAsync(CancellationToken.None);
        var blockedOwner = Assert.Single(ownership.Records, record => record.Primary.ProcessId == blockedId);
        await PrepareOwnershipJournalAsync(fixture, blockedOwner, mismatchPayload: false);
        await ApplyJournalRecoveryAsync(fixture, NativeTransactionJournalRecoveryOutcome.InvalidProof);
        var current = 3U;
        fixture.ProcessPolicyWriter.ProcessReadHandler = id =>
        {
            Assert.Equal(healthyId, id);
            return new ProcessResourcePolicySnapshot(id, "fixture", @"c:\tests\fixture.exe",
                started, "Normal", 1, 1, MemoryPriority: current);
        };
        fixture.ProcessPolicyWriter.BatchHandler = requests =>
        {
            var request = Assert.Single(requests);
            Assert.Equal(healthyId, request.ProcessId);
            Assert.Equal(5U, request.MemoryPriority);
            current = 5;
            return [new ProcessResourcePolicyBatchWriteResult(healthyId,
                [new ProcessResourcePolicyBatchFieldResult(
                    ProcessResourcePolicyBatchFields.MemoryPriority, true, "restored peer")])];
        };

        await fixture.Coordinator.RunOnceAsync(CancellationToken.None);

        Assert.Equal(1, fixture.ProcessPolicyWriter.BatchCalls);
        var retained = Assert.Single((await fixture.NativeTransactions.AppliedOwnership
            .ReadSnapshotAsync(CancellationToken.None)).Records);
        Assert.Equal(blockedOwner.Primary.ProcessId, retained.Primary.ProcessId);
        Assert.Equal(blockedOwner.Payload.Slot, retained.Payload.Slot);
        Assert.Equal((uint)NativeTransactionJournalPhase.RecoveryBlocked,
            Assert.Single((await fixture.ReadJournalSnapshotAsync()).Records).Phase);
    }

    [Fact]
    public async Task ProtectedRecordWithoutPayloadDoesNotStopOtherPayloadReconciliation()
    {
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(
            warm: true, scoreOnlyEnabled: false, automaticMemoryCleanupEnabled: false);
        var started = DateTimeOffset.UtcNow.AddMinutes(-5);
        await fixture.SeedProcessOwnershipAsync(24_049, checked((ulong)started.ToFileTime()),
            "software:healthy-payload", true, 900);
        var owner = Assert.Single((await fixture.NativeTransactions.AppliedOwnership
            .ReadSnapshotAsync(CancellationToken.None)).Records);
        await PrepareOwnershipJournalAsync(fixture, owner, mismatchPayload: false);
        Assert.True(fixture.NativeTransactions.TryAcquireExecutionAdmission(out var admission));
        await using (admission)
        {
            var snapshot = await admission!.ReadSnapshotAsync(CancellationToken.None);
            var valid = Assert.Single(snapshot.Records);
            var bad = valid;
            bad.Identity.ActionId += 100;
            bad.Phase = (uint)NativeTransactionJournalPhase.RecoveryBlocked;
            bad.PayloadSlot = 0;
            var source = HostManagerAppliedOwnershipProjection.CreatePayloadBinding(owner);
            var orphan = await admission.PersistPayloadAsync(source, new byte[] { 1, 2, 3 }, CancellationToken.None);
            admission.RequirePayloadReconciliation();
            await (Task)typeof(HostManagerSmartCoordinator)
                .GetMethod("ReconcileNativePayloadsIfRequiredAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(fixture.Coordinator,
                    [admission, snapshot with { Records = new[] { bad, valid } }, CancellationToken.None])!;

            Assert.False(admission.PayloadReconciliationRequired);
            Assert.NotEmpty(await admission.ReadPayloadAsync(
                HostManagerAppliedOwnershipProjection.CreatePayloadReference(owner), source, CancellationToken.None));
            await Assert.ThrowsAsync<FileNotFoundException>(() =>
                admission.ReadPayloadAsync(orphan, source, CancellationToken.None));
        }
    }

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
