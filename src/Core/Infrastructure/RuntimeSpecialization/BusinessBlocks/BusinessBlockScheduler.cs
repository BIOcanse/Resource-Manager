using ResourceManager.Shared.BusinessBlocks.State;
using ResourceManager.App.Application.RuntimeSpecialization;

namespace ResourceManager.App.Infrastructure.RuntimeSpecialization.BusinessBlocks;

/// <summary>The backend selects native explicitly; the compiled Zig tree owns decisions.</summary>
public sealed class BusinessBlockScheduler : ResourceManager.Shared.BusinessBlocks.BusinessBlockScheduler
{
    public new BackendBlockGraph Graph { get; }
    public BusinessBlockScheduler(IServiceProvider services, IEnumerable<BusinessBlockRegistration> registrations,
        NativeControl native, StateAuthority authority)
        : this(services, new BackendBlockGraph(registrations, native.Graph), native, authority) { }
    private BusinessBlockScheduler(IServiceProvider services, BackendBlockGraph graph, NativeControl native, StateAuthority authority)
        : base(native, InitializeAvailability(services, authority), id => graph.Get(id).Create(services),
            () => services.GetService<IRuntimePlanProvider>()?.Current.Version ?? 0)
        => Graph = graph;

    private static StateAuthority InitializeAvailability(IServiceProvider services, StateAuthority authority)
    {
        // The independent publisher initializes the three cells before native readers are activated.
        _ = services.GetRequiredService<ResourceManager.App.Infrastructure.Telemetry.Etw.NativeEtwSessionAvailability>();
        return authority;
    }
}
