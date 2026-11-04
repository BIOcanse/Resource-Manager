namespace ResourceManager.Shared.BusinessBlocks.State;

/// <summary>The sole capability to bind a current-value reader and notify its consumers of changes.</summary>
public sealed class StateReadFunction<T>
{
    private readonly StateAuthority authority;
    private readonly StateAuthority.Entry entry;
    internal StateReadFunction(StateAuthority authority, StateAuthority.Entry entry)
    { this.authority = authority; this.entry = entry; }

    public Task NotifyChangedAsync(BusinessBlockReason reason)
    {
        if (authority.Runtime is { } native && typeof(T) == typeof(bool))
            return native.NotifyChanged(entry.NativeClaim, reason);
        if (authority.Runtime is { } host)
        {
            var absence = Volatile.Read(ref entry.ReadFunctionBound) != 0
                ? ((StatePublication<T>)entry.ReadFunction!()).Read.NoValueReason : NoValueReason.OwnerStopped;
            return host.PublishPresence(entry.NativeClaim, absence, reason);
        }
        return authority.Notify(entry, reason);
    }

    public Task BindAsync(BusinessBlockReason reason)
    {
        if (authority.Runtime is { } native && typeof(T) == typeof(bool))
            return native.Bind(entry.NativeClaim, reason);
        Volatile.Write(ref entry.ReadFunctionBound, 1);
        return authority.Runtime is { } host ? host.Bind(entry.NativeClaim, reason) : authority.Notify(entry, reason);
    }

    public Task UnbindAsync(BusinessBlockReason reason)
    {
        if (authority.Runtime is { } native && typeof(T) == typeof(bool))
            return native.Unbind(entry.NativeClaim, reason);
        Volatile.Write(ref entry.Current, new StatePublication<T>(StateRead<T>.NoValue(NoValueReason.OwnerStopped)));
        Volatile.Write(ref entry.ReadFunctionBound, 0);
        return authority.Runtime is { } host ? host.Unbind(entry.NativeClaim, reason) : authority.Notify(entry, reason);
    }
}
