using ResourceManager.App.Infrastructure.NativeCore;

namespace ResourceManager.App.Infrastructure.Optimization;

internal static partial class HostManagerTransactionJournalProjection
{
    private static NativeTransactionJournalScope MapScope(NativeSmartCoordinatorActionScope value)
        => value switch
        {
            NativeSmartCoordinatorActionScope.ProcessPolicy => NativeTransactionJournalScope.Process,
            _ => throw new ArgumentOutOfRangeException(nameof(value), value, null)
        };

    private static NativeSmartCoordinatorActionScope MapFeedbackScope(uint value)
        => (NativeTransactionJournalScope)value switch
        {
            NativeTransactionJournalScope.Process => NativeSmartCoordinatorActionScope.ProcessPolicy,
            _ => throw new ArgumentOutOfRangeException(nameof(value), value, null)
        };

    private static bool MutationMatchesPhase(
        NativeTransactionJournalMutationEvent mutationEvent,
        uint phase)
        => mutationEvent switch
        {
            NativeTransactionJournalMutationEvent.ConfirmPreviousEffectRestored =>
                phase == (uint)NativeTransactionJournalPhase.Prepared,
            NativeTransactionJournalMutationEvent.ConfirmEffectObserved =>
                phase is (uint)NativeTransactionJournalPhase.Prepared or
                    (uint)NativeTransactionJournalPhase.PreviousEffectRestored,
            _ => false
        };

    private static NativeTransactionJournalDisposition MapDisposition(
        NativeSmartCoordinatorActionDisposition value)
        => value switch
        {
            NativeSmartCoordinatorActionDisposition.Apply => NativeTransactionJournalDisposition.Apply,
            NativeSmartCoordinatorActionDisposition.Restore => NativeTransactionJournalDisposition.Restore,
            _ => throw new ArgumentOutOfRangeException(nameof(value), value, null)
        };

    private static NativeTransactionJournalDomain MapDomains(NativeSmartCoordinatorGradeDomains value)
    {
        var result = NativeTransactionJournalDomain.None;
        if (value.HasFlag(NativeSmartCoordinatorGradeDomains.Process))
        {
            result |= NativeTransactionJournalDomain.Process;
        }
        if ((value & ~NativeSmartCoordinatorGradeDomains.Known) != 0 || result == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, null);
        }
        return result;
    }

    private static NativeTransactionJournalGradeValidity MapGradeValidity(
        NativeSmartCoordinatorActionScope scope,
        NativeSmartCoordinatorGradeDomains domains)
        => scope == NativeSmartCoordinatorActionScope.ProcessPolicy &&
            domains == NativeSmartCoordinatorGradeDomains.Process
            ? NativeTransactionJournalGradeValidity.Process
            : throw new ArgumentOutOfRangeException(nameof(scope), scope, null);

    private static NativeTransactionJournalProcessGrade MapProcessGrade(
        NativeSmartCoordinatorProcessGrade value)
        => value switch
        {
            NativeSmartCoordinatorProcessGrade.Level4 => NativeTransactionJournalProcessGrade.Level4,
            NativeSmartCoordinatorProcessGrade.Level3 => NativeTransactionJournalProcessGrade.Level3,
            NativeSmartCoordinatorProcessGrade.Level2 => NativeTransactionJournalProcessGrade.Level2,
            NativeSmartCoordinatorProcessGrade.Level1 => NativeTransactionJournalProcessGrade.Level1,
            NativeSmartCoordinatorProcessGrade.Normal => NativeTransactionJournalProcessGrade.Normal,
            NativeSmartCoordinatorProcessGrade.A1 => NativeTransactionJournalProcessGrade.A1,
            _ => throw new ArgumentOutOfRangeException(nameof(value), value, null)
        };

    private static NativeTransactionJournalFeedbackStatus MapFeedbackStatus(
        NativeSmartCoordinatorFeedbackStatus value)
        => value switch
        {
            NativeSmartCoordinatorFeedbackStatus.Succeeded => NativeTransactionJournalFeedbackStatus.Succeeded,
            NativeSmartCoordinatorFeedbackStatus.FailedUnchanged => NativeTransactionJournalFeedbackStatus.FailedUnchanged,
            NativeSmartCoordinatorFeedbackStatus.Rejected => NativeTransactionJournalFeedbackStatus.Rejected,
            NativeSmartCoordinatorFeedbackStatus.Skipped => NativeTransactionJournalFeedbackStatus.Skipped,
            NativeSmartCoordinatorFeedbackStatus.OwnershipLost => NativeTransactionJournalFeedbackStatus.OwnershipLost,
            NativeSmartCoordinatorFeedbackStatus.StateUncertain => NativeTransactionJournalFeedbackStatus.StateUncertain,
            _ => throw new ArgumentOutOfRangeException(nameof(value), value, null)
        };
}
