namespace ResourceManager.App.Domain.RuntimeSpecialization;

public sealed record CompiledHostManagerAppliedOwnershipRecreatePlan(
    int RecordCapacity,
    int PrimaryIndexCapacity,
    int PayloadIndexCapacity,
    long ResidentByteBudget,
    long ImageByteBudget,
    string LedgerRelativePath)
{
    public bool IsPublished => RecordCapacity > 0
        && PrimaryIndexCapacity >= RecordCapacity
        && PayloadIndexCapacity >= RecordCapacity
        && ResidentByteBudget > 0
        && ImageByteBudget > 0
        && !string.IsNullOrWhiteSpace(LedgerRelativePath);

    public static CompiledHostManagerAppliedOwnershipRecreatePlan Unpublished { get; } = new(
        0,
        0,
        0,
        0,
        0,
        string.Empty);
}

public sealed record CompiledHostManagerAppliedOwnershipHotPublishPlan(
    ulong ConfigurationGeneration,
    int MaximumFutureSkewMilliseconds,
    int PersistenceRetryDelayMilliseconds,
    int RecoveryDeadlineMilliseconds,
    int ShutdownDrainTimeoutMilliseconds)
{
    public bool IsPublished => ConfigurationGeneration > 0
        && MaximumFutureSkewMilliseconds >= 0
        && PersistenceRetryDelayMilliseconds > 0
        && RecoveryDeadlineMilliseconds > 0
        && ShutdownDrainTimeoutMilliseconds > 0;

    public static CompiledHostManagerAppliedOwnershipHotPublishPlan Unpublished { get; } = new(
        0,
        0,
        0,
        0,
        0);
}
