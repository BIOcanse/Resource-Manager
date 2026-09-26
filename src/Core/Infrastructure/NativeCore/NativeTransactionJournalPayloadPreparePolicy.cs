namespace ResourceManager.App.Infrastructure.NativeCore;

internal static class NativeTransactionJournalPayloadPreparePolicy
{
    internal static bool ShouldDeleteAfterFailure(
        bool payloadOwnedByAttempt,
        bool prepareCompleted,
        bool prepareRejectedWithoutMutation,
        Exception failure)
        => payloadOwnedByAttempt
            && !prepareCompleted
            && !NativeTransactionJournalCommitException.IsCommitAmbiguous(failure)
            && (prepareRejectedWithoutMutation
                || NativeTransactionJournalCommitException
                    .IsDefinitelyNotCommitted(failure));
}
