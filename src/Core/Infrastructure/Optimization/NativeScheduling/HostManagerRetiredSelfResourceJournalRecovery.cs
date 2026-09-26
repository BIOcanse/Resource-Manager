using ResourceManager.Adapter;
using ResourceManager.Adapter.NativeScheduling;
using ResourceManager.App.Domain.ProcessAttribution;
using ResourceManager.App.Infrastructure.NativeCore;

namespace ResourceManager.App.Infrastructure.Optimization.NativeScheduling;

internal enum HostManagerRetiredSelfResourceJournalDecision
{
    MarkEffectInvocationUncertain,
    SettleKnownNoEffect,
    AcknowledgeSealedFeedback,
    AcknowledgeStateUncertain,
    CompleteAuthoritativeResync,
    PreserveBlocked
}

internal static class HostManagerRetiredSelfResourceJournalRecovery
{
    private static readonly ulong OwnerApplicationKey =
        AdapterResourceKey.FromString(RuntimeAttributionIds.ResourceManagerSelf);
    private static readonly ulong BackendWorkingSetKey =
        AdapterResourceKey.FromString(
            "resource-manager:memory:backend-working-set");
    private static readonly ulong NativeUiWorkingSetKey =
        AdapterResourceKey.FromString(
            "resource-manager:memory:native-ui-working-set");

    internal static void RequireExactPayload(
        HostManagerResourceTransactionPayload payload)
    {
        var authority = payload.Selection.Authority;
        var exactResource =
            authority.ResourceKey == BackendWorkingSetKey
                && authority.ResourceId == 1
            || authority.ResourceKey == NativeUiWorkingSetKey
                && authority.ResourceId == 3;
        if (!payload.IsValid
            || payload.Selection.Tier != AdapterResourceTier.PhysicalMemory
            || authority.Source != ResourceSchedulerSource.AdaptedPrivate
            || authority.Action != AdapterResourceActionMask.Trim
            || authority.ActionRoute != AdapterResourceActionRoute.ManagerDirect
            || authority.Flags !=
                ResourceSchedulerExecutionAuthority.HostSelfExecutorProof
            || authority.OwnerApplicationKey != OwnerApplicationKey
            || authority.OwnerInstanceIdHigh != 0
            || authority.ExecutorIdLow != payload.HostSessionIncarnation
            || authority.ExecutorIdHigh != 0
            || !exactResource)
        {
            throw new InvalidDataException(
                "The resource journal payload is not an exact retired Host-self working-set trim.");
        }
    }

    internal static HostManagerRetiredSelfResourceJournalDecision Classify(
        NativeTransactionJournalRecord record)
    {
        if (!Enum.IsDefined((NativeTransactionJournalPhase)record.Phase))
        {
            throw new InvalidDataException(
                "The retired Host-self journal phase is invalid.");
        }

        var phase = (NativeTransactionJournalPhase)record.Phase;
        if (phase == NativeTransactionJournalPhase.FeedbackPending)
        {
            var feedback =
                (NativeTransactionJournalFeedbackStatus)record.FeedbackStatus;
            if (!Enum.IsDefined(feedback))
            {
                throw new InvalidDataException(
                    "The retired Host-self journal feedback status is invalid.");
            }
            return feedback == NativeTransactionJournalFeedbackStatus.StateUncertain
                ? HostManagerRetiredSelfResourceJournalDecision
                    .AcknowledgeStateUncertain
                : HostManagerRetiredSelfResourceJournalDecision
                    .AcknowledgeSealedFeedback;
        }

        return phase switch
        {
            NativeTransactionJournalPhase.Prepared =>
                HostManagerRetiredSelfResourceJournalDecision
                    .MarkEffectInvocationUncertain,
            NativeTransactionJournalPhase.PreviousEffectRestored =>
                HostManagerRetiredSelfResourceJournalDecision
                    .SettleKnownNoEffect,
            NativeTransactionJournalPhase.AuthoritativeResyncPending =>
                HostManagerRetiredSelfResourceJournalDecision
                    .CompleteAuthoritativeResync,
            NativeTransactionJournalPhase.EffectObserved
                or NativeTransactionJournalPhase.ReconciliationPending
                or NativeTransactionJournalPhase.RecoveryRetryPending
                or NativeTransactionJournalPhase.RecoveryBlocked
                or NativeTransactionJournalPhase.EffectInvocationUncertain =>
                HostManagerRetiredSelfResourceJournalDecision.PreserveBlocked,
            _ => throw new InvalidDataException(
                "The retired Host-self journal phase has no recovery decision.")
        };
    }
}
