using ResourceManager.Adapter;
using ResourceManager.Adapter.NativeScheduling;
using ResourceManager.App.Domain.ProcessAttribution;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Optimization.NativeScheduling;

namespace Resource_Manager_APP.Tests;

public sealed class HostManagerRetiredSelfResourceJournalRecoveryTests
{
    [Theory]
    [InlineData(
        NativeTransactionJournalPhase.Prepared,
        NativeTransactionJournalFeedbackStatus.Succeeded,
        HostManagerRetiredSelfResourceJournalDecision.MarkEffectInvocationUncertain)]
    [InlineData(
        NativeTransactionJournalPhase.PreviousEffectRestored,
        NativeTransactionJournalFeedbackStatus.Succeeded,
        HostManagerRetiredSelfResourceJournalDecision.SettleKnownNoEffect)]
    [InlineData(
        NativeTransactionJournalPhase.EffectObserved,
        NativeTransactionJournalFeedbackStatus.Succeeded,
        HostManagerRetiredSelfResourceJournalDecision.PreserveBlocked)]
    [InlineData(
        NativeTransactionJournalPhase.FeedbackPending,
        NativeTransactionJournalFeedbackStatus.Succeeded,
        HostManagerRetiredSelfResourceJournalDecision.AcknowledgeSealedFeedback)]
    [InlineData(
        NativeTransactionJournalPhase.FeedbackPending,
        NativeTransactionJournalFeedbackStatus.FailedUnchanged,
        HostManagerRetiredSelfResourceJournalDecision.AcknowledgeSealedFeedback)]
    [InlineData(
        NativeTransactionJournalPhase.FeedbackPending,
        NativeTransactionJournalFeedbackStatus.Rejected,
        HostManagerRetiredSelfResourceJournalDecision.AcknowledgeSealedFeedback)]
    [InlineData(
        NativeTransactionJournalPhase.FeedbackPending,
        NativeTransactionJournalFeedbackStatus.Skipped,
        HostManagerRetiredSelfResourceJournalDecision.AcknowledgeSealedFeedback)]
    [InlineData(
        NativeTransactionJournalPhase.FeedbackPending,
        NativeTransactionJournalFeedbackStatus.OwnershipLost,
        HostManagerRetiredSelfResourceJournalDecision.AcknowledgeSealedFeedback)]
    [InlineData(
        NativeTransactionJournalPhase.FeedbackPending,
        NativeTransactionJournalFeedbackStatus.StateUncertain,
        HostManagerRetiredSelfResourceJournalDecision.AcknowledgeStateUncertain)]
    [InlineData(
        NativeTransactionJournalPhase.ReconciliationPending,
        NativeTransactionJournalFeedbackStatus.Succeeded,
        HostManagerRetiredSelfResourceJournalDecision.PreserveBlocked)]
    [InlineData(
        NativeTransactionJournalPhase.AuthoritativeResyncPending,
        NativeTransactionJournalFeedbackStatus.Succeeded,
        HostManagerRetiredSelfResourceJournalDecision.CompleteAuthoritativeResync)]
    [InlineData(
        NativeTransactionJournalPhase.RecoveryRetryPending,
        NativeTransactionJournalFeedbackStatus.Succeeded,
        HostManagerRetiredSelfResourceJournalDecision.PreserveBlocked)]
    [InlineData(
        NativeTransactionJournalPhase.RecoveryBlocked,
        NativeTransactionJournalFeedbackStatus.Succeeded,
        HostManagerRetiredSelfResourceJournalDecision.PreserveBlocked)]
    [InlineData(
        NativeTransactionJournalPhase.EffectInvocationUncertain,
        NativeTransactionJournalFeedbackStatus.Succeeded,
        HostManagerRetiredSelfResourceJournalDecision.PreserveBlocked)]
    internal void ClassifyNeverRequestsAResourceEffectReplay(
        NativeTransactionJournalPhase phase,
        NativeTransactionJournalFeedbackStatus feedbackStatus,
        HostManagerRetiredSelfResourceJournalDecision expected)
    {
        var record = new NativeTransactionJournalRecord
        {
            Phase = (uint)phase,
            FeedbackStatus = (uint)feedbackStatus
        };

        Assert.Equal(
            expected,
            HostManagerRetiredSelfResourceJournalRecovery.Classify(record));
    }

    [Fact]
    public void ExactRetiredBackendWorkingSetPayloadIsAccepted()
    {
        HostManagerRetiredSelfResourceJournalRecovery.RequireExactPayload(
            CreatePayload(
                "resource-manager:memory:backend-working-set",
                resourceId: 1));
    }

    [Fact]
    public void ExactRetiredNativeUiWorkingSetPayloadIsAccepted()
    {
        HostManagerRetiredSelfResourceJournalRecovery.RequireExactPayload(
            CreatePayload(
                "resource-manager:memory:native-ui-working-set",
                resourceId: 3));
    }

