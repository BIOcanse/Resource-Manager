using ResourceManager.App.Application.DiskUsage;
using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Domain.DiskUsage;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Infrastructure.Adaptation;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.Ledger;
using ResourceManager.ReadWrite.Native;

namespace Resource_Manager_APP.Tests;

public sealed class MetricScratchLedgerLifecycleTests
{
    [Fact]
    public void SelfCanCloseIdleMetricIdentityBeforeMetricDestroysItsUnboundNativeState()
    {
        using var owner = new ResourceManagerLedgerOwner();
        using var manager = CreateManager(owner);
        var configuration = NativeMetricSnapshotAbiTests.CreateConfiguration();
        var session = new NativeMetricSnapshotSession(in configuration,
            (length, alignment) => MetricSnapshotScratch.Create(owner.CreateScratch, length, alignment));
        var call = new NativeMetricSnapshotAbiTests.ScratchCalls(session);
        call.Begin(); call.Submit(42);
        Assert.Equal(NativeMetricSnapshotStatus.Ok, session.FinalizeCompletion(in call.Finalize));
        Assert.Equal(session.Capacity.ScratchByteCount + 7, owner.ReadTable().RegisteredBytes);
        Assert.Contains(manager.TryClose(), new[] { LedgerStatus.Completed, LedgerStatus.Ok });
        // The checkpoint/current/baseline bank remains Native-owned, not an idle region pointer.
        Assert.Equal(42UL, call.Read().ValueBits);
        session.Dispose(); session.Dispose();
        Assert.Contains(manager.TryClose(), new[] { LedgerStatus.Completed, LedgerStatus.Ok });
    }

    [Fact]
    public void LiveMetricCompletionKeepsSharedOwnerValidWhenSelfCloseReturnsInUse()
    {
        using var owner = new ResourceManagerLedgerOwner();
        using var manager = CreateManager(owner);
        var configuration = NativeMetricSnapshotAbiTests.CreateConfiguration();
        using var session = new NativeMetricSnapshotSession(in configuration,
            (length, alignment) => MetricSnapshotScratch.Create(owner.CreateScratch, length, alignment));
        var call = new NativeMetricSnapshotAbiTests.ScratchCalls(session);
        call.Begin();
        Assert.Equal(LedgerStatus.InUse, manager.TryClose());
        Assert.Equal(NativeMetricSnapshotStatus.Ok, session.QueryHeader(out var header));
        Assert.Equal((uint)NativeMetricSnapshotPhase.CompletionOpen, header.Phase);
        call.Submit(ulong.MaxValue);
        Assert.Equal(NativeMetricSnapshotStatus.Ok, session.AbortCompletion(in call.Finalize));
        Assert.Contains(manager.TryClose(), new[] { LedgerStatus.Completed, LedgerStatus.Ok });
    }

