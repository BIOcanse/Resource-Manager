using ResourceManager.App.Application.Optimization;

namespace ResourceManager.App.Infrastructure.Optimization;

// Data survives the coordinator's run. There are no timers, callbacks or block lifetimes here.
public sealed class RuntimeCpuLeaseStore
{
    private readonly object gate = new();
    private readonly Dictionary<string, Entry> leases = new(StringComparer.Ordinal);
    internal sealed record Entry(RuntimeCpuLeaseSnapshot Snapshot, TaskCompletionSource Ended);

    internal bool HasLeases { get { lock (gate) return leases.Count != 0; } }
    internal Entry[] Capture() { lock (gate) return leases.Values.ToArray(); }
    internal Entry? Get(string id) { lock (gate) return leases.GetValueOrDefault(id); }
    internal RuntimeCpuLeaseSnapshot Add(RuntimeCpuLeaseRequest request, string holder, DateTimeOffset now, int capacity)
    {
        lock (gate)
        {
            if (leases.Count >= capacity) throw new InvalidOperationException("cpu-lease-capacity-exceeded");
            var value = new RuntimeCpuLeaseSnapshot(Guid.NewGuid().ToString("N"), request, holder,
                now, "pending", "scheduler-cycle-pending", null, []);
            leases.Add(value.LeaseId, new(value, new(TaskCreationOptions.RunContinuationsAsynchronously)));
            return value;
        }
    }
    internal bool Remove(string id)
    {
        Entry? entry;
        lock (gate) { if (!leases.Remove(id, out entry)) return false; }
        entry.Ended.TrySetResult();
        return true;
    }
    internal void Publish(string id, DateTimeOffset at, IReadOnlyList<RuntimeCpuLeaseEffect> effects, string? reason)
    {
        lock (gate)
        {
            if (!leases.TryGetValue(id, out var entry)) return;
            leases[id] = entry with { Snapshot = entry.Snapshot with
            {
                State = reason is not null ? "unavailable" : effects.Any(effect => effect.State == "failed") ? "failed"
                    : effects.Count == 0 ? "pending" : effects.All(effect => effect.State == "observed") ? "observed" : "pending",
                ReasonCode = reason ?? (effects.Count == 0 ? "target-not-running" : null),
                ObservedAt = at, Effects = effects.ToArray()
            } };
        }
    }
}
