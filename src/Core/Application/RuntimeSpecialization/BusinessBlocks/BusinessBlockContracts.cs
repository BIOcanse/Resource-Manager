namespace ResourceManager.App.Application.RuntimeSpecialization.BusinessBlocks;

public enum BusinessBlockState { Stopped, Starting, Running, Stopping, Failed }
public enum BusinessBlockOutcome { Done, Stopped, Failed }
public enum BusinessBlockRequestKind { Configuration, User, Dependency }
public enum BusinessBlockTransitionOutcome { None, Done, Stopped, Failed, StopTimeout }
public enum BusinessBlockTimeoutPolicy { MarkFailed, ContinueWaiting }
public enum BusinessBlockFailurePolicy { RecordOnly, StopBlock, StopDependents }

/// <summary>The controller owns the stop token. Implementations release resources before returning.</summary>
public interface IBusinessBlock
{
    Task<BusinessBlockResult> RunAsync(CancellationToken stopNotification);
}

public sealed record BusinessBlockResult(BusinessBlockOutcome Outcome, string? FailureType = null, string? Failure = null)
{
    public static BusinessBlockResult Done { get; } = new(BusinessBlockOutcome.Done);
    public static BusinessBlockResult Stopped { get; } = new(BusinessBlockOutcome.Stopped);
    public static BusinessBlockResult Failed(Exception error) => new(BusinessBlockOutcome.Failed, error.GetType().FullName, error.Message);
}

public sealed record BusinessBlockReason(
    BusinessBlockRequestKind Kind, string Owner, string ConfigurationOrDependency, long PlanVersion);

/// <summary>
/// Defaults: 30 s cooperative stop; record a timeout without claiming resources were released;
/// report failures without changing existing worker behavior. Null deadline disables the deadline.
/// </summary>
public sealed record BusinessBlockPolicy
{
    public TimeSpan? StopDeadline { get; init; } = TimeSpan.FromSeconds(30);
    public BusinessBlockTimeoutPolicy OnTimeout { get; init; } = BusinessBlockTimeoutPolicy.MarkFailed;
    public BusinessBlockFailurePolicy OnFailure { get; init; } = BusinessBlockFailurePolicy.RecordOnly;
}

public sealed class BusinessBlockOptions
{
    public const string SectionName = "BusinessBlocks";
    // Global retained transitions, not a separate unbounded history per block. Zero disables history.
    public int HistoryLimit { get; set; } = 128;
    public TimeSpan? MetricsInterval { get; set; } = TimeSpan.FromSeconds(1);
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
    BusinessBlockReason Reason, BusinessBlockTransitionOutcome Outcome, string? FailureType, string? Failure);

public sealed record BusinessBlockFacts(
    string Id, BusinessBlockState State, IReadOnlyList<string> Dependencies, BusinessBlockPolicy Policy,
    BusinessBlockReason? LastReason, BusinessBlockTransitionOutcome Outcome, bool ExecutionOutstanding,
    BusinessBlockMetricFacts Metrics)
{
    public bool ObservationOnly { get; init; }
}

public sealed record BusinessBlocksSnapshot(
    DateTimeOffset At, int HistoryLimit, TimeSpan? MetricsInterval,
    IReadOnlyList<BusinessBlockFacts> Blocks, IReadOnlyList<BusinessBlockTransition> History);
