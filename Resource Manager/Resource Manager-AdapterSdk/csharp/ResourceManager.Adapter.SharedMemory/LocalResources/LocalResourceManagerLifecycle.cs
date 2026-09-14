namespace ResourceManager.Adapter.LocalResources;

public enum LocalResourceManagerCloseResult : byte
{
    Closed = 1,
    RecoveryRequired = 2
}

public sealed class LocalResourceRecoveryRequiredException : InvalidOperationException
{
    public LocalResourceRecoveryRequiredException(int pendingExecutionCount)
        : base(
            $"The local resource manager still owns {pendingExecutionCount} unresolved execution(s).")
    {
        if (pendingExecutionCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pendingExecutionCount));
        }
        PendingExecutionCount = pendingExecutionCount;
    }

    public int PendingExecutionCount { get; }
}

public enum LocalResourceManagerTickPhase : byte
{
    Capacity = 1,
    Mode = 2
}

public sealed record LocalResourceTableTickFailure(
    LocalResourceId TableId,
    ulong TableIncarnation,
    LocalResourceManagerTickPhase Phase,
    Exception Cause);

public sealed class LocalResourceManagerTickFailedException : InvalidOperationException
{
    internal LocalResourceManagerTickFailedException(
        LocalResourceManagerTickResult partialResult,
        IReadOnlyList<LocalResourceTableTickFailure> failures)
        : base(
            "One or more local resource table phases failed after all sibling tasks were settled.",
            CreateInnerException(failures))
    {
        ArgumentNullException.ThrowIfNull(partialResult);
        PartialResult = partialResult;
        Failures = Array.AsReadOnly(failures.ToArray());
    }

    public LocalResourceManagerTickResult PartialResult { get; }
    public IReadOnlyList<LocalResourceTableTickFailure> Failures { get; }

    private static Exception CreateInnerException(
        IReadOnlyList<LocalResourceTableTickFailure> failures)
    {
        ArgumentNullException.ThrowIfNull(failures);
        if (failures.Count == 0)
        {
            throw new ArgumentException("At least one table failure is required.", nameof(failures));
        }

        return failures.Count == 1
            ? failures[0].Cause
            : new AggregateException(failures.Select(static failure => failure.Cause));
    }
}
