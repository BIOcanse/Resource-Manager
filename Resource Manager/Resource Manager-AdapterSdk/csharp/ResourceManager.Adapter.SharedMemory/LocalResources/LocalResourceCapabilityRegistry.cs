namespace ResourceManager.Adapter.LocalResources;

public sealed class LocalResourceCapabilityRegistry
{
    private readonly object _sync = new();
    private readonly NativeLocalResourceManagerSession _session;
    private readonly Dictionary<CapabilityKey, LocalResourceCapabilityHandler> _handlers = [];
    private readonly Dictionary<TableKey, HashSet<CapabilityKey>> _tableCapabilities = [];
    private readonly Dictionary<CapabilityKey, TableKey> _capabilityTables = [];

    internal LocalResourceCapabilityRegistry(NativeLocalResourceManagerSession session)
        => _session = session;

    public int RegisteredCount
    {
        get
        {
            lock (_sync) return _handlers.Count;
        }
    }

    public LocalResourceCapabilityHandle Register(
        LocalResourceTableHandle table,
        LocalResourceCapabilityDefinition definition,
        LocalResourceCapabilityHandler handler)
    {
        LocalResourceIntentExecutor.ThrowIfCapabilityHandlerLifecycleEntry();
        using var operation = _session.BeginOperation(LocalResourceOperationKind.Maintenance);
        return RegisterScoped(operation, table, definition, handler);
    }

    internal LocalResourceCapabilityHandle RegisterScoped(
        LocalResourceOperation operation,
        LocalResourceTableHandle table,
        LocalResourceCapabilityDefinition definition,
        LocalResourceCapabilityHandler handler)
    {
        _session.ValidateStandaloneOperation(operation, LocalResourceOperationKind.Maintenance);
        return RegisterCore(null, table, definition, handler);
    }

    internal LocalResourceCapabilityHandle RegisterOwned(
        LocalResourceManagerSessionOwner owner,
        LocalResourceTableHandle table,
        LocalResourceCapabilityDefinition definition,
        LocalResourceCapabilityHandler handler)
        => RegisterCore(owner, table, definition, handler);

