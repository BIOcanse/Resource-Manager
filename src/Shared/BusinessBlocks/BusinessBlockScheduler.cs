using Microsoft.Extensions.Logging;
using ResourceManager.Shared.BusinessBlocks.State;

namespace ResourceManager.Shared.BusinessBlocks;

/// <summary>
/// Transitional process selection seam: the backend selects native explicitly;
/// the window process keeps managed control until 5d. Claude's control session
/// deletes this seam and the managed implementation at the end of 5d.
/// </summary>
internal interface IBusinessBlockRuntime : IBusinessBlockQuery, IAsyncDisposable
{
    BusinessBlockGraph Graph { get; }
    StateAuthority Authority { get; }
    long CurrentPlanVersion { get; }
    StateItemKey DemandKey(int blockUid);
    Task SetDemandAsync(string key, int? blockId, BusinessBlockReason reason, CancellationToken cancellation = default);
    IBusinessBlockDependency AcquireDependency(int id, BusinessBlockReason reason);
    Task ShutdownAsync(BusinessBlockReason reason);
    BusinessBlockState ReadState(int id);
}

/// <summary>Host-facing shell. Each process explicitly selects one controller implementation.</summary>
public class BusinessBlockScheduler : IBusinessBlockQuery, IAsyncDisposable
{
    private readonly IBusinessBlockRuntime runtime;
    public BusinessBlockGraph Graph => runtime.Graph;
    public StateAuthority Authority => runtime.Authority;
    public long CurrentPlanVersion => runtime.CurrentPlanVersion;
    public StateItemKey DemandKey(int blockUid) => runtime.DemandKey(blockUid);

    /// <summary>Transitional managed selection for the window process and isolated managed fixtures, removed in 5d.</summary>
    public BusinessBlockScheduler(BusinessBlockGraph graph, Func<int, IBusinessBlock> create,
        int historyLimit, ILogger logger, string registryVersion, Func<int, string?> nameLookup,
        StateAuthority authority, int? demandItemUid, int shutdownItemUid, BusinessBlockController? control = null,
        IEnumerable<BusinessBlockDecisionRegistration>? decisions = null, Func<long>? currentPlanVersion = null)
        => runtime = new ManagedBusinessBlockRuntime(graph, create, historyLimit, logger, registryVersion,
            nameLookup, authority, demandItemUid, shutdownItemUid, control, decisions, currentPlanVersion);

    /// <summary>Explicit native selection. Load and create errors propagate; no managed fallback exists.</summary>
    public BusinessBlockScheduler(NativeControl native, StateAuthority authority,
        Func<int, IBusinessBlock> create, Func<long>? currentPlanVersion = null)
    {
        native.AttachAuthority(authority);
        runtime = native;
        native.Activate(create, currentPlanVersion);
    }

    public Task SetDemandAsync(string key, int? blockId, BusinessBlockReason reason, CancellationToken cancellation = default)
        => runtime.SetDemandAsync(key, blockId, reason, cancellation);
    public IBusinessBlockDependency AcquireDependency(int id, BusinessBlockReason reason) => runtime.AcquireDependency(id, reason);
    public Task ShutdownAsync(BusinessBlockReason reason) => runtime.ShutdownAsync(reason);
    public BusinessBlockState ReadState(int id) => runtime.ReadState(id);
    public BusinessBlocksSnapshot Read() => runtime.Read();
    // The native scheduler is a borrowed facade over the DI-owned controller. Its disposal requests
    // shutdown but leaves reads available while any host run completes; the NativeControl owner drains
    // and destroys the handle when its own lifetime ends.
    public ValueTask DisposeAsync() => runtime is NativeControl native
        ? new(native.RequestSchedulerShutdownAsync())
        : runtime.DisposeAsync();
}
