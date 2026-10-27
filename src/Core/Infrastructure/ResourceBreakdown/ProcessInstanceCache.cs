using System.Collections.Concurrent;

namespace ResourceManager.App.Infrastructure.ResourceBreakdown;

internal sealed class ProcessInstanceCache<TValue>
    where TValue : class
{
    private readonly ConcurrentDictionary<ProcessInstanceKey, TValue> entries = new();

    internal int Count => entries.Count;
    internal IEnumerable<TValue> Values => entries.Values;

    internal bool TryGet(ProcessInstanceKey key, out TValue? value)
    {
        return entries.TryGetValue(key, out value);
    }

    internal TValue? GetOrAdd(ProcessInstanceKey key, Func<TValue?> valueFactory)
    {
        if (entries.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var created = valueFactory();
        return created is null ? null : entries.GetOrAdd(key, created);
    }

    internal void Set(ProcessInstanceKey key, TValue value)
    {
        entries[key] = value;
    }

    internal void Prune(IReadOnlySet<ProcessInstanceKey> liveInstances)
    {
        foreach (var key in entries.Keys)
        {
            if (!liveInstances.Contains(key))
            {
                entries.TryRemove(key, out _);
            }
        }
    }
}
