using ResourceManager.App.StateItems;
using ResourceManager.Shared.BusinessBlocks.State;

namespace Resource_Manager_APP.Tests;

/// <summary>Explicit authoritative publications for inert production-tree fixtures.</summary>
internal sealed class BackendPreciseRuleTestFacts
{
    private static readonly int[] NativeFacts = [BackendItems.PresentNativeSessionAvailable,
        BackendItems.GpuAllocationNativeSessionAvailable, BackendItems.VidMmNativeSessionAvailable,
        BackendItems.PresentUnfrozen, BackendItems.VidMmUnfrozen];
    private readonly Dictionary<StateItemKey, StateCell<bool>> cells = [];
    internal BackendPreciseRuleTestFacts(StateAuthority authority, NativeControl native,
        bool hostPermits = true, bool nativeFacts = false)
    {
        foreach (var declaration in StateRegistry.Items.Where(item => item.ValueType == typeof(bool)
            && item.Storage == StateItemStorage.Reference && !item.PerBlock
            && (nativeFacts || !NativeFacts.Contains(item.Uid))))
            cells.Add(new(declaration.Uid), authority.ClaimCell<bool>(declaration.Uid));
        if (hostPermits)
            foreach (var block in native.Graph.BlockIds)
                cells.Add(new(BackendItems.HostStartPermitted, block),
                    authority.ClaimCell<bool>(new(BackendItems.HostStartPermitted, block)));
    }
    internal IEnumerable<StateItemKey> Keys => cells.Keys;
    internal StateCell<bool> this[StateItemKey key] => cells[key];
    internal async Task InitializeAsync(bool facts = false)
    {
        foreach (var (key, cell) in cells)
            await cell.PublishAsync(key.ItemUid is BackendItems.HostStartPermitted or BackendItems.FanCoreAvailable || facts, BusinessBlockTestSupport.Reason());
    }
    internal Task PublishAsync(int uid, bool value)
        => cells[new(uid)].PublishAsync(value, BusinessBlockTestSupport.Reason());
}
