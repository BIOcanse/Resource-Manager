using System.Text.Json.Serialization;
using ResourceManager.Shared.BusinessBlocks.State;

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
    public IReadOnlyList<BusinessBlockItem> Items { get; init; } = [];
    public bool ShuttingDown { get; init; }
    [JsonIgnore]
    public System.Text.Json.JsonElement? NativeSnapshot { get; init; }
}

public sealed record BusinessBlockNode(int Id, int ParentId,
    IReadOnlyList<int> Inputs);
public sealed record BusinessBlockDemand(string Key, int BlockId, BusinessBlockReason Reason);
public sealed record BusinessBlockItem(int ItemUid, int? BlockUid, object? Value, NoValueReason? NoValueReason)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? BlockName { get; init; }
}
public sealed record BusinessBlockDecision(long Sequence, int ParentId, string Transition,
    BusinessBlockReason? Reason, IReadOnlyList<int> ShouldRun, IReadOnlyList<BusinessBlockItem> Items);
/// <summary>A rule can only read its declared items, captured together before evaluating it.</summary>
public sealed class BusinessBlockDecisionInput
{
    private readonly StateAuthority authority;
    private readonly IReadOnlyDictionary<StateItemKey, IStatePublication?> items;
    public IReadOnlyDictionary<int, BusinessBlockState> Children { get; }
    public IReadOnlyCollection<StateItemKey> ItemKeys => items.Keys.ToArray();
    internal BusinessBlockDecisionInput(StateAuthority authority, IReadOnlyDictionary<StateItemKey, IStatePublication?> items,
        IReadOnlyDictionary<int, BusinessBlockState> children)
    { this.authority = authority; this.items = items; Children = children; }
    public StateRead<T> Read<T>(StateItemKey key)
    {
        if (!items.TryGetValue(key, out var publication)) throw new InvalidOperationException($"Rule read undeclared state item {key}.");
        authority.ValidateType<T>(key);
        return publication is StatePublication<T> value ? value.Read : StateRead<T>.NoValue(NoValueReason.NotPublished);
    }
}
public sealed record BusinessBlockControlTransition(long Sequence, DateTimeOffset At, int BlockId,
    BusinessBlockState From, BusinessBlockState To);
public sealed record BusinessBlockControlSnapshot(IReadOnlyDictionary<int, BusinessBlockState> States,
    IReadOnlyList<BusinessBlockControlTransition> History);
public sealed record BusinessBlockRunContext(CancellationToken StopNotification, Action ReportRunning);

/// <summary>Transitional managed rule for the window process and fixtures; Claude's control session deletes it at the end of 5d. The backend's rules are compiled in Zig.</summary>
public sealed record BusinessBlockDecisionRegistration(int ParentId, IReadOnlyList<StateItemKey> ItemKeys,
    Func<BusinessBlockDecisionInput, IReadOnlyCollection<int>> Decide);

