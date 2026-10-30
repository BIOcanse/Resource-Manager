using ResourceManager.App.Application.Components;
using ResourceManager.App.Domain.Dependencies;
using ResourceManager.App.Domain.Software;

namespace ResourceManager.App.Infrastructure.Dependencies;

/// <summary>
/// The held result of the optional dependency file probes, shared by every dependency manager
/// and the provider runtime probe (the manager is created per request, this holder is not).
/// A probe runs on the first read; afterwards reads return the held result until an install,
/// download, removal, repair or verify completes, or a caller asks for a refresh
/// (<see cref="Invalidate"/>). The installed-software list is part of the key: a refreshed
/// inventory is a different list and re-probes the external install candidates.
/// A probe that started before an invalidation is returned to its caller but not held.
/// </summary>
public sealed class OptionalDependencyProbeResults
{
    private readonly object gate = new();
    private readonly Dictionary<string, HeldStatuses> statuses = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ComponentProviderRuntimeProbeResult> providers = new(StringComparer.OrdinalIgnoreCase);
    private long generation;

    /// <summary>Number of status probes and provider probes that ran; tests use it.</summary>
    internal int StatusProbeCount { get; private set; }

    internal int ProviderProbeCount { get; private set; }

    /// <summary>Drops every held result so the next read probes again.</summary>
    public void Invalidate()
    {
        lock (gate)
        {
            generation++;
            statuses.Clear();
            providers.Clear();
        }
    }

    internal IReadOnlyList<OptionalDependencyStatus> GetStatuses(
        string? windowsSid,
        IReadOnlyList<InstalledSoftwareEntry> installedSoftware,
        Func<IReadOnlyList<OptionalDependencyStatus>> probe)
    {
        var scope = windowsSid?.Trim() ?? "";
        long started;
        lock (gate)
        {
            if (statuses.TryGetValue(scope, out var held) && ReferenceEquals(held.InstalledSoftware, installedSoftware))
                return held.Statuses;
            started = generation;
            StatusProbeCount++;
        }

        var probed = probe();
        lock (gate)
        {
            if (generation == started) statuses[scope] = new(installedSoftware, probed);
        }
        return probed;
    }

    internal ComponentProviderRuntimeProbeResult GetProvider(string componentId, Func<ComponentProviderRuntimeProbeResult> probe)
    {
        long started;
        lock (gate)
        {
            if (providers.TryGetValue(componentId, out var held)) return held;
            started = generation;
            ProviderProbeCount++;
        }

        var probed = probe();
        lock (gate)
        {
            if (generation == started) providers[componentId] = probed;
        }
        return probed;
    }

    private sealed record HeldStatuses(
        IReadOnlyList<InstalledSoftwareEntry> InstalledSoftware,
        IReadOnlyList<OptionalDependencyStatus> Statuses);
}
