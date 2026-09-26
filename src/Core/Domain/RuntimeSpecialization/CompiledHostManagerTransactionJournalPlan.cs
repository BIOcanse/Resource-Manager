namespace ResourceManager.App.Domain.RuntimeSpecialization;

public sealed record CompiledHostManagerTransactionJournalRecreatePlan(
    int RecordCapacity,
    long ResidentByteBudget,
    int PayloadCount,
    long PayloadByteBudget,
    string JournalRelativePath,
    string PayloadRelativeDirectory)
{
    public bool IsPublished => RecordCapacity > 0
        && ResidentByteBudget > 0
        && PayloadCount > 0
        && PayloadByteBudget > 0
        && !string.IsNullOrWhiteSpace(JournalRelativePath)
        && !string.IsNullOrWhiteSpace(PayloadRelativeDirectory);

    public static CompiledHostManagerTransactionJournalRecreatePlan Unpublished { get; } = new(
        0,
        0,
        0,
        0,
        string.Empty,
        string.Empty);
}

public sealed record CompiledHostManagerTransactionJournalHotPublishPlan(
    ulong ConfigurationGeneration,
    int MaximumRecoveryAttempts,
    int RetryDelayMilliseconds,
    int RecoveryDeadlineMilliseconds,
    int MaximumFutureSkewMilliseconds,
    int ShutdownDrainTimeoutMilliseconds)
{
    public bool IsPublished => ConfigurationGeneration > 0
        && MaximumRecoveryAttempts > 0
        && RetryDelayMilliseconds > 0
        && RecoveryDeadlineMilliseconds > 0
        && MaximumFutureSkewMilliseconds >= 0
        && ShutdownDrainTimeoutMilliseconds > 0;

    public static CompiledHostManagerTransactionJournalHotPublishPlan Unpublished { get; } = new(
        0,
        0,
        0,
        0,
        0,
        0);
}
