using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace ResourceManager.Shared.BusinessBlocks;

/// <summary>Static decision-node composition and fact publication. No execution policies or global alignment queue.</summary>
public class BusinessBlockScheduler : IBusinessBlockQuery, IAsyncDisposable
{
    public const string ShutdownFact = "program:shutting-down";
    public static string DemandFact(int id) => "demand:" + id;
    public static string EnabledFact(int id) => "enabled:" + id;
    public static string ChildrenStoppedFact(int id) => "children-stopped:" + id;
    private readonly Func<int, IBusinessBlock> create;
    private readonly Func<long> currentPlanVersion;
    private readonly BusinessBlockController control;
    private readonly BusinessBlockFactAuthority facts = new();
    private readonly ConcurrentDictionary<string, BusinessBlockDemand> demands = new(StringComparer.Ordinal);
    private readonly Dictionary<int, BusinessBlockDecisionNode> nodes = new();
    private readonly int historyLimit;
    private readonly string registryVersion;
    private readonly Func<int, string?> nameLookup;
    private long nextDependency;
    private int disposed;
    private int shuttingDown;
    public BusinessBlockGraph Graph { get; }
    public long CurrentPlanVersion => currentPlanVersion();

    public BusinessBlockScheduler(BusinessBlockGraph graph, Func<int, IBusinessBlock> create,
        int historyLimit, ILogger logger, string registryVersion, Func<int, string?> nameLookup, BusinessBlockController? control = null,
        IEnumerable<BusinessBlockDecisionRegistration>? decisions = null, Func<long>? currentPlanVersion = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(registryVersion);
        ArgumentNullException.ThrowIfNull(nameLookup);
        this.registryVersion = registryVersion;
        this.nameLookup = nameLookup;
        this.create = create;
        this.currentPlanVersion = currentPlanVersion ?? (() => 0);
        this.historyLimit = historyLimit;
        ArgumentOutOfRangeException.ThrowIfNegative(historyLimit);
        this.control = control ?? new(Microsoft.Extensions.Logging.Abstractions.NullLogger<BusinessBlockController>.Instance, historyLimit, nameLookup);
        Graph = graph;
        var rules = (decisions ?? []).ToDictionary(value => value.ParentId);
        foreach (var parent in rules.Keys) _ = Graph.Children(parent);
        facts.Declare(ShutdownFact);
        foreach (var block in Graph.Nodes)
        {
            facts.Declare(DemandFact(block.Id));
            facts.Declare(EnabledFact(block.Id), true);
            if (Graph.Children(block.Id).Count > 0) facts.Declare(ChildrenStoppedFact(block.Id), true);
        }
        foreach (var parent in Graph.BlockIds.Prepend(Graph.Program))
        {
            var children = Graph.Children(parent);
            if (children.Count == 0 && parent != Graph.Program) continue;
            var rule = rules.GetValueOrDefault(parent) ?? new(parent,
                children.Select(DemandFact).ToArray(), input => children.Where(id => input.Facts[DemandFact(id)].Value).ToArray());
            // Enablement is a fact read only by the block's own parent.
            var keys = rule.FactKeys.Concat(children.Select(EnabledFact))
                .Concat(children.Where(id => Graph.Children(id).Count > 0).Select(ChildrenStoppedFact))
                .Concat(parent == Graph.Program ? [ShutdownFact] : Array.Empty<string>()).Distinct(StringComparer.Ordinal).ToArray();
            foreach (var key in keys) facts.Declare(key);
            var node = new BusinessBlockDecisionNode(parent, children, keys, rule.Decide, facts, this.control,
                id => this.create(id), id => nodes.GetValueOrDefault(id), logger, historyLimit, Graph.Program, nameLookup);
            nodes.Add(parent, node);
        }
        foreach (var node in nodes.Values)
        {
            foreach (var key in node.FactKeys) facts.Subscribe(key, node);
        }
        this.control.StateChanged += OnStateChanged;
    }

