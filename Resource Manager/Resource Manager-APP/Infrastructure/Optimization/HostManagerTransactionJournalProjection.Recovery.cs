using ResourceManager.App.Infrastructure.NativeCore;

namespace ResourceManager.App.Infrastructure.Optimization;

internal static partial class HostManagerTransactionJournalProjection
{
    public static NativeTransactionJournalPayloadReference CreatePayloadReference(
        in NativeTransactionJournalRecord record)
    {
        ValidateDurableRecord(in record);
        return new NativeTransactionJournalPayloadReference(
            record.PayloadSlot,
            record.PayloadGeneration,
            record.PayloadLength,
            record.PayloadDigestLow,
            record.PayloadDigestHigh);
    }

    public static NativeTransactionJournalPayloadProvenance CreatePayloadProvenance(
        in NativeTransactionJournalRecord record)
    {
        ValidateDurableRecord(in record);
        return new NativeTransactionJournalPayloadProvenance(
            record.PayloadProvenanceDigestLow,
            record.PayloadProvenanceDigestHigh);
    }

    public static NativeTransactionJournalRecoveryEvidenceInput CreateRecoveryEvidence(
        in NativeTransactionJournalRecord record,
        ulong expectedJournalRevision,
        NativeTransactionJournalRecoveryOutcome outcome,
        ulong observedAtUtcMilliseconds,
        ulong retryNotBeforeUtcMilliseconds,
        uint stableSystemStatus,
        uint stableSystemError,
        ulong authoritativeFactsGeneration = 0)
    {
        ValidateRecordIdentity(in record);
        if (expectedJournalRevision == 0 ||
            observedAtUtcMilliseconds == 0 ||
            observedAtUtcMilliseconds < record.UpdatedAtUtcMilliseconds ||
            !Enum.IsDefined(outcome))
        {
            throw new ArgumentException("The recovery evidence identity or time is invalid.");
        }

        var requiresRetryTime = outcome is NativeTransactionJournalRecoveryOutcome.RetryableFailure
            or NativeTransactionJournalRecoveryOutcome.Unavailable;
        if (requiresRetryTime != (retryNotBeforeUtcMilliseconds != 0) ||
            (retryNotBeforeUtcMilliseconds != 0 &&
                retryNotBeforeUtcMilliseconds <= observedAtUtcMilliseconds) ||
            (outcome == NativeTransactionJournalRecoveryOutcome.AuthoritativeResyncCompleted)
                != (authoritativeFactsGeneration != 0))
        {
            throw new ArgumentException("The recovery evidence payload is inconsistent.");
        }

        return new NativeTransactionJournalRecoveryEvidenceInput
        {
            AbiVersion = NativeTransactionJournalAbi.Version,
            StructSize = NativeTransactionJournalSession
                .SizeOf<NativeTransactionJournalRecoveryEvidenceInput>(),
            Identity = record.Identity,
            ExpectedJournalRevision = expectedJournalRevision,
            ExpectedEntryRevision = record.EntryRevision,
            ObservedAtUtcMilliseconds = observedAtUtcMilliseconds,
            RetryNotBeforeUtcMilliseconds = retryNotBeforeUtcMilliseconds,
            Outcome = (uint)outcome,
            ExpectedPhase = record.Phase,
            StableSystemStatus = stableSystemStatus,
            StableSystemError = stableSystemError,
            Flags = 0,
            AuthoritativeFactsGeneration = authoritativeFactsGeneration
        };
    }

    public static NativeTransactionJournalAckInput CreateAck(
        in NativeTransactionJournalRecord record,
        ulong expectedJournalRevision,
        NativeTransactionJournalAckResult result,
        ulong acknowledgedAtUtcMilliseconds,
        ulong retryNotBeforeUtcMilliseconds,
        uint stableSystemStatus,
        uint stableSystemError)
    {
        ValidateRecordIdentity(in record);
        if (expectedJournalRevision == 0 ||
            record.Phase != (uint)NativeTransactionJournalPhase.FeedbackPending ||
            acknowledgedAtUtcMilliseconds == 0 ||
            acknowledgedAtUtcMilliseconds < record.UpdatedAtUtcMilliseconds ||
            !Enum.IsDefined(result))
        {
            throw new ArgumentException("The transaction-journal acknowledgement is invalid.");
        }

        var requiresRetryTime = result == NativeTransactionJournalAckResult.Uncertain;
        if (requiresRetryTime != (retryNotBeforeUtcMilliseconds != 0) ||
            (retryNotBeforeUtcMilliseconds != 0 &&
                retryNotBeforeUtcMilliseconds <= acknowledgedAtUtcMilliseconds))
        {
            throw new ArgumentException(
                "The transaction-journal acknowledgement retry time is invalid.");
        }

        return new NativeTransactionJournalAckInput
        {
            AbiVersion = NativeTransactionJournalAbi.Version,
            StructSize = NativeTransactionJournalSession.SizeOf<NativeTransactionJournalAckInput>(),
            Identity = record.Identity,
            ExpectedJournalRevision = expectedJournalRevision,
            ExpectedEntryRevision = record.EntryRevision,
            AcknowledgedAtUtcMilliseconds = acknowledgedAtUtcMilliseconds,
            RetryNotBeforeUtcMilliseconds = retryNotBeforeUtcMilliseconds,
            Result = (uint)result,
            ExpectedPhase = record.Phase,
            StableSystemStatus = stableSystemStatus,
            StableSystemError = stableSystemError,
            Flags = 0
        };
    }

    private static void ValidateDurableRecord(in NativeTransactionJournalRecord record)
    {
        ValidateRecordIdentity(in record);
        var reference = new NativeTransactionJournalPayloadReference(
            record.PayloadSlot,
            record.PayloadGeneration,
            record.PayloadLength,
            record.PayloadDigestLow,
            record.PayloadDigestHigh);
        if (record.PayloadKind != (uint)NativeTransactionJournalPayloadKind.Durable ||
            record.PayloadReserved != 0 ||
            !reference.IsValid ||
            !new NativeTransactionJournalPayloadProvenance(
                record.PayloadProvenanceDigestLow,
                record.PayloadProvenanceDigestHigh).IsValid)
        {
            throw new InvalidDataException(
                "The transaction-journal record does not contain a durable payload reference.");
        }
    }

    private static void ValidateRecordIdentity(in NativeTransactionJournalRecord record)
    {
        if (record.EntryRevision == 0 ||
            record.Identity.ConfigurationGeneration == 0 ||
            record.Identity.PlanEpoch == 0 ||
            record.Identity.ActionId == 0 ||
            record.Identity.HostSessionIncarnation == 0 ||
            record.Identity.TargetId == 0 ||
            record.Identity.Reserved != 0 ||
            record.GradeReserved != 0 ||
            record.FeedbackReserved != 0 ||
            record.PayloadReserved != 0)
        {
            throw new InvalidDataException(
                "The transaction-journal record identity is invalid.");
        }
    }
}
