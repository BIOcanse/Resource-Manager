namespace Resource_Manager_APP.Tests;

public sealed partial class StateRegistryTests
{
    [Fact]
    public void CompleteResidentSessionHasOneHostOwnerAndSeparateBooleanProjection()
    {
        var declarations = ResourceManager.NativeUi.DesktopStateItems.StateRegistry.Items;
        var session = Assert.Single(declarations, item => item.Uid == ResourceManager.NativeUi.DesktopStateItems.StateItemUids.BackendSession);
        var ready = Assert.Single(declarations, item => item.Uid == ResourceManager.NativeUi.DesktopStateItems.StateItemUids.BackendSessionReady);
        Assert.Equal(typeof(ResourceManager.NativeUi.LegacyStateItems.DesktopBackendSession), session.ValueType);
        Assert.Equal(typeof(bool), ready.ValueType);
        Assert.Equal(ResourceManager.NativeUi.DesktopStateItems.BlockUids.BackendConnection, session.Owner.BlockUid);
        Assert.Equal(session.Owner.BlockUid, ready.Owner.BlockUid);
        Assert.Equal(ResourceManager.Shared.BusinessBlocks.State.StateItemStorage.Reference, session.Storage);
        Assert.False(session.PerBlock);
        Assert.Equal(ResourceManager.Shared.BusinessBlocks.State.StateItemTrigger.Event, session.Trigger);
    }
}
