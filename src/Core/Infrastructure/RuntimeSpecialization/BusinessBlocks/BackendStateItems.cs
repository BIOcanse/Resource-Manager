using ResourceManager.App.StateItems;
using ResourceManager.Shared.BusinessBlocks.State;

namespace ResourceManager.App.Infrastructure.RuntimeSpecialization.BusinessBlocks;

/// <summary>Creates this process's state directory from its generated registry declarations.</summary>
public sealed class BackendStateItems
{
    public StateAuthority Authority { get; } = new(StateRegistry.Items, StateRegistry.Blocks);
}
