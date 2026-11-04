using ResourceManager.App.Application.Components;
using ResourceManager.App.Domain.Dependencies;
using ResourceManager.App.Domain.Software;

namespace ResourceManager.App.Infrastructure.Dependencies;

// Dedicated binding/projection adapter. Native cache state alone decides lookup and commit admission.
internal sealed class NativeDependencyCache(NativeDependencyCacheWorkspace workspace)
{
    internal IReadOnlyList<OptionalDependencyStatus> GetStatuses(string? windowsSid,
        IReadOnlyList<InstalledSoftwareEntry> installedSoftware, Func<IReadOnlyList<OptionalDependencyStatus>> probe)
    {
        ArgumentNullException.ThrowIfNull(installedSoftware);
        ArgumentNullException.ThrowIfNull(probe);
        return Get(1, windowsSid ?? "", workspace.SourceId(installedSoftware), installedSoftware, probe);
    }

    internal ComponentProviderRuntimeProbeResult GetProvider(string componentId,
        Func<ComponentProviderRuntimeProbeResult> probe)
    {
        ArgumentNullException.ThrowIfNull(componentId);
        ArgumentNullException.ThrowIfNull(probe);
        return Get(2, componentId, 0, null, probe);
    }

    private T Get<T>(uint kind, string key, ulong source, object? sourceRoot, Func<T> probe) where T : class
    {
        var found = workspace.Lookup(kind, key, source);
        if (found.Token.Projection != 0) return (T)found.Value!;
        // Arbitrary host probes never run while the cache/workspace gate or a Ledger lease is held.
        var value = probe();
        workspace.Commit(kind, key, source, found.Token, sourceRoot, value);
        return value;
    }
}
