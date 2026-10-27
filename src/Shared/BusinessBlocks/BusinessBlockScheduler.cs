using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using ResourceManager.Shared.BusinessBlocks.State;

namespace ResourceManager.Shared.BusinessBlocks;

/// <summary>Static decision-node composition. Policies read only explicitly declared state items.</summary>
public class BusinessBlockScheduler : IBusinessBlockQuery, IAsyncDisposable
{
    private readonly Func<int, IBusinessBlock> create;
    private readonly Func<long> currentPlanVersion;
    private readonly BusinessBlockController control;
    private readonly ConcurrentDictionary<string, BusinessBlockDemand> demands = new(StringComparer.Ordinal);
    private readonly object demandSync = new();
    private readonly Dictionary<int, StateCell<bool>> demandCells = new();
    private readonly Dictionary<int, BusinessBlockDecisionNode> nodes = new();
    private readonly List<IDisposable> subscriptions = new();
    private readonly StateWriter<bool> shutdown;
    private readonly int? demandItemUid;
    private readonly int shutdownItemUid;
    private readonly int historyLimit;
    private readonly string registryVersion;
    private readonly Func<int, string?> nameLookup;
    private long nextDependency;
    private int disposed;
    public BusinessBlockGraph Graph { get; }
    public StateAuthority Authority { get; }
    public long CurrentPlanVersion => currentPlanVersion();
    public StateItemKey DemandKey(int blockUid) => demandItemUid is { } uid ? new(uid, blockUid)
        : throw new NotSupportedException("This process has no demand item declaration.");

    public BusinessBlockScheduler(BusinessBlockGraph graph, Func<int, IBusinessBlock> create,
        int historyLimit, ILogger logger, string registryVersion, Func<int, string?> nameLookup,
        StateAuthority authority, int? demandItemUid, int shutdownItemUid, BusinessBlockController? control = null,
        IEnumerable<BusinessBlockDecisionRegistration>? decisions = null, Func<long>? currentPlanVersion = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(registryVersion);
        ArgumentNullException.ThrowIfNull(nameLookup);
        this.registryVersion = registryVersion;
        this.nameLookup = nameLookup;
        this.create = create;
        this.currentPlanVersion = currentPlanVersion ?? (() => 0);
        this.historyLimit = historyLimit;
        this.demandItemUid = demandItemUid;
        this.shutdownItemUid = shutdownItemUid;
        ArgumentOutOfRangeException.ThrowIfNegative(historyLimit);
        this.control = control ?? new(Microsoft.Extensions.Logging.Abstractions.NullLogger<BusinessBlockController>.Instance, historyLimit, nameLookup);
        Graph = graph;
        Authority = authority;
        var initial = new BusinessBlockReason(BusinessBlockRequestKind.Configuration, graph.Program.ToString(System.Globalization.CultureInfo.InvariantCulture), "initial", CurrentPlanVersion);
        shutdown = authority.ClaimWrite<bool>(shutdownItemUid);
        _ = shutdown.PublishAsync(false, initial);
        if (demandItemUid is not null)
            foreach (var block in graph.Nodes)
            {
                var cell = authority.ClaimCell<bool>(DemandKey(block.Id));
                demandCells.Add(block.Id, cell);
                _ = cell.PublishAsync(false, initial);
            }
        var rules = (decisions ?? []).ToDictionary(value => value.ParentId);
        foreach (var parent in rules.Keys) _ = Graph.Children(parent);
        foreach (var parent in Graph.BlockIds.Prepend(Graph.Program))
        {
            var children = Graph.Children(parent);
            if (children.Count == 0 && parent != Graph.Program) continue;
            var rule = rules.GetValueOrDefault(parent);
            if (rule is null)
            {
                // UI compositions provide their own rules and have no backend demand catalog.
                if (children.Count > 0 && demandItemUid is null)
                    throw new ArgumentException($"Parent {parent} requires an explicit rule in a process without demand items.");
                rule = new(parent, children.Select(DemandKey).ToArray(),
                    input => children.Where(id => input.Read<bool>(DemandKey(id)).GetValueOrDefault(false)).ToArray());
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

    public Task SetDemandAsync(string key, int? blockId, BusinessBlockReason reason, CancellationToken cancellation = default)
    {
        ThrowIfDisposed();
        if (demandItemUid is null) throw new NotSupportedException("This process has no demand item declaration.");
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        Validate(reason);
        if (blockId is not null) _ = Graph.Get(blockId.Value);
        lock (demandSync)
        {
            BusinessBlockDemand? previous;
            if (blockId is null) demands.TryRemove(key, out previous);
            else
            {
                demands.TryGetValue(key, out previous);
                demands[key] = new(key, blockId.Value, reason);
            }
            return Task.WhenAll(new[] { previous?.BlockId, blockId }.OfType<int>().Distinct()
                .Select(id => demandCells[id].PublishAsync(demands.Values.Any(demand => demand.BlockId == id), reason)));
        }
    }

    public IBusinessBlockDependency AcquireDependency(int id, BusinessBlockReason reason)
    {
        var key = "dependency:" + Interlocked.Increment(ref nextDependency);
        return new BusinessBlockDemandLease(this, key, reason) { Ready = SetDemandAsync(key, id, reason) };
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
            Graph = Graph.Nodes, Demands = demands.Values.ToArray(), Items = Authority.ReadAll(nameLookup),
            Decisions = nodes.Values.SelectMany(node => node.ReadDecisions()).ToArray(),
            CurrentDecisions = nodes.Values.Select(node => node.ReadCurrentDecision()).OfType<BusinessBlockDecision>().ToArray(),
            ShuttingDown = Authority.Read<bool>(shutdownItemUid).GetValueOrDefault(false)
        };
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0)
        {
            control.StateChanged -= OnStateChanged;
            foreach (var subscription in subscriptions) subscription.Dispose();
            foreach (var node in nodes.Values) node.Dispose();
            // The service provider can already be disposed; this teardown event has no active plan or readers.
            var reason = new BusinessBlockReason(BusinessBlockRequestKind.Dependency,
                Graph.Program.ToString(System.Globalization.CultureInfo.InvariantCulture), "demand-owner-stopped", 0);
            await Task.WhenAll(demandCells.Values.Select(cell => cell.UnbindAsync(reason))).ConfigureAwait(false);
        }
    }
    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
    private static void Validate(BusinessBlockReason reason)
    {
        ArgumentNullException.ThrowIfNull(reason);
        if (!Enum.IsDefined(reason.Kind) || string.IsNullOrWhiteSpace(reason.Owner) || string.IsNullOrWhiteSpace(reason.ConfigurationOrDependency))
            throw new ArgumentException("An update needs its kind, owner and cause.", nameof(reason));
    }
}
