namespace ResourceManager.Shared.BusinessBlocks.State;

internal interface IStatePublication
{
    BusinessBlockItem Snapshot(StateItemKey key);
}

internal sealed record StatePublication<T>(StateRead<T> Read) : IStatePublication
{
    public BusinessBlockItem Snapshot(StateItemKey key) => new(key.ItemUid, key.BlockUid,
        Read.HasValue ? Read.Value : null, Read.NoValueReason);
}

/// <summary>The sole capability to publish an owned directory item.</summary>
public sealed class StateWriter<T>
{
    private readonly StateAuthority authority;
    private readonly StateAuthority.Entry entry;
    internal StateWriter(StateAuthority authority, StateAuthority.Entry entry) { this.authority = authority; this.entry = entry; }
    public Task PublishAsync(T value, BusinessBlockReason reason) => Publish(StateRead<T>.FromValue(value), reason);
    public Task PublishNoValueAsync(NoValueReason reason, BusinessBlockReason cause) => Publish(StateRead<T>.NoValue(reason), cause);
    private Task Publish(StateRead<T> read, BusinessBlockReason reason)
    {
        Volatile.Write(ref entry.Current, new StatePublication<T>(read));
        return authority.Notify(entry, reason);
    }
}

/// <summary>An owner's reference cell. Its current publication is replaced atomically, never sampled.</summary>
public sealed class StateCell<T>
{
    private readonly object sync = new();
    private readonly StateAuthority authority;
    private readonly StateAuthority.Entry entry;
    private StatePublication<T> current = new(StateRead<T>.NoValue(NoValueReason.NotPublished));
    private bool bound = true;
    internal StateCell(StateAuthority authority, StateAuthority.Entry entry) { this.authority = authority; this.entry = entry; }
    public Task PublishAsync(T value, BusinessBlockReason reason) => Publish(StateRead<T>.FromValue(value), reason);
    public Task PublishNoValueAsync(NoValueReason reason, BusinessBlockReason cause) => Publish(StateRead<T>.NoValue(reason), cause);
    private Task Publish(StateRead<T> read, BusinessBlockReason reason)
    {
        lock (sync)
        {
            current = new(read);
            if (!bound) return Task.CompletedTask;
            Volatile.Write(ref entry.Current, current);
        }
        return authority.Notify(entry, reason);
    }
    public Task BindAsync(BusinessBlockReason reason)
    {
        lock (sync) { bound = true; Volatile.Write(ref entry.Current, current); }
        return authority.Notify(entry, reason);
    }
    public Task UnbindAsync(BusinessBlockReason reason)
    {
        lock (sync)
        {
            bound = false;
            Volatile.Write(ref entry.Current, new StatePublication<T>(StateRead<T>.NoValue(NoValueReason.OwnerStopped)));
        }
        return authority.Notify(entry, reason);
    }
}
