using Microsoft.Extensions.Logging;
using ResourceManager.Shared.BusinessBlocks.State;

namespace ResourceManager.Shared.BusinessBlocks;

/// <summary>Transitional managed controller for the window process and isolated fixtures. Claude control session removes this implementation and its seam at the end of 5d.</summary>
internal sealed class ManagedBusinessBlockRuntime : IBusinessBlockRuntime
{
    private readonly Func<int, IBusinessBlock> create;
    private readonly Func<long> currentPlanVersion;
    private readonly BusinessBlockController control;
    private readonly Dictionary<int, BusinessBlockDecisionNode> nodes = new();
    private readonly List<IDisposable> subscriptions = new();
    private readonly StateWriter<bool> shutdown;
    private readonly int shutdownItemUid;
    private readonly int historyLimit;
    private readonly string registryVersion;
    private readonly Func<int, string?> nameLookup;
    private int disposed;
    public BusinessBlockGraph Graph { get; }
    public StateAuthority Authority { get; }
    public long CurrentPlanVersion => currentPlanVersion();
    public ManagedBusinessBlockRuntime(BusinessBlockGraph graph, Func<int, IBusinessBlock> create,
        int historyLimit, ILogger logger, string registryVersion, Func<int, string?> nameLookup,
        StateAuthority authority, int shutdownItemUid, BusinessBlockController? control = null,
        IEnumerable<BusinessBlockDecisionRegistration>? decisions = null, Func<long>? currentPlanVersion = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(registryVersion);
        ArgumentNullException.ThrowIfNull(nameLookup);
        this.registryVersion = registryVersion;
        this.nameLookup = nameLookup;
        this.create = create;
        this.currentPlanVersion = currentPlanVersion ?? (() => 0);
        this.historyLimit = historyLimit;
        this.shutdownItemUid = shutdownItemUid;
        ArgumentOutOfRangeException.ThrowIfNegative(historyLimit);
        this.control = control ?? new(Microsoft.Extensions.Logging.Abstractions.NullLogger<BusinessBlockController>.Instance, historyLimit, nameLookup);
        Graph = graph;
        Authority = authority;
        var initial = new BusinessBlockReason(BusinessBlockRequestKind.Configuration, graph.Program.ToString(System.Globalization.CultureInfo.InvariantCulture), "initial", CurrentPlanVersion);
        shutdown = authority.ClaimWrite<bool>(shutdownItemUid);
        _ = shutdown.PublishAsync(false, initial);
        var rules = (decisions ?? []).ToDictionary(value => value.ParentId);
        foreach (var parent in rules.Keys) _ = Graph.Children(parent);
        foreach (var parent in Graph.BlockIds.Prepend(Graph.Program))
        {
            var children = Graph.Children(parent);
            if (children.Count == 0 && parent != Graph.Program) continue;
            var rule = rules.GetValueOrDefault(parent);
            if (rule is null)
            {
                if (children.Count > 0)
                    throw new ArgumentException($"Parent {parent} requires an explicit decision rule.");
                rule = new(parent, [], static _ => []);
            }
            var keys = rule.ItemKeys.Concat(parent == Graph.Program ? new StateItemKey[] { shutdownItemUid } : []).Distinct().ToArray();
            foreach (var key in keys) authority.ValidateKey(key);
            var node = new BusinessBlockDecisionNode(parent, children, keys, rule.Decide, authority, this.control,
                id => this.create(id), id => nodes.GetValueOrDefault(id),
                why => { if (parent != Graph.Program) _ = nodes[Graph.Get(parent).ParentId].Wake("branch-drained:" + parent, why); },
                logger, historyLimit, Graph.Program, shutdownItemUid, nameLookup);
            nodes.Add(parent, node);
        }
        foreach (var node in nodes.Values)
            foreach (var key in node.ItemKeys)
                subscriptions.Add(authority.Subscribe(key, why => node.Wake("item:" + key, why)));
        this.control.StateChanged += OnStateChanged;
    }

    public Task ShutdownAsync(BusinessBlockReason reason)
    {
        ThrowIfDisposed(); Validate(reason);
        return shutdown.PublishAsync(true, reason);
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
            Graph = Graph.Nodes, Items = Authority.ReadAll(nameLookup),
            Decisions = nodes.Values.SelectMany(node => node.ReadDecisions()).ToArray(),
            CurrentDecisions = nodes.Values.Select(node => node.ReadCurrentDecision()).OfType<BusinessBlockDecision>().ToArray(),
            ShuttingDown = Authority.Read<bool>(shutdownItemUid).GetValueOrDefault(false)
        };
    }
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0)
        {
            control.StateChanged -= OnStateChanged;
            foreach (var subscription in subscriptions) subscription.Dispose();
            foreach (var node in nodes.Values) node.Dispose();

        }
        return ValueTask.CompletedTask;
    }
    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
    private static void Validate(BusinessBlockReason reason)
    {
        ArgumentNullException.ThrowIfNull(reason);
        if (!Enum.IsDefined(reason.Kind) || string.IsNullOrWhiteSpace(reason.Owner) || string.IsNullOrWhiteSpace(reason.ConfigurationOrDependency))
            throw new ArgumentException("An update needs its kind, owner and cause.", nameof(reason));
    }
}
