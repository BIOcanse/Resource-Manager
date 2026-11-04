using ResourceManager.App.Application.Components;
using ResourceManager.App.Domain.Dependencies;
using ResourceManager.App.Domain.Software;
using ResourceManager.App.Infrastructure.Adaptation;

namespace ResourceManager.App.Infrastructure.Dependencies;

/// <summary>
/// Holds optional dependency probe results in the native Ledger workspace. A changed inventory
/// object or an invalidation causes another probe. An invalidated in-flight probe is returned
/// to its caller and is not retained.
/// </summary>
public sealed class OptionalDependencyProbeResults : IDisposable
{
    private readonly NativeDependencyCache cache;
    internal NativeDependencyCacheWorkspace Workspace { get; }

    public OptionalDependencyProbeResults() : this(new NativeDependencyCacheWorkspace()) { }
    public OptionalDependencyProbeResults(ResourceManagerLedgerOwner owner)
        : this(new NativeDependencyCacheWorkspace(owner)) { }
    internal OptionalDependencyProbeResults(NativeDependencyCacheWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        Workspace = workspace;
        cache = new NativeDependencyCache(workspace);
    }

    /// <summary>Native diagnostic counters, projected for existing consumers and tests.</summary>
    internal int StatusProbeCount => Workspace.StatusProbeCount;
    internal int ProviderProbeCount => Workspace.ProviderProbeCount;
    public void Invalidate() => Workspace.Invalidate();

    internal IReadOnlyList<OptionalDependencyStatus> GetStatuses(string? windowsSid,
        IReadOnlyList<InstalledSoftwareEntry> installedSoftware, Func<IReadOnlyList<OptionalDependencyStatus>> probe)
        => cache.GetStatuses(windowsSid, installedSoftware, probe);

    internal ComponentProviderRuntimeProbeResult GetProvider(string componentId,
        Func<ComponentProviderRuntimeProbeResult> probe) => cache.GetProvider(componentId, probe);

    public void Dispose() => Workspace.Dispose();
}
