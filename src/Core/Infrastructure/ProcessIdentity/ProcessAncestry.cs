namespace ResourceManager.App.Infrastructure.ProcessIdentity;

internal readonly record struct ProcessAncestryIdentity(int ProcessId, int ParentProcessId, long StartKey);

internal static class ProcessAncestry
{
    // The parent PID is a reference to a live sampled instance only if it predates its child.
    // Unknown identities and broken/cyclic chains are not descendants.
    internal static IReadOnlySet<int> FindFamily(
        IReadOnlyDictionary<int, ProcessAncestryIdentity> processes, IReadOnlySet<int> roots)
    {
        var family = roots.Where(id => processes.TryGetValue(id, out var item) && item.StartKey > 0).ToHashSet();
        var unrelated = new HashSet<int>();
        var path = new List<int>();
        var visited = new HashSet<int>();
        foreach (var processId in processes.Keys)
        {
            path.Clear(); visited.Clear();
            var current = processId;
            var belongs = false;
            while (path.Count <= processes.Count)
            {
                if (family.Contains(current)) { belongs = true; break; }
                if (unrelated.Contains(current) || !visited.Add(current)
                    || !processes.TryGetValue(current, out var child) || child.StartKey <= 0) break;
                path.Add(current);
                if (!processes.TryGetValue(child.ParentProcessId, out var parent)
                    || parent.StartKey <= 0 || parent.StartKey > child.StartKey) break;
                current = parent.ProcessId;
            }
            foreach (var id in path)
            {
                if (belongs) family.Add(id); else unrelated.Add(id);
            }
        }
        return family;
    }
}
