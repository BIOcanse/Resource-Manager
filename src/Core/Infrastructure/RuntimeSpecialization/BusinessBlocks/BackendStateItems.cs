using ResourceManager.App.StateItems;
using ResourceManager.Shared.BusinessBlocks.State;

namespace ResourceManager.App.Infrastructure.RuntimeSpecialization.BusinessBlocks;

/// <summary>Creates this process's state directory from its generated registry declarations.</summary>
public sealed class BackendStateItems
{
    public StateAuthority Authority { get; }
    // Transitional fixture construction. Production DI explicitly supplies its native controller.
    public BackendStateItems() => Authority = new(StateRegistry.Items, StateRegistry.Blocks);
    internal BackendStateItems(NativeControl control)
        => Authority = control.CreateAuthority(StateRegistry.Items, StateRegistry.Blocks);
}
