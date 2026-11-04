using ResourceManager.App.StateItems;
using ResourceManager.Shared.BusinessBlocks.State;

namespace ResourceManager.App.Infrastructure.Control.Writers;

/// <summary>Publishes the bridge subscription source's own usage, independently of process runs.</summary>
internal sealed class HardwareBridgeReadingSubscriptions
{
    private readonly StateCell<bool>? cell;
    public HardwareBridgeReadingSubscriptions(HardwareBridgeProcessOwner owner, StateAuthority? authority)
    {
        cell = authority?.ClaimCell<bool>(StateItemUids.HardwareBridgeReadingsInUse);
        owner.ReadingsInUseChanged += Publish;
        Publish(owner.ReadingsInUse);
    }
    private void Publish(bool value) => _ = cell?.PublishAsync(value,
        new(BusinessBlockRequestKind.Configuration, nameof(HardwareBridgeReadingSubscriptions), "bridge-readings-in-use", 0));
}
