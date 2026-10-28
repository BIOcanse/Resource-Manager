using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.StateItems;
using ResourceManager.App.Application.RuntimeSpecialization.BusinessBlocks;
using ResourceManager.Shared.BusinessBlocks;
using ResourceManager.Shared.BusinessBlocks.State;
using SharedScheduler = ResourceManager.Shared.BusinessBlocks.BusinessBlockScheduler;

namespace Resource_Manager_APP.Tests;

/// <summary>Managed-only fixture registration; production backend registrations contain no graph data.</summary>
internal sealed record TestBlockRegistration(
    int Id, IReadOnlyList<int> Dependencies, Func<IServiceProvider, IBusinessBlock> Create)
{
    public int ParentId { get; init; } = BlockUids.Program;
}

/// <summary>Explicitly selects the transitional managed scheduler for isolated generic lifecycle tests.</summary>
internal sealed class TestBlockScheduler : SharedScheduler
{
    internal TestBlockScheduler(IEnumerable<TestBlockRegistration> declarations, StateAuthority authority,
        int historyLimit = 128, ILogger? logger = null, BusinessBlockController? control = null,
        IEnumerable<BusinessBlockDecisionRegistration>? decisions = null, int program = BlockUids.Program,
        int shutdownItemUid = StateItemUids.ProgramShuttingDown,
        string? registryVersion = null, Func<int, string?>? nameLookup = null, Func<long>? currentPlanVersion = null)
        : this(Prepare(declarations, program), authority, historyLimit, logger, control, decisions,
            program, shutdownItemUid, registryVersion, nameLookup, currentPlanVersion)
    {
    }

    private TestBlockScheduler(Prepared prepared, StateAuthority authority, int historyLimit, ILogger? logger,
        BusinessBlockController? control, IEnumerable<BusinessBlockDecisionRegistration>? decisions,
        int program, int shutdownItemUid, string? registryVersion,
        Func<int, string?>? nameLookup, Func<long>? currentPlanVersion)
        : base(prepared.Graph, prepared.Create, historyLimit,
            logger ?? NullLogger<SharedScheduler>.Instance,
            registryVersion ?? StateRegistry.Version, nameLookup ?? (static _ => null), authority,
            shutdownItemUid, control, FixtureDecisions(prepared.Graph, decisions), currentPlanVersion)
    {
        if (prepared.Graph.Program != program) throw new ArgumentException("Fixture graph program does not match.", nameof(program));
    }

    private readonly Dictionary<int, StateWriter<bool>> executionWriters = new();

    internal Task PublishExecutionAsync(int id, bool enabled, BusinessBlockReason reason, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        StateWriter<bool> writer;
        lock (executionWriters)
        {
            if (!Graph.BlockIds.Contains(id)) throw new KeyNotFoundException($"Unknown fixture block {id}.");
            if (!executionWriters.TryGetValue(id, out writer!))
                executionWriters.Add(id, writer = Authority.ClaimWrite<bool>(BusinessBlockTestSupport.ExecutionKey(id)));
        }
        return writer.PublishAsync(enabled, reason).WaitAsync(cancellation);
    }

    // Isolated fixtures declare their own execution/configuration inputs explicitly.
    // Production controllers never infer rules from another component's requested blocks.
    private static IEnumerable<BusinessBlockDecisionRegistration> FixtureDecisions(BusinessBlockGraph graph,
        IEnumerable<BusinessBlockDecisionRegistration>? overrides)
    {
        var rules = (overrides ?? []).ToDictionary(rule => rule.ParentId);
        foreach (var parent in graph.BlockIds.Prepend(graph.Program))
        {
            var children = graph.Children(parent);
            if (children.Count == 0 || rules.ContainsKey(parent)) continue;
            rules.Add(parent, new(parent, children.SelectMany(id => new[]
                { BusinessBlockTestSupport.ExecutionKey(id), BusinessBlockTestSupport.GateKey(id) }).ToArray(),
                input => children.Where(id => input.Read<bool>(BusinessBlockTestSupport.ExecutionKey(id)).GetValueOrDefault(false)
                    && input.Read<bool>(BusinessBlockTestSupport.GateKey(id)).GetValueOrDefault(true)).ToArray()));
        }
        return rules.Values;
    }

    internal static BusinessBlockGraph CreateGraph(IEnumerable<TestBlockRegistration> declarations,
        int program = BlockUids.Program)
        => new(program, declarations.Select(value => new BusinessBlockNode(value.Id, value.ParentId, value.Dependencies)));

    private static Prepared Prepare(IEnumerable<TestBlockRegistration> declarations, int program)
    {
        var rows = declarations.ToArray();
        var graph = CreateGraph(rows, program);
        var factories = rows.ToDictionary(value => value.Id);
        var services = new BusinessBlockTestSupport.EmptyServices();
        return new(graph, id => factories[id].Create(services));
    }

    private sealed record Prepared(BusinessBlockGraph Graph, Func<int, IBusinessBlock> Create);
}
