using ResourceManager.Shared.BusinessBlocks.State;
using Microsoft.Extensions.Options;
using ResourceManager.App.Application.RuntimeSpecialization;
using ResourceManager.App.Application.RuntimeSpecialization.BusinessBlocks;

namespace ResourceManager.App.Infrastructure.RuntimeSpecialization.BusinessBlocks;

/// <summary>Backend registration and configuration adapter for the shared scheduler.</summary>
public sealed class BusinessBlockScheduler : ResourceManager.Shared.BusinessBlocks.BusinessBlockScheduler
{
    public new BackendBlockGraph Graph => (BackendBlockGraph)base.Graph;

    public BusinessBlockScheduler(IServiceProvider services, IEnumerable<BusinessBlockRegistration> registrations,
        IOptions<BusinessBlockOptions> options, ILogger<BusinessBlockScheduler> logger, StateAuthority authority,
        BusinessBlockController? control = null, IEnumerable<BusinessBlockDecisionRegistration>? decisions = null)
        : this(services, new BackendBlockGraph(registrations), options, logger, authority, control, decisions) { }

    private BusinessBlockScheduler(IServiceProvider services, BackendBlockGraph graph,
        IOptions<BusinessBlockOptions> options, ILogger<BusinessBlockScheduler> logger, StateAuthority authority,
        BusinessBlockController? control, IEnumerable<BusinessBlockDecisionRegistration>? decisions)
        : base(graph, id => graph.Get(id).Create(services), options.Value.HistoryLimit, logger, ResourceManager.App.StateItems.StateRegistry.Version, LookupName, authority, ResourceManager.App.StateItems.StateItemUids.BlockDemand,
            ResourceManager.App.StateItems.StateItemUids.ProgramShuttingDown, control, decisions,
            () => services.GetService<IRuntimePlanProvider>()?.Current.Version ?? 0) { }

    private static string? LookupName(int uid)
    {
#if RM_STATE_NAMES
        return ResourceManager.App.StateItems.StateNames.Name(uid);
#else
        return null;
#endif
    }
}