    [Fact]
    public void EvictedMetricRefillsTheSameIdentityAndFullScratchTableRejectsAnotherUseWithoutFallback()
    {
        using var owner = new ResourceManagerLedgerOwner();
        var configuration = NativeMetricSnapshotAbiTests.CreateConfiguration();
        ResourceManagerLedgerOwner.ComputeScratch? identity = null;
        var factories = 0;
        using var session = new NativeMetricSnapshotSession(in configuration,
            (length, alignment) => MetricSnapshotScratch.Create(bytes =>
            { factories++; return identity = owner.CreateScratch(bytes); }, length, alignment));
        var uid = identity!.Uid;
        var call = new NativeMetricSnapshotAbiTests.ScratchCalls(session);
        call.Begin(); call.Submit(0);
        Assert.Equal(NativeMetricSnapshotStatus.Ok, session.FinalizeCompletion(in call.Finalize));
        owner.SetMode(LedgerTableMode.Optimize);
        Assert.Equal(1UL, owner.Tick().Released);
        Assert.Equal(0UL, owner.ReadTable().RegisteredBytes);
        using var score = owner.CreateScratch(8192);
        using var oldOperation = owner.CreateScratch(8192);
        using var candidateOperation = owner.CreateScratch(16384);
        using var scoreUse = score.Acquire();
        using var oldUse = oldOperation.Acquire();
        using var candidateUse = candidateOperation.Acquire();
        scoreUse.Complete(); oldUse.Complete(); candidateUse.Complete();
        call.Begin(2);
        Assert.Equal(1, factories);
        Assert.Equal(uid, identity.Uid);
        var registered = 8192UL + 8192UL + 16384UL + session.Capacity.ScratchByteCount + 7;
        Assert.Equal(registered, owner.ReadTable().RegisteredBytes);
        using var oldReport = owner.CreateScratch(40960);
        using var candidateReport = owner.CreateScratch(49152);
        using var oldReportUse = oldReport.Acquire();
        using var candidateReportUse = candidateReport.Acquire();
        oldReportUse.Complete(); candidateReportUse.Complete();
        registered += 40960UL + 49152UL;
        Assert.Equal(registered, owner.ReadTable().RegisteredBytes);
        var sampling = HostManagerTestPlanFactory.CreatePlan().SamplingSubscription.Recreate.Roles[2];
        var samplingCapacity = new NativeSamplingSubscriptionCapacity
        {
            DueItemCapacity = checked((uint)sampling.MaximumDueItemCount),
            SourceViewCapacity = checked((uint)sampling.MaximumSourceViewCount),
            ExpiredSourceCapacity = checked((uint)sampling.MaximumExpiredSourceCount),
            TriggerCapacity = checked((uint)sampling.MaximumMembershipCount)
        };
        var oldSamplingBytes = new NativeSamplingOutputLayout(in samplingCapacity).StorageLength;
        samplingCapacity.TriggerCapacity = checked(samplingCapacity.TriggerCapacity + 1);
        var candidateSamplingBytes = new NativeSamplingOutputLayout(in samplingCapacity).StorageLength;
        using var oldSampling = owner.CreateScratch(oldSamplingBytes);
        using var candidateSampling = owner.CreateScratch(candidateSamplingBytes);
        using var oldSamplingUse = oldSampling.Acquire();
        using var candidateSamplingUse = candidateSampling.Acquire();
        oldSamplingUse.Complete(); candidateSamplingUse.Complete();
        registered += oldSamplingBytes + candidateSamplingBytes;
        Assert.Equal(registered, owner.ReadTable().RegisteredBytes);
        var smartPlan = HostManagerTestPlanFactory.CreatePlan().SmartCoordinator;
        var smartConfiguration = NativeSmartCoordinatorConfigurationWriter.Create(
            smartPlan.Build, smartPlan.Recreate, smartPlan.HotPublish, smartPlan.ConfigurationGeneration);
        using var smart = new NativeSmartCoordinatorWorkspace(in smartConfiguration, owner.CreateScratch);
        using var smartOperation = smart.BeginOperation();
        smart.SetFeedback(0, new NativeSmartCoordinatorFeedback { ActionId = 67 });
        Assert.Equal(67UL, smart.GetFeedback(0).ActionId);
        var smartLayout = new NativeSmartScratchLayout(smart.Capacity.InputRowCapacity,
            smart.Capacity.ActionCapacity, smart.Capacity.FeedbackCapacity);
        registered += smartLayout.Length;
        Assert.Equal(registered, owner.ReadTable().RegisteredBytes);
        var ownershipCapacity = new NativeAppliedOwnershipCapacity
        {
            RecordCapacity = 16, RecordSize = NativeAppliedOwnershipAbi.RecordSize,
            MaximumImageBytes = 512, ResidentBytes = 8192
        };
        using var currentOwnership = new NativeAppliedOwnershipWorkspace(in ownershipCapacity, owner.CreateScratch);
        using var candidateOwnership = new NativeAppliedOwnershipWorkspace(in ownershipCapacity, owner.CreateScratch);
        using var currentOwnershipUse = currentOwnership.Acquire();
        using var candidateOwnershipUse = candidateOwnership.Acquire();
        currentOwnershipUse.Image.Span[0] = 17;
        candidateOwnershipUse.Image.Span[0] = 23;
        currentOwnershipUse.Complete(); candidateOwnershipUse.Complete();
        registered += currentOwnership.Length + candidateOwnership.Length;
        Assert.Equal(registered, owner.ReadTable().RegisteredBytes);
        Assert.Equal(17, currentOwnershipUse.Image.Span[0]);
        Assert.Equal(23, candidateOwnershipUse.Image.Span[0]);
        var journalCapacity = new NativeTransactionJournalCapacity
        {
            RecordCapacity = 16,
            MaximumImageLength = NativeTransactionJournalAbi.ImageHeaderSize +
                16UL * NativeTransactionJournalSession.SizeOf<NativeTransactionJournalRecord>(),
            ResidentBytes = 16384
        };
        using var currentJournal = new NativeTransactionJournalWorkspace(in journalCapacity, owner.CreateScratch);
        using var candidateJournal = new NativeTransactionJournalWorkspace(in journalCapacity, owner.CreateScratch);
        using var currentJournalUse = currentJournal.Acquire();
        using var candidateJournalUse = candidateJournal.Acquire();
        currentJournalUse.Image.Span[0] = 31;
        candidateJournalUse.Image.Span[0] = 41;
        currentJournalUse.Complete(); candidateJournalUse.Complete();
        registered += currentJournal.Length + candidateJournal.Length;
        Assert.Equal(registered, owner.ReadTable().RegisteredBytes);
        Assert.Equal(31, currentJournalUse.Image.Span[0]);
        Assert.Equal(41, candidateJournalUse.Image.Span[0]);
        using var remainingRoleOutputs = new SamplingOutputCapacityBatch();
        registered += SamplingOutputCapacityBatch.FillRemainingRoleOutputs(owner, remainingRoleOutputs);
        Assert.Equal(registered, owner.ReadTable().RegisteredBytes);
        var nativeSamplingPlan = HostManagerTestPlanFactory.CreatePlan().SamplingSubscription;
        ResourceManagerLedgerOwner.ComputeScratch? currentNative = null, candidateNative = null;
        using var currentNativeWorkspace = new NativeSamplingSubscriptionWorkspace();
        using var candidateNativeWorkspace = new NativeSamplingSubscriptionWorkspace();
        currentNativeWorkspace.Initialize(nativeSamplingPlan, length => currentNative = owner.CreateScratch(length));
        candidateNativeWorkspace.Initialize(nativeSamplingPlan, length => candidateNative = owner.CreateScratch(length));
        SamplingSessionScratchTests.Snapshot(currentNativeWorkspace.GetSession(1));
        SamplingSessionScratchTests.Snapshot(candidateNativeWorkspace.GetSession(7));
        using var currentNativeUse = currentNative!.Acquire();
        using var candidateNativeUse = candidateNative!.Acquire();
        currentNativeUse.Complete(); candidateNativeUse.Complete();
        registered += currentNative.Length + candidateNative.Length;
        Assert.Equal(registered, owner.ReadTable().RegisteredBytes);
        ResourceManagerLedgerOwner.ComputeScratch? pdhIdentity = null;
        using var pdh = new PdhParsingScratch(length => pdhIdentity = owner.CreateScratch(length), owner.NativePageSize);
        var additionalResources = new List<ResourceManagerLedgerOwner.ComputeScratch>();
        var additionalUses = new List<ComputeScratchLease>();
        pdh.BeginSample();
        try
        {
            pdh.Supply(8192);
            registered += pdhIdentity!.Length;
            Assert.Equal(registered, owner.ReadTable().RegisteredBytes);
            // Preserve the original 28 real workspace uses, and occupy current
            // plus pending query pools (2 * 64), model workspaces (2),
            // and portable registry output workspaces (2) and display normalization workspaces (2).
            for (var index = 0; index < 134; index++)
            {
                var resource = owner.CreateScratch(8192);
                additionalResources.Add(resource);
                var use = resource.Acquire();
                additionalUses.Add(use);
                use.Complete();
                registered += resource.Length;
            }
            using var excess = owner.CreateScratch(8192);
            var blocked = Assert.Throws<LedgerException>(() => { using var unexpected = excess.Acquire(); });
            Assert.Equal(LedgerStatus.Blocked, blocked.Status);
            Assert.Equal(registered, owner.ReadTable().RegisteredBytes);
            Assert.Equal(0UL, owner.Tick().Released);
        }
        finally
        {
            foreach (var use in additionalUses) use.Dispose();
            foreach (var resource in additionalResources) resource.Dispose();
            pdh.EndSample();
        }
        call.Submit(ulong.MaxValue, 2);
        Assert.Equal(NativeMetricSnapshotStatus.Ok, session.FinalizeCompletion(in call.Finalize));
        Assert.Equal(ulong.MaxValue, call.Read().ValueBits);
        smartOperation.Complete();
    }
    private static ResourceManagerSelfLocalResourceManager CreateManager(ResourceManagerLedgerOwner owner)
        => new(new RejectingPolicyWriter(), new UnusedDiskStore(), owner, () => [], (_, _) => null);