    public Task SetDemandAsync(string key, int? blockId, BusinessBlockReason reason, CancellationToken cancellation = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        Validate(reason);
        if (blockId is not null) _ = Graph.Get(blockId.Value);
        BusinessBlockDemand? previous;
        if (blockId is null) demands.TryRemove(key, out previous);
        else
        {
            var replacement = new BusinessBlockDemand(key, blockId.Value, reason);
            while (true)
            {
                if (demands.TryGetValue(key, out previous))
                {
                    if (demands.TryUpdate(key, replacement, previous)) break;
                }
                else if (demands.TryAdd(key, replacement)) break;
            }
        }
        var affected = new[] { previous?.BlockId, blockId }.OfType<int>().Distinct();
        return Task.WhenAll(affected.Select(id => facts.PublishDemand(id, demands, reason)));
    }

    public Task SetEnabledAsync(int id, bool enabled, BusinessBlockReason reason)
    {
        ThrowIfDisposed(); _ = Graph.Get(id); Validate(reason);
        return facts.Publish(EnabledFact(id), enabled, reason);
    }

    /// <summary>Publish a declared branch condition; its declared readers alone are woken.</summary>
    public Task SetFactAsync(string key, bool value, BusinessBlockReason reason)
    {
        ThrowIfDisposed(); Validate(reason);
        if (key == ShutdownFact || key.StartsWith("demand:", StringComparison.Ordinal) || key.StartsWith("enabled:", StringComparison.Ordinal)
            || key.StartsWith("children-stopped:", StringComparison.Ordinal))
            throw new ArgumentException("Use the demand, enablement or shutdown operation for this fact.", nameof(key));
        return facts.Publish(key, value, reason);
    }

    public IBusinessBlockDependency AcquireDependency(int id, BusinessBlockReason reason)
    {
        var key = "dependency:" + Interlocked.Increment(ref nextDependency);
        return new BusinessBlockDemandLease(this, key, reason) { Ready = SetDemandAsync(key, id, reason) };
    }

    public Task ShutdownAsync(BusinessBlockReason reason)
    {
        ThrowIfDisposed(); Validate(reason);
        Interlocked.Exchange(ref shuttingDown, 1);
        return facts.Publish(ShutdownFact, true, reason);
    }

    private void OnStateChanged(int id, BusinessBlockState state)
    {
        if (Volatile.Read(ref disposed) != 0) return;
        var reason = new BusinessBlockReason(BusinessBlockRequestKind.Dependency, id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            state == BusinessBlockState.Stopped ? "run-returned" : "state:" + state, CurrentPlanVersion);
        if (nodes.TryGetValue(id, out var ownNode)) _ = ownNode.Wake("owner-" + reason.ConfigurationOrDependency, reason);
        _ = nodes[Graph.Get(id).ParentId].Wake(reason.ConfigurationOrDependency, reason);
    }

    public BusinessBlockState ReadState(int id) { _ = Graph.Get(id); return control.ReadState(id); }
    public BusinessBlocksSnapshot Read()
    {
        var actual = control.Read();
        return new(DateTimeOffset.UtcNow, historyLimit, Graph.Nodes.Select(node => new BusinessBlockFacts(
            node.Id, actual.States.GetValueOrDefault(node.Id), node.ParentId, node.Inputs) { Name = nameLookup(node.Id) }).ToArray(), actual.History)
        {
            RegistryVersion = registryVersion, Program = Graph.Program, ProgramName = nameLookup(Graph.Program),
            Graph = Graph.Nodes, Demands = demands.Values.ToArray(), Facts = facts.ReadAll(),
            Decisions = nodes.Values.SelectMany(node => node.ReadDecisions()).ToArray(),
            CurrentDecisions = nodes.Values.Select(node => node.ReadCurrentDecision()).OfType<BusinessBlockDecision>().ToArray(),
            ShuttingDown = Volatile.Read(ref shuttingDown) != 0
        };
    }
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0)
        {
            control.StateChanged -= OnStateChanged;
            foreach (var node in nodes.Values) node.Dispose();
        }
        return ValueTask.CompletedTask;
    }
    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
    private static void Validate(BusinessBlockReason reason)
    {
        ArgumentNullException.ThrowIfNull(reason);
        if (!Enum.IsDefined(reason.Kind) || string.IsNullOrWhiteSpace(reason.Owner) || string.IsNullOrWhiteSpace(reason.ConfigurationOrDependency))
            throw new ArgumentException("A fact needs its kind, owner and cause.", nameof(reason));
    }
}
