namespace ResourceManager.App.Domain.RuntimeSpecialization;

public sealed record CompiledHostManagerOperationCoordinatorCapacityPlan(
    uint MaximumOperationCount,
    uint MaximumDomainCount,
    uint MaximumActionCount,
    uint OperationIndexCapacity,
    uint DomainIndexCapacity,
    uint MaximumReadCount,
    ulong MaximumPersistenceByteCount,
    ulong ResidentByteBudget,
    uint MaximumPayloadCount,
    ulong MaximumPayloadByteCount,
    uint MaximumEffectReceiptCount,
    ulong MaximumEnvelopeByteCount)
{
    public bool IsPublished => MaximumOperationCount > 0
        && MaximumDomainCount > 0
        && MaximumDomainCount <= MaximumOperationCount
        && MaximumActionCount > 0
        && MaximumActionCount <= MaximumOperationCount
        && OperationIndexCapacity >= MaximumOperationCount
        && DomainIndexCapacity >= MaximumDomainCount
        && MaximumReadCount == MaximumOperationCount
        && MaximumPersistenceByteCount > 0
        && ResidentByteBudget > 0
        && MaximumPayloadCount > 0
        && MaximumPayloadByteCount > 0
        && MaximumEffectReceiptCount > 0
        && MaximumEnvelopeByteCount > 0;

    public bool FitsWithin(CompiledHostManagerOperationCoordinatorCapacityPlan maximum)
        => MaximumOperationCount <= maximum.MaximumOperationCount
            && MaximumDomainCount <= maximum.MaximumDomainCount
            && MaximumActionCount <= maximum.MaximumActionCount
            && OperationIndexCapacity <= maximum.OperationIndexCapacity
            && DomainIndexCapacity <= maximum.DomainIndexCapacity
            && MaximumReadCount <= maximum.MaximumReadCount
            && MaximumPersistenceByteCount <= maximum.MaximumPersistenceByteCount
            && ResidentByteBudget <= maximum.ResidentByteBudget
            && MaximumPayloadCount <= maximum.MaximumPayloadCount
            && MaximumPayloadByteCount <= maximum.MaximumPayloadByteCount
            && MaximumEffectReceiptCount <= maximum.MaximumEffectReceiptCount
            && MaximumEnvelopeByteCount <= maximum.MaximumEnvelopeByteCount;

    public static CompiledHostManagerOperationCoordinatorCapacityPlan Unpublished { get; } =
        new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
}