    private sealed class UnusedDiskStore : IDiskUsageTreeStore
    {
        public DiskUsageScanSummary? CurrentSummary => null;
        public DiskUsageTreeReadLease? TryAcquire() => null;
        public void Replace(DiskUsageScanResult result, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Metric close does not replace disk trees.");
        public void Clear() { }
    }

    // Any OS access is a test failure; these cases exercise only shared Ledger close.
    private sealed class RejectingPolicyWriter : IProcessResourcePolicyWriter
    {
        public DateTimeOffset? TryReadProcessStartedAt(int processId) => throw Unexpected();
        public IReadOnlyList<ProcessResourcePolicyBatchWriteResult> TryApplyBatch(
            IReadOnlyList<ProcessResourcePolicyBatchRequest> requests) => throw Unexpected();
        public ProcessResourcePolicySnapshot? TryReadProcess(int processId)
            => throw Unexpected();

        public RecoveryReadResult<ProcessInstanceRecoverySnapshot>
            ReadProcessInstanceForRecovery(int processId)
            => throw Unexpected();

        public RecoveryReadResult<ProcessPlacementRecoverySnapshot>
            ReadPlacementStateForRecovery(
                int processId,
                ProcessPlacementReadFields requiredFields)
            => throw Unexpected();

        public RecoveryReadResult<ThreadCpuSetPolicySnapshot>
            ReadThreadPlacementStateForRecovery(
                int processId,
                int threadId)
            => throw Unexpected();

