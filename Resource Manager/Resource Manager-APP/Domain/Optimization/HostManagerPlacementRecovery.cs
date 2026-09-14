namespace ResourceManager.App.Domain.Optimization;

public enum HostManagerPlacementSettlementKind : byte
{
    Restored = 1,
    AlreadyRestored = 2,
    OwnershipLost = 3,
    RetryableFailure = 4,
    InvalidReceipt = 5,
    RetainedActionFact = 6
}

public sealed record HostManagerPlacementRecordSettlement(
    HostManagerAppliedRecord Record,
    HostManagerPlacementSettlementKind Kind,
    int NativeErrorCode,
    string Message)
{
    public bool CanRemoveReceipt => Kind is
        HostManagerPlacementSettlementKind.Restored
        or HostManagerPlacementSettlementKind.AlreadyRestored
        or HostManagerPlacementSettlementKind.OwnershipLost;
}

public readonly record struct HostManagerPlacementReceiptKey(
    string ResourceKind,
    string TargetId)
{
    public static HostManagerPlacementReceiptKey Create(HostManagerAppliedPlacementReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        return Create(receipt.ResourceKind, receipt.TargetId);
    }

    public static HostManagerPlacementReceiptKey Create(string resourceKind, string targetId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceKind);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetId);
        return new HostManagerPlacementReceiptKey(
            resourceKind.Trim().ToUpperInvariant(),
            targetId.Trim().ToUpperInvariant());
    }
}
