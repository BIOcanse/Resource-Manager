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
        int? demandItemUid = StateItemUids.BlockDemand, int shutdownItemUid = StateItemUids.ProgramShuttingDown,
        string? registryVersion = null, Func<int, string?>? nameLookup = null, Func<long>? currentPlanVersion = null)
        : this(Prepare(declarations, program), authority, historyLimit, logger, control, decisions,
            program, demandItemUid, shutdownItemUid, registryVersion, nameLookup, currentPlanVersion)
    {
    }

    private TestBlockScheduler(Prepared prepared, StateAuthority authority, int historyLimit, ILogger? logger,
        BusinessBlockController? control, IEnumerable<BusinessBlockDecisionRegistration>? decisions,
        int program, int? demandItemUid, int shutdownItemUid, string? registryVersion,
        Func<int, string?>? nameLookup, Func<long>? currentPlanVersion)
        : base(prepared.Graph, prepared.Create, historyLimit,
            logger ?? NullLogger<SharedScheduler>.Instance,
            registryVersion ?? StateRegistry.Version, nameLookup ?? (static _ => null), authority,
            demandItemUid, shutdownItemUid, control, decisions, currentPlanVersion)
    {
        if (prepared.Graph.Program != program) throw new ArgumentException("Fixture graph program does not match.", nameof(program));
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
