namespace ResourceManager.App.Application.RuntimeSpecialization.BusinessBlocks;

public enum BusinessBlockState { Stopped, Starting, Running, Stopping, Failed }
public enum BusinessBlockOutcome { Done, Stopped, Failed }
public enum BusinessBlockRequestKind { Configuration, User, Dependency }
public enum BusinessBlockTransitionOutcome { None, Done, Stopped, Failed, StopTimeout }
public enum BusinessBlockTimeoutPolicy { MarkFailed, ContinueWaiting }
public enum BusinessBlockFailurePolicy { RecordOnly, StopBlock, StopDependents }
public enum BusinessBlockFailureStage { Start, Run, Stop, StopDeadline, StopNotification, ReportedFailure }

/// <summary>The controller owns the stop token. Implementations release resources before returning.</summary>
public interface IBusinessBlock
{
    Task<BusinessBlockResult> RunAsync(CancellationToken stopNotification);
}

/// <summary>A counted request for a producer block to remain available.</summary>
public interface IBusinessBlockDependency : IDisposable, IAsyncDisposable
{
    Task Ready { get; }
    Task Released { get; }
}

public sealed record BusinessBlockResult(BusinessBlockOutcome Outcome, string? FailureType = null, BusinessBlockFailureStage? FailureStage = null)
{
    // Full errors cross the execution boundary for logging only, never the diagnostic wire.
    internal Exception? Error { get; init; }
    public static BusinessBlockResult Done { get; } = new(BusinessBlockOutcome.Done);
    public static BusinessBlockResult Stopped { get; } = new(BusinessBlockOutcome.Stopped);
    public static BusinessBlockResult Failed(Exception error, BusinessBlockFailureStage stage = BusinessBlockFailureStage.Run)
        => new(BusinessBlockOutcome.Failed, error.GetType().FullName, stage) { Error = error };
}

public sealed record BusinessBlockReason(
    BusinessBlockRequestKind Kind, string Owner, string ConfigurationOrDependency, long PlanVersion);

/// <summary>
/// Defaults: no controller deadline; a legacy hosted service receives the host's own stop token.
/// An explicit deadline can be configured. Failures are recorded without changing worker behavior.
/// </summary>
public sealed record BusinessBlockPolicy
{
    public TimeSpan? StopDeadline { get; init; }
    public BusinessBlockTimeoutPolicy OnTimeout { get; init; } = BusinessBlockTimeoutPolicy.MarkFailed;
    public BusinessBlockFailurePolicy OnFailure { get; init; } = BusinessBlockFailurePolicy.RecordOnly;
}

public sealed class BusinessBlockOptions
{
    public const string SectionName = "BusinessBlocks";
    // Global retained transitions, not a separate unbounded history per block. Zero disables history.
    public int HistoryLimit { get; set; } = 128;
    // No periodic wakeup by default. Callers can compare successive cumulative reads.
    public TimeSpan? MetricsInterval { get; set; }
    public BusinessBlockPolicy Policy { get; set; } = new();
    public Dictionary<string, BusinessBlockPolicy> Blocks { get; set; } = new(StringComparer.Ordinal);
}

public interface IBusinessBlockDiagnostics
{
    void ReportFailure(string blockId, Exception error, string operation);
    IDisposable MeasureSegment(string blockId);
}

public interface IBusinessBlockQuery
{
    BusinessBlocksSnapshot Read();
}

public sealed record BusinessBlockTransition(
    long Sequence, DateTimeOffset At, string BlockId, BusinessBlockState From, BusinessBlockState To,
    BusinessBlockReason Reason, BusinessBlockTransitionOutcome Outcome, string? FailureType, BusinessBlockFailureStage? FailureStage);

public sealed record BusinessBlockFacts(
    string Id, BusinessBlockState State, IReadOnlyList<string> Dependencies, BusinessBlockPolicy Policy,
    BusinessBlockReason? LastReason, BusinessBlockTransitionOutcome Outcome, bool ExecutionOutstanding,
    BusinessBlockMetricFacts Metrics)
{
    public bool ObservationOnly { get; init; }
    public bool Enabled { get; init; } = true;
    public IReadOnlyList<BusinessBlockDependencyFacts> ActiveDependencies { get; init; } = Array.Empty<BusinessBlockDependencyFacts>();
}

public sealed record BusinessBlockDependencyFacts(
    BusinessBlockReason Reason, TimeSpan? Interval, IReadOnlyList<string> Outputs)
{
    public string Owner => Reason.Owner;
}

public sealed record BusinessBlocksSnapshot(
    DateTimeOffset At, int HistoryLimit, TimeSpan? MetricsInterval,
    IReadOnlyList<BusinessBlockFacts> Blocks, IReadOnlyList<BusinessBlockTransition> History);
