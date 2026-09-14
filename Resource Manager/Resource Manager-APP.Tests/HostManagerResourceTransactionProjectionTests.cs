using ResourceManager.Adapter;
using ResourceManager.Adapter.NativeScheduling;
using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Domain.Adaptation;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Optimization;
using ResourceManager.App.Infrastructure.Optimization.NativeScheduling;
using ResourceManager.App.Infrastructure.Optimization.Transactions;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;

namespace Resource_Manager_APP.Tests;

public sealed class HostManagerResourceTransactionProjectionTests
{
    private const ulong Mib = 1024 * 1024;
    private const ulong ProjectionEpoch = 0x7101;

    [Fact]
    public async Task ExactDurablePrepareProofBindsAndReopensAsBlockingPending()
    {
        var hostPlan = HostManagerTestPlanFactory.CreatePlan();
        var deployment = new HostManagerDeploymentState();
        var provider = new RuntimePlanProvider(deployment);
        provider.Publish(CompiledRuntimePlan.Default with
        {
            Version = 1,
            HostManager = hostPlan
        });
        var firstIdentity = new HostManagerRuntimeIdentity();
        using var scheduler = new HostResourceScheduler(
            provider,
            deployment,
            firstIdentity);
        var plan = PlanPrivate(
            scheduler,
            requestId: 1,
            now: 100,
            pending: [],
            attemptLow: 0,
            attemptHigh: 0x7202);
        var selection = Assert.Single(plan.NativePlan.Selections);
        var token = scheduler.ReserveSelection(
            plan,
            selection,
            nowMonotonicTimestamp: 100,
            deadlineTimestamp: 200,
            pendingGeneration: 0x7301);
        var encoded = HostManagerResourceTransactionProjection.EncodePayload(
            plan,
            token,
            selection,
            CreateCapacity(),
            token.PendingGeneration,
            deadlineTimestamp: 200,
            journalConfigurationGeneration:
                hostPlan.HotPublish.TransactionJournal.ConfigurationGeneration,
            hostSessionIncarnation: firstIdentity.InstanceId);
        var payload =
            HostManagerResourceTransactionProjection.DecodePayload(encoded);
        Assert.Equal(HostManagerResourceTransactionProjection.EncodedPayloadSize, encoded.Length);
        Assert.Equal(CreateCapacity(), payload.Capacity);
        var reservedHeaderByte = encoded.ToArray();
        reservedHeaderByte[60] = 1;
        Assert.Throws<InvalidDataException>(() =>
            HostManagerResourceTransactionProjection.DecodePayload(
                reservedHeaderByte));
        var root = CreateTempDirectory();
        HostManagerTransactionJournalRuntime? journal = null;
        HostManagerTransactionJournalRuntime? reopened = null;
        try
        {
            journal = await HostManagerTransactionJournalRuntime.OpenAsync(
                root,
                hostPlan.HostRecreate.TransactionJournal,
                hostPlan.HotPublish.TransactionJournal,
                CancellationToken.None);
            Assert.True(journal.TryAcquireExecutionAdmission(out var admission));
            Assert.NotNull(admission);
            await using (admission)
            {
                if (admission!.PayloadReconciliationRequired)
                {
                    _ = await admission.ReconcilePayloadsAsync(
                        ReadOnlyMemory<
                            NativeTransactionJournalPayloadLiveReference>
                            .Empty,
                        CancellationToken.None);
                }
                var snapshot = await admission!.ReadSnapshotAsync(
                    CancellationToken.None);
                var nowUtc = checked((ulong)
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                var binding =
                    HostManagerResourceTransactionProjection
                        .CreatePayloadBinding(
                            payload,
                            snapshot.Header.JournalInstanceLow,
                            snapshot.Header.JournalInstanceHigh,
                            checked(nowUtc + 60_000),
                            maximumRecoveryAttempts: 3);
                var provenance =
                    NativeTransactionJournalPayloadProvenance.Create(binding);
                var reference = await admission.PersistPayloadAsync(
                    binding,
                    encoded,
                    CancellationToken.None);
                var prepare =
                    HostManagerResourceTransactionProjection.CreatePrepare(
                        payload,
                        binding,
                        reference,
                        provenance,
                        snapshot.Header.JournalRevision,
                        nowUtc);
                Assert.Equal(
                    NativeTransactionJournalStatus.Ok,
                    await admission.PrepareAsync(
                        prepare,
                        CancellationToken.None));
                var prepared = await admission.ReadSnapshotAsync(
                    CancellationToken.None);
                var read = await admission.GetAsync(
                    prepare.Identity,
                    CancellationToken.None);
                Assert.Equal(NativeTransactionJournalStatus.Ok, read.Status);
                var proof = HostManagerResourceJournalPrepareProof.Create(
                    prepared.Header,
                    read.Record,
                    payload,
                    reference);
                Assert.Equal(0UL, payload.JournalTransactionIdLow);
                Assert.Equal(0x7202UL, payload.JournalTransactionIdHigh);
                Assert.NotEqual(
                    payload.JournalTransactionIdLow,
                    payload.JournalActionId);
                Assert.True(proof.Matches(token, selection));
                Assert.False(proof.Matches(
                    token with
                    {
                        PendingGeneration =
                            checked(token.PendingGeneration + 1)
                    },
                    selection));
                Assert.Throws<InvalidDataException>(() =>
                    HostManagerResourceJournalPrepareProof.Create(
                        prepared.Header,
                        read.Record,
                        payload with
                        {
                            JournalTransactionIdHigh = checked(
                                payload.JournalTransactionIdHigh + 1)
                        },
                        reference));
                Assert.Throws<InvalidDataException>(() =>
                    HostManagerResourceJournalPrepareProof.Create(
                        prepared.Header,
                        read.Record,
                        payload,
                        reference with
                        {
                            DigestHigh = checked(reference.DigestHigh + 1)
                        }));
                scheduler.BindReservationToJournal(token, proof);
            }

            Assert.Equal(
                HostManagerTransactionJournalShutdownResult.RecoveryRequired,
                await journal.ShutdownAsync(CancellationToken.None));
            await journal.DisposeAsync();
            journal = null;

            reopened = await HostManagerTransactionJournalRuntime.OpenAsync(
                root,
                hostPlan.HostRecreate.TransactionJournal,
                hostPlan.HotPublish.TransactionJournal,
                CancellationToken.None);
            Assert.Equal(
                HostManagerTransactionJournalRuntimeState.RecoveryRequired,
                reopened.State);
            Assert.True(reopened.TryAcquireRecoveryAdmission(
                out var recoveryAdmission));
            Assert.NotNull(recoveryAdmission);
            ResourceSchedulerPendingAction durablePending;
            await using (recoveryAdmission)
            {
                var snapshot = await recoveryAdmission!.ReadSnapshotAsync(
                    CancellationToken.None);
                var record = Assert.Single(snapshot.Records);
                var reference =
                    HostManagerTransactionJournalProjection
                        .CreatePayloadReference(in record);
                var provenance =
                    HostManagerTransactionJournalProjection
                        .CreatePayloadProvenance(in record);
                var reopenedPayload =
                    HostManagerResourceTransactionProjection.DecodePayload(
                        await recoveryAdmission.ReadPayloadAsync(
                            reference,
                            provenance,
                            CancellationToken.None));
                durablePending =
                    HostManagerResourceTransactionProjection
                        .CreateJournalPending(
                            snapshot.Header,
                            record,
                            reopenedPayload);
            }
            Assert.Equal(
                selection.Authority.ActionAttemptIdLow,
                durablePending.JournalTransactionIdLow);
            Assert.Equal(
                selection.Authority.ActionAttemptIdHigh,
                durablePending.JournalTransactionIdHigh);

            var restartedDeployment = new HostManagerDeploymentState();
            var restartedProvider =
                new RuntimePlanProvider(restartedDeployment);
            restartedProvider.Publish(CompiledRuntimePlan.Default with
            {
                Version = 1,
                HostManager = hostPlan
            });
            var restartedIdentity = new HostManagerRuntimeIdentity();
            using var restartedScheduler = new HostResourceScheduler(
                restartedProvider,
                restartedDeployment,
                restartedIdentity);
            var blocked = PlanPrivate(
                restartedScheduler,
                requestId: 2,
                now: 300,
                pending: [durablePending],
                attemptLow: 0x7401,
                attemptHigh: 0x7402);
            Assert.Empty(blocked.NativePlan.Selections);
            Assert.NotEqual(plan.StateGeneration, blocked.StateGeneration);
        }
        finally
        {
            if (reopened is not null)
            {
                await reopened.DisposeAsync();
            }
            if (journal is not null)
            {
                await journal.DisposeAsync();
            }
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static HostResourceSchedulerPlanResult PlanPrivate(
        HostResourceScheduler scheduler,
        ulong requestId,
        ulong now,
        IReadOnlyList<ResourceSchedulerPendingAction> pending,
        ulong attemptLow,
        ulong attemptHigh)
        => scheduler.Plan(
            scheduler.CurrentHostPlan,
            requestId,
            now,
            AdapterResourceActionMask.Trim,
            1 << (byte)AdapterResourceTier.PhysicalMemory,
            emitCandidates: true,
            [CreateTarget()],
            [CreatePrivateResource(attemptLow, attemptHigh)],
            new ResourceSchedulerFactBatch(
                ProjectionEpoch,
                8,
                pending));

    private static ResourceSchedulerTarget CreateTarget()
        => new(
            13,
            12,
            13,
            14,
            15,
            16,
            17,
            30,
            CreateCapacity(),
            0,
            1,
            -3,
            ResourceSchedulerSchedulingGrade.Optimize,
            ResourceSchedulerSchedulingGrade.Normal,
            AdapterSoftwareSurfaceState.PureBackground,
            false,
            true);

    private static ResourceSchedulerPrivateResource CreatePrivateResource(
        ulong attemptLow,
        ulong attemptHigh)
        => new(
            TargetKey: 13,
            SizeBytes: 512 * Mib,
            DiscardAuthority: default,
            CreateAuthority(attemptLow, attemptHigh),
            MoveDownAuthority: default,
            AdapterResourceTier.PhysicalMemory,
            AdapterResourceKind.Cache,
            AdapterResourceRecoveryKind.BuiltData,
            AdapterResourceGranularity.PartialUsable,
            AdapterResourceProtocol.AllActions
                & ~AdapterResourceActionMask.Trim,
            AdapterResourceActionRoute.ManagerDirect,
            AdapterResourceDemandMask.None,
            ActivityScore: 0);

    private static ResourceSchedulerExecutionAuthority CreateAuthority(
        ulong attemptLow,
        ulong attemptHigh)
        => new(
            ResourceSchedulerSource.AdaptedPrivate,
            AdapterResourceActionMask.Trim,
            AdapterResourceActionRoute.ManagerDirect,
            ResourceSchedulerExecutionAuthority.HostSelfExecutorProof,
            ResourceSlot: 1,
            ResourceId: 1,
            LedgerInstanceId: 20,
            SourceSnapshotGeneration: 19,
            ResourceGeneration: 21,
            ResourceKey: 101,
            OwnerApplicationKey: 12,
            OwnerInstanceIdLow: 13,
            OwnerInstanceIdHigh: 14,
            OwnerContextGeneration: 15,
            LeaseGeneration: 16,
            BindingGeneration: 18,
            CapabilityGeneration: 17,
            SchedulingRevision: 22,
            ExecutorIdLow: 23,
            ExecutorIdHigh: 0,
            attemptLow,
            attemptHigh,
            ProjectionEpoch);

    private static ResourceSchedulerCapacity CreateCapacity()
        => new(
            TotalVramBytes: 8 * Mib,
            FreeVramBytes: 4 * Mib,
            TotalPhysicalBytes: 16 * Mib,
            FreePhysicalBytes: 1 * Mib,
            TotalVirtualBytes: 32 * Mib,
            FreeVirtualBytes: 16 * Mib,
            FallbackVramFreeRatio: 0.50,
            FallbackPhysicalFreeRatio: 0.0625,
            FallbackVirtualFreeRatio: 0.50,
            FreeBytesValidMask: 0b111);

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"rm-resource-proof-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
