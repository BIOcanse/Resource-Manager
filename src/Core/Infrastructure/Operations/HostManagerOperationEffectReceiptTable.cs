using ResourceManager.App.Infrastructure.NativeCore;

namespace ResourceManager.App.Infrastructure.Operations;

internal enum HostManagerOperationEffectReceiptState : uint
{
    Prepared = 1,
    Started = 2,
    Observed = 3,
    Settled = 4,
    Uncertain = 5
}

internal enum HostManagerOperationEffectReceiptOutcome : uint
{
    None = 0,
    EffectNotObserved = 1,
    Succeeded = 2,
    RetryableFailure = 3,
    TerminalFailure = 4,
    Canceled = 5,
    Uncertain = 6
}

internal sealed record HostManagerOperationEffectReceipt(
    HostManagerOperationEffectReceiptState State,
    HostManagerOperationEffectReceiptOutcome Outcome,
    NativeOperationHandle128 ReceiptId,
    NativeOperationHandle128 SessionInstanceId,
    NativeOperationHandle128 OperationId,
    NativeOperationHandle128 AttemptToken,
    ulong ActionId,
    ulong PlanEpoch,
    ulong ConfigurationGeneration,
    ulong ReceiptRevision,
    NativeOperationActionKind ActionKind,
    uint EffectKind,
    uint EffectPhase,
    long CreatedUtcMilliseconds,
    long UpdatedUtcMilliseconds,
    NativeOperationHandle128 ExpectedBeforeHandle,
    NativeOperationHandle128 ExpectedAfterHandle,
    NativeOperationHandle128 ObservationHandle,
    NativeOperationHandle128 ErrorHandle);

internal sealed class HostManagerOperationEffectReceiptTable
{
    private readonly Dictionary<NativeOperationHandle128, HostManagerOperationEffectReceipt>
        entries = [];

    internal int Count => entries.Count;

    internal HostManagerOperationEffectReceipt Require(
        NativeOperationHandle128 receiptId)
        => entries.TryGetValue(receiptId, out var receipt)
            ? receipt
            : throw new InvalidDataException(
                "The operation effect receipt is absent.");

    internal HostManagerOperationEffectReceipt? FindLatest(
        NativeOperationHandle128 operationId,
        NativeOperationHandle128 attemptToken,
        NativeOperationHandle128 excludedReceiptId)
        => entries.Values
            .Where(receipt =>
                receipt.OperationId == operationId
                && receipt.AttemptToken == attemptToken
                && receipt.ReceiptId != excludedReceiptId)
            .OrderByDescending(static receipt => receipt.ReceiptRevision)
            .ThenByDescending(static receipt => receipt.UpdatedUtcMilliseconds)
            .FirstOrDefault();

    internal void Upsert(HostManagerOperationEffectReceipt receipt)
    {
        Validate(receipt);
        if (entries.TryGetValue(receipt.ReceiptId, out var current))
        {
            if (!SameIdentity(current, receipt)
                || receipt.ReceiptRevision <= current.ReceiptRevision
                || receipt.CreatedUtcMilliseconds != current.CreatedUtcMilliseconds)
            {
                throw new InvalidDataException(
                    "The operation effect receipt transition is non-monotonic.");
            }
        }
        entries[receipt.ReceiptId] = receipt;
    }

    internal HostManagerOperationEffectReceipt[] ExportCanonical()
        => entries.Values
            .OrderBy(static value => value.OperationId.High)
            .ThenBy(static value => value.OperationId.Low)
            .ThenBy(static value => value.AttemptToken.High)
            .ThenBy(static value => value.AttemptToken.Low)
            .ThenBy(static value => value.ActionId)
            .ThenBy(static value => value.EffectKind)
            .ThenBy(static value => value.EffectPhase)
            .ThenBy(static value => value.ReceiptId.High)
            .ThenBy(static value => value.ReceiptId.Low)
            .ToArray();

    internal void Import(HostManagerOperationEffectReceipt receipt)
    {
        Validate(receipt);
        if (!entries.TryAdd(receipt.ReceiptId, receipt))
        {
            throw new InvalidDataException(
                "The operation effect receipt catalog contains a duplicate identity.");
        }
    }

    internal HostManagerOperationEffectReceiptTable Clone()
    {
        var result = new HostManagerOperationEffectReceiptTable();
        foreach (var receipt in entries.Values)
        {
            result.Import(receipt);
        }
        return result;
    }