    private LocalResourceCapabilityHandle RegisterCore(
        LocalResourceManagerSessionOwner? owner,
        LocalResourceTableHandle table,
        LocalResourceCapabilityDefinition definition,
        LocalResourceCapabilityHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_sync)
        {
            var handle = _session.RegisterCapabilityCore(owner, table, definition);
            var capabilityKey = CapabilityKey.From(handle);
            var tableKey = TableKey.From(table);
            try
            {
                _handlers.Add(capabilityKey, handler);
                _capabilityTables.Add(capabilityKey, tableKey);
                if (!_tableCapabilities.TryGetValue(tableKey, out var capabilities))
                {
                    capabilities = [];
                    _tableCapabilities.Add(tableKey, capabilities);
                }
                capabilities.Add(capabilityKey);
            }
            catch
            {
                _handlers.Remove(capabilityKey);
                _capabilityTables.Remove(capabilityKey);
                if (_tableCapabilities.TryGetValue(tableKey, out var capabilities))
                {
                    capabilities.Remove(capabilityKey);
                    if (capabilities.Count == 0) _tableCapabilities.Remove(tableKey);
                }
                _session.UnregisterCapabilityCore(owner, handle);
                throw;
            }
            return handle;
        }
    }

    public LocalResourceCapabilityHandle Replace(
        LocalResourceCapabilityHandle handle,
        LocalResourceCapabilityDefinition definition,
        LocalResourceCapabilityHandler handler)
    {
        LocalResourceIntentExecutor.ThrowIfCapabilityHandlerLifecycleEntry();
        using var operation = _session.BeginOperation(LocalResourceOperationKind.Maintenance);
        return ReplaceScoped(operation, handle, definition, handler);
    }

    internal LocalResourceCapabilityHandle ReplaceScoped(
        LocalResourceOperation operation,
        LocalResourceCapabilityHandle handle,
        LocalResourceCapabilityDefinition definition,
        LocalResourceCapabilityHandler handler)
    {
        _session.ValidateStandaloneOperation(operation, LocalResourceOperationKind.Maintenance);
        return ReplaceCore(null, handle, definition, handler);
    }

    internal LocalResourceCapabilityHandle ReplaceOwned(
        LocalResourceManagerSessionOwner owner,
        LocalResourceCapabilityHandle handle,
        LocalResourceCapabilityDefinition definition,
        LocalResourceCapabilityHandler handler)
        => ReplaceCore(owner, handle, definition, handler);

    private LocalResourceCapabilityHandle ReplaceCore(
        LocalResourceManagerSessionOwner? owner,
        LocalResourceCapabilityHandle handle,
        LocalResourceCapabilityDefinition definition,
        LocalResourceCapabilityHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_sync)
        {
            var oldKey = CapabilityKey.From(handle);
            if (!_capabilityTables.TryGetValue(oldKey, out var tableKey))
            {
                throw new InvalidOperationException(
                    "The replaced local resource capability has no managed table binding.");
            }
            var replacement = _session.ReplaceCapabilityCore(owner, handle, definition);
            var replacementKey = CapabilityKey.From(replacement);
            _capabilityTables.Remove(oldKey);
            _handlers.Remove(oldKey);
            _handlers.Add(replacementKey, handler);
            _capabilityTables.Add(replacementKey, tableKey);
            if (!_tableCapabilities.TryGetValue(tableKey, out var capabilities))
            {
                capabilities = [];
                _tableCapabilities.Add(tableKey, capabilities);
            }
            capabilities.Remove(oldKey);
            capabilities.Add(replacementKey);
            return replacement;
        }
    }

    public void Unregister(LocalResourceCapabilityHandle handle)
    {
        LocalResourceIntentExecutor.ThrowIfCapabilityHandlerLifecycleEntry();
        using var operation = _session.BeginOperation(LocalResourceOperationKind.Maintenance);
        UnregisterScoped(operation, handle);
    }

    internal void UnregisterScoped(
        LocalResourceOperation operation,
        LocalResourceCapabilityHandle handle)
    {
        _session.ValidateStandaloneOperation(operation, LocalResourceOperationKind.Maintenance);
        UnregisterCore(null, handle);
    }

    internal void UnregisterOwned(
        LocalResourceManagerSessionOwner owner,
        LocalResourceCapabilityHandle handle)
        => UnregisterCore(owner, handle);

    private void UnregisterCore(
        LocalResourceManagerSessionOwner? owner,
        LocalResourceCapabilityHandle handle)
    {
        lock (_sync)
        {
            _session.UnregisterCapabilityCore(owner, handle);
            var capabilityKey = CapabilityKey.From(handle);
            _handlers.Remove(capabilityKey);
            if (_capabilityTables.Remove(capabilityKey, out var tableKey) &&
                _tableCapabilities.TryGetValue(tableKey, out var capabilities))
            {
                capabilities.Remove(capabilityKey);
                if (capabilities.Count == 0) _tableCapabilities.Remove(tableKey);
            }
        }
    }

    internal bool TryResolve(
        ulong capabilityId,
        ulong generation,
        out LocalResourceCapabilityHandler handler)
    {
        lock (_sync)
        {
            return _handlers.TryGetValue(new(capabilityId, generation), out handler!);
        }
    }

    internal bool CanResolveAll(IReadOnlyList<LocalResourceIntent> intents)
    {
        ArgumentNullException.ThrowIfNull(intents);
        lock (_sync)
        {
            for (var index = 0; index < intents.Count; index++)
            {
                var intent = intents[index];
                if (!_handlers.ContainsKey(new(intent.CapabilityId, intent.CapabilityGeneration)))
                {
                    return false;
                }
            }
            return true;
        }
    }

    internal void RemoveTable(LocalResourceTableHandle table)
    {
        lock (_sync)
        {
            var tableKey = TableKey.From(table);
            if (!_tableCapabilities.Remove(tableKey, out var capabilities)) return;
            foreach (var capability in capabilities)
            {
                _handlers.Remove(capability);
                _capabilityTables.Remove(capability);
            }
        }
    }

    internal void Clear()
    {
        lock (_sync)
        {
            _handlers.Clear();
            _tableCapabilities.Clear();
            _capabilityTables.Clear();
        }
    }

    private readonly record struct CapabilityKey(ulong CapabilityId, ulong Generation)
    {
        internal static CapabilityKey From(LocalResourceCapabilityHandle handle)
            => new(handle.CapabilityId, handle.Generation);
    }

    private readonly record struct TableKey(
        ulong ManagerInstanceId,
        ulong TableSlotGeneration,
        uint TableSlotIndex)
    {
        internal static TableKey From(LocalResourceTableHandle handle)
            => new(
                handle.Native.ManagerInstanceId,
                handle.Native.SlotGeneration,
                handle.Native.SlotIndex);
    }
}
