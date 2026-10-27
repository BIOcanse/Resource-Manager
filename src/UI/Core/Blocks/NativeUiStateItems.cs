using ResourceManager.NativeUi.LegacyStateItems;
using ResourceManager.Shared.BusinessBlocks.State;

namespace ResourceManager.NativeUi.Blocks;

/// <summary>The UI operation paths hold their own writes and the backend flow's publication cell.</summary>
internal sealed class NativeUiStateItems
{
    internal StateAuthority Authority { get; } = new(StateRegistry.Items, StateRegistry.Blocks);
    internal StateWriter<bool> ProgramStarted { get; }
    internal StateWriter<bool> WindowWanted { get; }
    internal StateCell<bool> BackendSessionReady { get; }

    internal NativeUiStateItems()
    {
        ProgramStarted = Authority.ClaimWrite<bool>(StateItemUids.ProgramStarted);
        WindowWanted = Authority.ClaimWrite<bool>(StateItemUids.WindowWanted);
        BackendSessionReady = Authority.ClaimCell<bool>(StateItemUids.BackendSessionReady);
    }
}