    internal void ReplaceWith(HostManagerOperationEffectReceiptTable source)
    {
        ArgumentNullException.ThrowIfNull(source);
        entries.Clear();
        foreach (var receipt in source.entries.Values)
        {
            Import(receipt);
        }
    }

    internal void PruneTo(IReadOnlySet<NativeOperationHandle128> liveOperationIds)
    {
        ArgumentNullException.ThrowIfNull(liveOperationIds);
        foreach (var receiptId in entries
                     .Where(pair => !liveOperationIds.Contains(pair.Value.OperationId))
                     .Select(static pair => pair.Key)
                     .ToArray())
        {
            entries.Remove(receiptId);
        }
    }

    internal void AddPayloadRoots(HashSet<NativeOperationHandle128> roots)
    {
        ArgumentNullException.ThrowIfNull(roots);
        foreach (var receipt in entries.Values)
        {
            AddIfNonzero(receipt.ExpectedBeforeHandle);
            AddIfNonzero(receipt.ExpectedAfterHandle);
            AddIfNonzero(receipt.ObservationHandle);
            AddIfNonzero(receipt.ErrorHandle);
        }

        void AddIfNonzero(NativeOperationHandle128 handle)
        {
            if (!handle.IsZero)
            {
                roots.Add(handle);
            }
        }
    }

    private static void Validate(HostManagerOperationEffectReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        if (!Enum.IsDefined(receipt.State)
            || !Enum.IsDefined(receipt.Outcome)
            || receipt.ReceiptId.IsZero
            || receipt.SessionInstanceId.IsZero
            || receipt.OperationId.IsZero
            || receipt.AttemptToken.IsZero
            || receipt.ActionId == 0
            || receipt.PlanEpoch == 0
            || receipt.ConfigurationGeneration == 0
            || receipt.ReceiptRevision == 0
            || !Enum.IsDefined(receipt.ActionKind)
            || receipt.EffectKind == 0
            || receipt.EffectPhase == 0
            || receipt.CreatedUtcMilliseconds < 0
            || receipt.UpdatedUtcMilliseconds < receipt.CreatedUtcMilliseconds
            || receipt.ExpectedBeforeHandle.IsZero
            || !HasCanonicalEvidenceShape(receipt))
        {
            throw new InvalidDataException(
                "The operation effect receipt is non-canonical.");
        }
    }

    private static bool HasCanonicalEvidenceShape(
        HostManagerOperationEffectReceipt receipt)
        => receipt.Outcome switch
        {
            HostManagerOperationEffectReceiptOutcome.None =>
                receipt.State is HostManagerOperationEffectReceiptState.Prepared
                    or HostManagerOperationEffectReceiptState.Started
                && receipt.ObservationHandle.IsZero
                && receipt.ErrorHandle.IsZero,
            HostManagerOperationEffectReceiptOutcome.Succeeded =>
                receipt.State == HostManagerOperationEffectReceiptState.Settled
                && !receipt.ObservationHandle.IsZero
                && receipt.ErrorHandle.IsZero,
            HostManagerOperationEffectReceiptOutcome.Uncertain =>
                receipt.State == HostManagerOperationEffectReceiptState.Uncertain
                && receipt.ObservationHandle.IsZero
                && !receipt.ErrorHandle.IsZero,
            HostManagerOperationEffectReceiptOutcome.EffectNotObserved
                or HostManagerOperationEffectReceiptOutcome.RetryableFailure
                or HostManagerOperationEffectReceiptOutcome.TerminalFailure
                or HostManagerOperationEffectReceiptOutcome.Canceled =>
                receipt.State == HostManagerOperationEffectReceiptState.Settled
                && receipt.ObservationHandle.IsZero
                && !receipt.ErrorHandle.IsZero,
            _ => false
        };

    private static bool SameIdentity(
        HostManagerOperationEffectReceipt left,
        HostManagerOperationEffectReceipt right)
        => left.ReceiptId == right.ReceiptId
            && left.SessionInstanceId == right.SessionInstanceId
            && left.OperationId == right.OperationId
            && left.AttemptToken == right.AttemptToken
            && left.ActionId == right.ActionId
            && left.PlanEpoch == right.PlanEpoch
            && left.ConfigurationGeneration == right.ConfigurationGeneration
            && left.ActionKind == right.ActionKind
            && left.EffectKind == right.EffectKind
            && left.EffectPhase == right.EffectPhase
            && left.ExpectedBeforeHandle == right.ExpectedBeforeHandle
            && left.ExpectedAfterHandle == right.ExpectedAfterHandle;
}