    [Theory]
    [InlineData("wrong-owner")]
    [InlineData("wrong-action")]
    [InlineData("wrong-resource-key")]
    [InlineData("wrong-resource-id")]
    [InlineData("wrong-executor")]
    [InlineData("wrong-tier")]
    public void NonRetiredResourcePayloadIsRejected(string mutation)
    {
        var payload = CreatePayload(
            "resource-manager:memory:backend-working-set",
            resourceId: 1);
        var authority = payload.Selection.Authority;
        payload = mutation switch
        {
            "wrong-owner" => WithAuthority(
                payload,
                authority with
                {
                    OwnerApplicationKey = AdapterResourceKey.FromString("another-app")
                }),
            "wrong-action" => WithAuthority(
                payload,
                authority with { Action = AdapterResourceActionMask.Discard }),
            "wrong-resource-key" => WithAuthority(
                payload,
                authority with
                {
                    ResourceKey = AdapterResourceKey.FromString("another-resource")
                }),
            "wrong-resource-id" => WithAuthority(
                payload,
                authority with { ResourceId = 99 }),
            "wrong-executor" => WithAuthority(
                payload,
                authority with { ExecutorIdLow = payload.HostSessionIncarnation + 1 }),
            "wrong-tier" => payload with
            {
                Selection = payload.Selection with { Tier = AdapterResourceTier.Vram }
            },
            _ => throw new ArgumentOutOfRangeException(nameof(mutation))
        };

        Assert.Throws<InvalidDataException>(() =>
            HostManagerRetiredSelfResourceJournalRecovery.RequireExactPayload(payload));
    }

    [Fact]
    public void UnknownFeedbackStatusIsRejected()
    {
        var record = new NativeTransactionJournalRecord
        {
            Phase = (uint)NativeTransactionJournalPhase.FeedbackPending,
            FeedbackStatus = uint.MaxValue
        };

        Assert.Throws<InvalidDataException>(() =>
            HostManagerRetiredSelfResourceJournalRecovery.Classify(record));
    }

    private static HostManagerResourceTransactionPayload CreatePayload(
        string resourceKey,
        uint resourceId)
    {
        const ulong hostSessionIncarnation = 41;
        const ulong targetKey = 43;
        var authority = new ResourceSchedulerExecutionAuthority(
            ResourceSchedulerSource.AdaptedPrivate,
            AdapterResourceActionMask.Trim,
            AdapterResourceActionRoute.ManagerDirect,
            ResourceSchedulerExecutionAuthority.HostSelfExecutorProof,
            ResourceSlot: 0,
            resourceId,
            LedgerInstanceId: 1,
            SourceSnapshotGeneration: 2,
            ResourceGeneration: 3,
            AdapterResourceKey.FromString(resourceKey),
            AdapterResourceKey.FromString(RuntimeAttributionIds.ResourceManagerSelf),
            OwnerInstanceIdLow: targetKey,
            OwnerInstanceIdHigh: 0,
            OwnerContextGeneration: 4,
            LeaseGeneration: 5,
            BindingGeneration: 6,
            CapabilityGeneration: 7,
            SchedulingRevision: 8,
            ExecutorIdLow: hostSessionIncarnation,
            ExecutorIdHigh: 0,
            ActionAttemptIdLow: 11,
            ActionAttemptIdHigh: 13,
            ProjectionEpoch: 17);
        return new HostManagerResourceTransactionPayload(
            JournalTransactionIdLow: 11,
            JournalTransactionIdHigh: 13,
            JournalActionId: 19,
            new ResourceSchedulerReservationToken(
                StateGeneration: 23,
                SlotGeneration: 29,
                PendingGeneration: 19,
                SlotIndex: 0),
            new ResourceSchedulerSelection(
                targetKey,
                RequestId: 31,
                authority,
                SizeBytes: 4096,
                EstimatedReleaseBytes: 2048,
                ConfigurationGeneration: 37,
                FinalImportance: 1,
                CandidateIndex: 0,
                TargetInputIndex: 0,
                AdapterResourceTier.PhysicalMemory,
                ActivityScore: 0),
            new ResourceSchedulerCapacity(
                TotalVramBytes: 0,
                FreeVramBytes: 0,
                TotalPhysicalBytes: 8192,
                FreePhysicalBytes: 4096,
                TotalVirtualBytes: 0,
                FreeVirtualBytes: 0,
                FallbackVramFreeRatio: 1,
                FallbackPhysicalFreeRatio: 0.5,
                FallbackVirtualFreeRatio: 1,
                FreeBytesValidMask: 0b010),
            DeadlineTimestamp: 47,
            PlanLease: 53,
            PlanEpoch: 59,
            JournalConfigurationGeneration: 61,
            hostSessionIncarnation);
    }

    private static HostManagerResourceTransactionPayload WithAuthority(
        HostManagerResourceTransactionPayload payload,
        ResourceSchedulerExecutionAuthority authority)
        => payload with
        {
            Selection = payload.Selection with { Authority = authority }
        };
}
