using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Application.RuntimeSpecialization;
using ResourceManager.App.Domain.RuntimeSpecialization;

namespace ResourceManager.App.Infrastructure.Optimization;

// Data survives the coordinator's run. There are no timers, callbacks or block lifetimes here.
public sealed class RuntimeCpuLeaseStore(IRuntimePlanProvider runtimePlanProvider, TimeProvider timeProvider)
    : IRuntimeCpuLeaseRegistry
{
    private readonly object gate = new();
    private readonly Dictionary<string, Entry> leases = new(StringComparer.Ordinal);
    internal sealed record Entry(RuntimeCpuLeaseSnapshot Snapshot, TaskCompletionSource Ended);
    internal HostManagerVersionedWakeDeadline WakeDeadline { get; } = new(timeProvider);

    public RuntimeCpuLeaseSnapshot CreateCpuLease(RuntimeCpuLeaseRequest request, string holder)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Target);
        var target = request.Target;
        var kinds = (target.SoftwareId is null ? 0 : 1) + (target.ProcessId is null ? 0 : 1)
            + (target.ExecutablePath is null ? 0 : 1);
        if (kinds != 1 || target.ProcessId is <= 0 || target.ProcessId.HasValue != target.ProcessStartKey.HasValue
            || target.ProcessStartKey == 0 || target.SoftwareId is not null && string.IsNullOrWhiteSpace(target.SoftwareId)
            || target.ProcessStartKey > 2650467743999999999UL
            || !request.Lock && !request.Exclusive || string.IsNullOrWhiteSpace(holder))
            throw new InvalidOperationException("invalid-cpu-lease-target-or-operation");
        if (target.ExecutablePath is { } path)
        {
            if (!Path.IsPathFullyQualified(path) || !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("cpu-lease-executable-path-required");
            target = target with { ExecutablePath = Path.GetFullPath(path) };
        }
        var topology = runtimePlanProvider.Current.CpuPlacementTopology
            ?? throw new InvalidOperationException("cpu-topology-unavailable");
        var cores = request.PhysicalCoreIds?.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        if (cores is not { Length: > 0 } || cores.Any(id => !topology.PhysicalCores.Any(core => core.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
            || request.Exclusive && cores.Length >= topology.PhysicalCores.Count)
            throw new InvalidOperationException("invalid-cpu-lease-physical-core-set");
        var result = Add(request with { Target = target, PhysicalCoreIds = cores }, holder, timeProvider.GetUtcNow(),
            runtimePlanProvider.Current.HostManager.RequirePublished().HostRecreate.PlacementCoordinator.TargetCapacity);
        WakeDeadline.PublishForeground(TimeSpan.Zero);
        return result;
    }

    public bool DeleteCpuLease(string leaseId)
    {
        var removed = Remove(leaseId);
        if (removed) WakeDeadline.PublishForeground(TimeSpan.Zero);
        return removed;
    }

    public Task WaitForCpuLeaseEndAsync(string leaseId, CancellationToken cancellationToken)
        => (Get(leaseId)?.Ended.Task ?? Task.CompletedTask).WaitAsync(cancellationToken);

    internal bool HasLeases { get { lock (gate) return leases.Count != 0; } }
    internal Entry[] Capture() { lock (gate) return leases.Values.ToArray(); }
    internal Entry? Get(string id) { lock (gate) return leases.GetValueOrDefault(id); }
    private RuntimeCpuLeaseSnapshot Add(RuntimeCpuLeaseRequest request, string holder, DateTimeOffset now, int capacity)
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
    private bool Remove(string id)
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
