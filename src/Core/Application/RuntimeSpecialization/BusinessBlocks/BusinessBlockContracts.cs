namespace ResourceManager.App.Application.RuntimeSpecialization.BusinessBlocks;

public enum BusinessBlockState { Stopped, Starting, Running, Stopping }
public enum BusinessBlockOutcome { Done, Stopped, Failed }
public enum BusinessBlockRequestKind { Configuration, User, Dependency }
public enum BusinessBlockFailureStage { Start, Run, Stop, ReportedFailure }

/// <summary>The entry reports Running and returns only after releasing its resources.</summary>
public interface IBusinessBlock
{
    Task RunAsync(BusinessBlockRunContext context);
}

/// <summary>A data-need fact. Ready/Released acknowledge its parent's decision, not producer readiness.</summary>
public interface IBusinessBlockDependency : IDisposable, IAsyncDisposable
{
    Task Ready { get; }
    Task Released { get; }
}

public sealed record BusinessBlockResult(BusinessBlockOutcome Outcome, string? FailureType = null, BusinessBlockFailureStage? FailureStage = null)
{
    internal Exception? Error { get; init; }
    public static BusinessBlockResult Done { get; } = new(BusinessBlockOutcome.Done);
    public static BusinessBlockResult Stopped { get; } = new(BusinessBlockOutcome.Stopped);
    public static BusinessBlockResult Failed(Exception error, BusinessBlockFailureStage stage = BusinessBlockFailureStage.Run)
        => new(BusinessBlockOutcome.Failed, error.GetType().FullName, stage) { Error = error };
}

public sealed record BusinessBlockReason(
    BusinessBlockRequestKind Kind, string Owner, string ConfigurationOrDependency, long PlanVersion);

public sealed class BusinessBlockOptions
{
    public const string SectionName = "BusinessBlocks";
    public int HistoryLimit { get; set; } = 128;
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

public sealed record BusinessBlockFacts(string Id, BusinessBlockState State, string ParentId,
    IReadOnlyList<string> Inputs, IReadOnlyList<string> Outputs);
public sealed record BusinessBlockDiagnostics(string Id, BusinessBlockResult? Result, BusinessBlockMetricFacts Metrics);
public sealed record BusinessBlockDebugSnapshot(BusinessBlocksSnapshot Scheduling, IReadOnlyList<BusinessBlockDiagnostics> Diagnostics);

public sealed record BusinessBlocksSnapshot(DateTimeOffset At, int HistoryLimit,
    IReadOnlyList<BusinessBlockFacts> Blocks, IReadOnlyList<BusinessBlockControlTransition> History)
{
    public IReadOnlyList<BusinessBlockNode> Graph { get; init; } = [];
    public IReadOnlyList<BusinessBlockDemand> Demands { get; init; } = [];
    public IReadOnlyList<BusinessBlockDecision> Decisions { get; init; } = [];
    public IReadOnlyList<BusinessBlockDecision> CurrentDecisions { get; init; } = [];
    public IReadOnlyList<BusinessBlockFact> Facts { get; init; } = [];
    public bool ShuttingDown { get; init; }
}

public sealed record BusinessBlockNode(string Id, string ParentId,
    IReadOnlyList<string> Inputs, IReadOnlyList<string> Outputs);
public sealed record BusinessBlockDemand(string Key, string BlockId, BusinessBlockReason Reason);
/// <summary>Authoritative boolean conditions used by today's rules; execution details are not facts here.</summary>
public sealed record BusinessBlockFact(string Key, bool Value, IReadOnlyList<BusinessBlockReason> Reasons);
public sealed record BusinessBlockDecision(long Sequence, string ParentId, string Transition,
    BusinessBlockReason Reason, IReadOnlyList<string> ShouldRun, IReadOnlyList<BusinessBlockFact> Facts);
public sealed record BusinessBlockDecisionInput(IReadOnlyDictionary<string, BusinessBlockFact> Facts,
    IReadOnlyDictionary<string, BusinessBlockState> Children);
public sealed record BusinessBlockControlTransition(long Sequence, DateTimeOffset At, string BlockId,
    BusinessBlockState From, BusinessBlockState To);
public sealed record BusinessBlockControlSnapshot(IReadOnlyDictionary<string, BusinessBlockState> States,
    IReadOnlyList<BusinessBlockControlTransition> History);
public sealed record BusinessBlockRunContext(CancellationToken StopNotification, Action ReportRunning);
