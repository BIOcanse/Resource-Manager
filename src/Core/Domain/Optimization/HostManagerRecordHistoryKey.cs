namespace ResourceManager.App.Domain.Optimization;

// Binds a local restoration outcome to its still-active ledger record. Persisted
// history identity and restart reconciliation always use the record's sequence.
public readonly record struct HostManagerRecordHistoryKey(
    HostManagerPlacementReceiptKey Placement, string Kind, string RecordId)
{
    public static HostManagerRecordHistoryKey Create(
        HostManagerAppliedPlacementReceipt placement, HostManagerAppliedRecord record)
        => new(HostManagerPlacementReceiptKey.Create(placement),
            record.Kind.Trim().ToUpperInvariant(), record.RecordId.Trim().ToUpperInvariant());
}

public static class HostManagerSmartRecordRestoreResults
{
    public const string Restored = "restored";
    public const string TargetExited = "targetExited";
    public const string OwnershipLost = "ownershipLost";
    public const string Failed = "failed";
    public const string Unknown = "unknown";

    public static bool IsSupported(string? value)
        => value is Restored or TargetExited or OwnershipLost or Failed or Unknown;
}
