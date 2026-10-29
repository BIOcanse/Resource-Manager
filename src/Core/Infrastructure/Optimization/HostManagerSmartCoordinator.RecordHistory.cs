using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Application.GpuPlacement;
using ResourceManager.App.Infrastructure.GpuPlacement.External;

namespace ResourceManager.App.Infrastructure.Optimization;

public sealed partial class HostManagerSmartCoordinator
{
    internal static string HistoryResultFromSettlement(HostManagerPlacementRecordSettlement settlement)
    {
        if (settlement.HistoryRestoreResult is { } result)
        {
            if (!HostManagerSmartRecordRestoreResults.IsSupported(result))
                throw new InvalidDataException("Unsupported smart record restore outcome.");
            return result;
        }
        return settlement.Kind switch
        {
            HostManagerPlacementSettlementKind.Restored or HostManagerPlacementSettlementKind.AlreadyRestored
                => HostManagerSmartRecordRestoreResults.Restored,
            HostManagerPlacementSettlementKind.OwnershipLost => HostManagerSmartRecordRestoreResults.OwnershipLost,
            HostManagerPlacementSettlementKind.RetryableFailure or HostManagerPlacementSettlementKind.InvalidReceipt
                => HostManagerSmartRecordRestoreResults.Failed,
            _ => HostManagerSmartRecordRestoreResults.Unknown
        };
    }

    internal static string HistoryResultFromAbsence(RecoveryReadAbsenceReason reason)
        => reason switch
        {
            RecoveryReadAbsenceReason.TargetExited => HostManagerSmartRecordRestoreResults.TargetExited,
            RecoveryReadAbsenceReason.IdentityChanged => HostManagerSmartRecordRestoreResults.OwnershipLost,
            _ => HostManagerSmartRecordRestoreResults.Unknown
        };

    internal static string HistoryResultFromRejectedBaseline(HostManagerPlacementValueRelation relation,
        RecoveryReadAbsenceReason absenceReason)
        => relation switch
        {
            HostManagerPlacementValueRelation.NotFound => HistoryResultFromAbsence(absenceReason),
            HostManagerPlacementValueRelation.Unavailable => HostManagerSmartRecordRestoreResults.Failed,
            _ => HostManagerSmartRecordRestoreResults.OwnershipLost
        };

    internal static string HistoryResultFromWriteReadBack(HostManagerPlacementValueRelation relation,
        RecoveryReadAbsenceReason absenceReason)
        => relation switch
        {
            HostManagerPlacementValueRelation.Previous => HostManagerSmartRecordRestoreResults.Failed,
            HostManagerPlacementValueRelation.NotFound => HistoryResultFromAbsence(absenceReason),
            HostManagerPlacementValueRelation.Foreign => HostManagerSmartRecordRestoreResults.OwnershipLost,
            _ => HostManagerSmartRecordRestoreResults.Unknown
        };

    internal static string HistoryResultFromExternalAction(RunningGpuPlacementActionResult result)
    {
        if (result.HistoryRestoreResult is { } known)
        {
            if (!HostManagerSmartRecordRestoreResults.IsSupported(known))
                throw new InvalidDataException("Unsupported external action history outcome.");
            return known;
        }
        if (WindowsExternalGpuPlacementRuntime.IsUnconfirmedObservation(result))
            return HostManagerSmartRecordRestoreResults.Unknown;
        return result.Status is RunningGpuPlacementActionStatuses.Skipped or RunningGpuPlacementActionStatuses.NotApplied
            ? HostManagerSmartRecordRestoreResults.Failed : HostManagerSmartRecordRestoreResults.Unknown;
    }
}