        public ProcessResourcePolicyWriteResult TrySetPriorityClass(
            int processId,
            string priorityClass)
            => throw Unexpected();

        public ProcessResourcePolicyWriteResult TrySetProcessorAffinity(
            int processId,
            long affinityMask)
            => throw Unexpected();

        public IReadOnlyList<uint>? TryReadProcessDefaultCpuSets(int processId)
            => throw Unexpected();

        public ProcessResourcePolicyWriteResult TrySetProcessDefaultCpuSets(
            int processId,
            IReadOnlyList<uint> cpuSetIds)
            => throw Unexpected();

        public ThreadCpuSetPolicySnapshot? TryReadThreadSelectedCpuSets(
            int processId,
            int threadId)
            => throw Unexpected();

        public ProcessResourcePolicyWriteResult TrySetThreadSelectedCpuSets(
            int processId,
            int threadId,
            DateTimeOffset expectedCreatedAt,
            IReadOnlyList<uint> cpuSetIds)
            => throw Unexpected();

        public ProcessMemoryPrioritySnapshot? TryReadMemoryPriority(
            int processId)
            => throw Unexpected();

        public ProcessResourcePolicyWriteResult TrySetMemoryPriority(
            int processId,
            DateTimeOffset expectedStartedAt,
            uint expectedCurrentMemoryPriority,
            uint memoryPriority)
            => throw Unexpected();

        public ProcessPowerThrottlingSnapshot? TryReadPowerThrottling(
            int processId)
            => throw Unexpected();

        public ProcessResourcePolicyWriteResult TrySetPowerThrottling(
            int processId,
            uint controlMask,
            uint stateMask)
            => throw Unexpected();

        private static InvalidOperationException Unexpected()
            => new("Metric scratch close unexpectedly attempted OS policy access.");
    }
}
