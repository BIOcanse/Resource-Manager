using ResourceManager.App.Application.MemoryProtection;

namespace ResourceManager.App.Infrastructure.MemoryProtection;

internal sealed class HostMemoryProtectionTrees
{
    internal readonly record struct Key(int ProcessId, string? StartKey);
    internal sealed record Choice(HostMemoryTreeRoot Root, decimal GrowthBytes, IReadOnlyList<HostMemoryProcess> LeavesFirst);
    private readonly Dictionary<Key, List<HostMemoryProcess>> children = new();
    private readonly Dictionary<Key, HostMemoryProcess> roots;
    private readonly Dictionary<Key, decimal?> currentTotals;
    private readonly Dictionary<Key, decimal?> pointTotals;

    internal HostMemoryProtectionTrees(IReadOnlyList<HostMemoryProcess> current, IReadOnlyList<HostMemoryProcess> point)
    {
        var pointById = point.ToDictionary(item => item.ProcessId);
        roots = point.Concat(current).GroupBy(Identity).ToDictionary(group => group.Key, group => group.Last());
        pointTotals = Totals(point, pointById, new());
        currentTotals = Totals(current, pointById, children);
    }

    internal IEnumerable<Choice> Ranked()
    {
        foreach (var pair in currentTotals.Where(pair => pair.Value is not null
            && (!pointTotals.TryGetValue(pair.Key, out var before) || before is not null))
            .Select(pair => (pair.Key, Growth: pair.Value!.Value - pointTotals.GetValueOrDefault(pair.Key, 0)!.Value))
            .Where(pair => pair.Growth > 0).OrderByDescending(pair => pair.Growth).ThenBy(pair => pair.Key.ProcessId))
        {
            var members = new List<HostMemoryProcess>();
            if (roots.TryGetValue(pair.Key, out var root) && IsCurrent(root)) members.Add(root);
            var pending = new Queue<Key>();
            var seen = new HashSet<Key>();
            pending.Enqueue(pair.Key);
            while (pending.Count > 0 && seen.Count <= roots.Count + children.Count)
            {
                var key = pending.Dequeue();
                if (!seen.Add(key) || !children.TryGetValue(key, out var direct)) continue;
                foreach (var child in direct) { members.Add(child); pending.Enqueue(Identity(child)); }
            }
            yield return new(new(pair.Key.ProcessId, pair.Key.StartKey, root?.Name), pair.Growth,
                members.DistinctBy(Identity).OrderByDescending(Start).ThenBy(item => item.ProcessId).ToArray());
        }
    }

    private readonly HashSet<Key> live = [];
    private bool IsCurrent(HostMemoryProcess process) => live.Contains(Identity(process));
    private Dictionary<Key, decimal?> Totals(IReadOnlyList<HostMemoryProcess> processes,
        IReadOnlyDictionary<int, HostMemoryProcess> point, Dictionary<Key, List<HostMemoryProcess>> links)
    {
        var byId = processes.ToDictionary(item => item.ProcessId);
        var totals = new Dictionary<Key, decimal?>();
        foreach (var child in processes)
        {
            if (ReferenceEquals(links, children)) live.Add(Identity(child));
            if (child.ParentProcessId is not > 0 || child.ParentProcessId == child.ProcessId) continue;
            var pid = child.ParentProcessId.Value;
            // A live or recorded parent counts only when it started before the child. A reused PID means the
            // original parent exited, so the child still belongs to that exited parent's node.
            Key key;
            if (byId.TryGetValue(pid, out var parent) && Start(parent) > 0 && Start(parent) < Start(child)) key = Identity(parent);
            else if (point.TryGetValue(pid, out var old) && Start(old) > 0 && Start(old) < Start(child)) key = Identity(old);
            else key = new(pid, null); // Exited parent: the native row still identifies its original PID.
            if (!links.TryGetValue(key, out var list)) links[key] = list = [];
            list.Add(child);
            var previous = totals.GetValueOrDefault(key, 0);
            totals[key] = previous is { } sum && child.PrivateCommitBytes is { } bytes ? sum + bytes : null;
        }
        return totals;
    }

    private static Key Identity(HostMemoryProcess process) => new(process.ProcessId, process.ProcessStartKey);
    private static ulong Start(HostMemoryProcess process) => ulong.TryParse(process.ProcessStartKey, out var value) ? value : 0;
}
