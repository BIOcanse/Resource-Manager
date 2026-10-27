using System.Text.Json.Serialization;

namespace ResourceManager.Shared.BusinessBlocks;

public enum BusinessBlockState { Stopped, Starting, Running, Stopping }
public enum BusinessBlockRequestKind { Configuration, User, Dependency }

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

public sealed record BusinessBlockReason(
    BusinessBlockRequestKind Kind, string Owner, string ConfigurationOrDependency, long PlanVersion);

public interface IBusinessBlockQuery
{
    BusinessBlocksSnapshot Read();
}

public sealed record BusinessBlockFacts(int Id, BusinessBlockState State, int ParentId,
    IReadOnlyList<int> Inputs)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; init; }
}
public sealed record BusinessBlocksSnapshot(DateTimeOffset At, int HistoryLimit,
    IReadOnlyList<BusinessBlockFacts> Blocks, IReadOnlyList<BusinessBlockControlTransition> History)
{
    public required string RegistryVersion { get; init; }
    public required int Program { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ProgramName { get; init; }
    public IReadOnlyList<BusinessBlockNode> Graph { get; init; } = [];
    public IReadOnlyList<BusinessBlockDemand> Demands { get; init; } = [];
    public IReadOnlyList<BusinessBlockDecision> Decisions { get; init; } = [];
    public IReadOnlyList<BusinessBlockDecision> CurrentDecisions { get; init; } = [];
    public IReadOnlyList<BusinessBlockFact> Facts { get; init; } = [];
    public bool ShuttingDown { get; init; }
}

public sealed record BusinessBlockNode(int Id, int ParentId,
    IReadOnlyList<int> Inputs);
public sealed record BusinessBlockDemand(string Key, int BlockId, BusinessBlockReason Reason);
/// <summary>Authoritative boolean conditions used by today's rules; execution details are not facts here.</summary>
public sealed record BusinessBlockFact(string Key, bool Value, IReadOnlyList<BusinessBlockReason> Reasons);
public sealed record BusinessBlockDecision(long Sequence, int ParentId, string Transition,
    BusinessBlockReason Reason, IReadOnlyList<int> ShouldRun, IReadOnlyList<BusinessBlockFact> Facts);
public sealed record BusinessBlockDecisionInput(IReadOnlyDictionary<string, BusinessBlockFact> Facts,
    IReadOnlyDictionary<int, BusinessBlockState> Children);
public sealed record BusinessBlockControlTransition(long Sequence, DateTimeOffset At, int BlockId,
    BusinessBlockState From, BusinessBlockState To);
public sealed record BusinessBlockControlSnapshot(IReadOnlyDictionary<int, BusinessBlockState> States,
    IReadOnlyList<BusinessBlockControlTransition> History);
public sealed record BusinessBlockRunContext(CancellationToken StopNotification, Action ReportRunning);

/// <summary>A parent's rule reads only these declared facts and its immediate children's states.</summary>
public sealed record BusinessBlockDecisionRegistration(int ParentId, IReadOnlyList<string> FactKeys,
    Func<BusinessBlockDecisionInput, IReadOnlyCollection<int>> Decide);

