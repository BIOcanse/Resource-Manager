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
        if (authority.Runtime is { } native && typeof(T) == typeof(bool))
            return native.PublishBool(entry.NativeClaim, StateAuthority.AsBoolean(read), reason);

        var publication = new StatePublication<T>(read);
        if (authority.Runtime is not { } runtime)
        {
            Volatile.Write(ref entry.Current, publication);
            return authority.Notify(entry, reason);
        }

        lock (entry.Sync)
        {
            var previous = Interlocked.Exchange(ref entry.Current, publication);
            try { return runtime.PublishPresence(entry.NativeClaim, read.NoValueReason, reason); }
            catch
            {
                Interlocked.CompareExchange(ref entry.Current, previous, publication);
                throw;
            }
        }
    }
}

/// <summary>An owner's reference cell. Its current publication is replaced atomically, never sampled.</summary>
public sealed class StateCell<T>
{
    private readonly object sync = new();
    private readonly StateAuthority authority;
    private readonly StateAuthority.Entry entry;
    private StatePublication<T>? current;
    private bool bound;
    private long revision;
    internal StateCell(StateAuthority authority, StateAuthority.Entry entry)
    {
        this.authority = authority;
        this.entry = entry;
        if (authority.Runtime is null || typeof(T) != typeof(bool))
        {
            current = new(StateRead<T>.NoValue(NoValueReason.NotPublished));
            bound = true;
        }
    }
    public Task PublishAsync(T value, BusinessBlockReason reason) => Publish(StateRead<T>.FromValue(value), reason);
    public Task PublishNoValueAsync(NoValueReason reason, BusinessBlockReason cause) => Publish(StateRead<T>.NoValue(reason), cause);
    private Task Publish(StateRead<T> read, BusinessBlockReason reason)
    {
        var runtime = authority.Runtime;
        if (runtime is not null && typeof(T) == typeof(bool))
            return runtime.PublishBool(entry.NativeClaim, StateAuthority.AsBoolean(read), reason);

        var publication = new StatePublication<T>(read);
        if (runtime is not null)
        {
            lock (sync)
            {
                var previousCurrent = current;
                var previousBound = bound;
                var previousExposure = Volatile.Read(ref entry.Current);
                current = publication;
                if (bound) Volatile.Write(ref entry.Current, publication);
                var operationRevision = ++revision;
                try { return runtime.PublishPresence(entry.NativeClaim, read.NoValueReason, reason); }
                catch
                {
                    RollBack(operationRevision, previousCurrent, previousBound, previousExposure);
                    throw;
                }
            }
        }

        lock (sync)
        {
            current = publication;
            if (!bound) return Task.CompletedTask;
            Volatile.Write(ref entry.Current, publication);
        }
        return authority.Notify(entry, reason);
    }
    public Task BindAsync(BusinessBlockReason reason)
    {
        var runtime = authority.Runtime;
        if (runtime is not null && typeof(T) == typeof(bool))
            return runtime.Bind(entry.NativeClaim, reason);

        if (runtime is not null)
        {
            lock (sync)
            {
                var previousCurrent = current;
                var previousBound = bound;
                var previousExposure = Volatile.Read(ref entry.Current);
                bound = true;
                Volatile.Write(ref entry.Current, current!);
                var operationRevision = ++revision;
                try { return runtime.Bind(entry.NativeClaim, reason); }
                catch
                {
                    RollBack(operationRevision, previousCurrent, previousBound, previousExposure);
                    throw;
                }
            }
        }

        lock (sync)
        {
            bound = true;
            Volatile.Write(ref entry.Current, current!);
        }
        return authority.Notify(entry, reason);
    }
    public Task UnbindAsync(BusinessBlockReason reason)
    {
        var runtime = authority.Runtime;
        if (runtime is not null && typeof(T) == typeof(bool))
            return runtime.Unbind(entry.NativeClaim, reason);

        if (runtime is not null)
        {
            lock (sync)
            {
                var previousCurrent = current;
                var previousBound = bound;
                var previousExposure = Volatile.Read(ref entry.Current);
                bound = false;
                Volatile.Write(ref entry.Current, new StatePublication<T>(StateRead<T>.NoValue(NoValueReason.OwnerStopped)));
                var operationRevision = ++revision;
                try { return runtime.Unbind(entry.NativeClaim, reason); }
                catch
                {
                    RollBack(operationRevision, previousCurrent, previousBound, previousExposure);
                    throw;
                }
            }
        }

        lock (sync)
        {
            bound = false;
            Volatile.Write(ref entry.Current, new StatePublication<T>(StateRead<T>.NoValue(NoValueReason.OwnerStopped)));
        }
        return authority.Notify(entry, reason);
    }

    private void RollBack(long operationRevision, StatePublication<T>? previousCurrent, bool previousBound, IStatePublication? previousExposure)
    {
        lock (sync)
        {
            if (revision != operationRevision) return;
            current = previousCurrent;
            bound = previousBound;
            Volatile.Write(ref entry.Current, previousExposure);
            revision++;
        }
    }
}
