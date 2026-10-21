using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite.Native;
using ResourceManager.ReadWrite.Windows.Devices.Npu;

namespace ResourceManager.ReadWrite.Windows.Metrics.Topology;

public sealed record NpuEngineTopologyRead(NativeMetricSnapshotCatalogTopologyStatus Status,
    IReadOnlyList<NpuEngineCatalogIdentity> Identities)
{
    public IWindowsNpuEngineDirectory? Directory { get; init; }
    public IDisposable? InitialReference { get; init; }
}

public static class WindowsNpuEngineTopology
{
    public static uint EffectiveMaximumInstanceCount(IEnumerable<NativeMetricSnapshotRuleTemplate> templates)
        => templates.Where(static template => template.SourceId == NativeMetricSnapshotSources.WindowsNpuEngine
                && template.ScopeExpansion == 17)
            .Select(static template => template.MaximumInstanceCount).DefaultIfEmpty(0U).Min();

    public static NpuEngineTopologyRead OpenDirectory(Func<uint, IWindowsNpuEngineDirectory?> open,
        uint maximum, IWindowsNpuEngineDirectory? prior = null)
    {
        if (maximum == 0) return new(NativeMetricSnapshotCatalogTopologyStatus.Complete, []);
        if (prior is not null && prior.IsCurrent(maximum))
            return new(NativeMetricSnapshotCatalogTopologyStatus.Complete, prior.Identities)
                { Directory = prior, InitialReference = prior.AcquireLease() };
        var directory = open(maximum);
        return directory is null ? new(NativeMetricSnapshotCatalogTopologyStatus.Unavailable, [])
            : new(NativeMetricSnapshotCatalogTopologyStatus.Complete, directory.Identities)
                { Directory = directory, InitialReference = directory };
    }

    internal static NativeMetricSnapshotCatalogTopologySnapshot AddDirectory(
        NativeMetricSnapshotCatalogTopologySnapshot topology, uint maximum,
        Func<uint, IWindowsNpuEngineDirectory?, NpuEngineTopologyRead> open, IWindowsNpuEngineDirectory? prior)
    {
        if (maximum == 0) return topology with { NpuEngineStatus = NativeMetricSnapshotCatalogTopologyStatus.Complete,
            NpuEngineIdentities = [], NpuEngineDirectory = null, NpuEngineDirectoryReference = null };
        IDisposable? initial = null;
        try
        {
            var read = open(maximum, prior);
            initial = read.InitialReference;
            if (read.Status != NativeMetricSnapshotCatalogTopologyStatus.Complete || read.Directory is null || initial is null
                || read.Identities.Count > maximum || !read.Directory.IsCurrent(maximum)
                || !read.Directory.Identities.SequenceEqual(read.Identities)
                || read.Identities.Any(identity => identity.DirectoryId == Guid.Empty
                    || !ReferenceEquals(identity.Directory, read.Directory)
                    || identity.ExactIdentityKey.Contains('\0')
                    || System.Text.Encoding.UTF8.GetByteCount(identity.ExactIdentityKey) > NativeMetricSnapshotCatalogHandleMap.MaximumExactKeyByteCount)
                || read.Identities.Select(static identity => identity.ExactIdentityKey).Distinct(StringComparer.Ordinal).Count() != read.Identities.Count)
                return topology with { NpuEngineStatus = NativeMetricSnapshotCatalogTopologyStatus.Unavailable };
            var result = topology with { NpuEngineStatus = NativeMetricSnapshotCatalogTopologyStatus.Complete,
                NpuEngineIdentities = read.Identities, NpuEngineDirectory = read.Directory, NpuEngineDirectoryReference = initial };
            initial = null;
            return result;
        }
        catch (Exception) { return topology with { NpuEngineStatus = NativeMetricSnapshotCatalogTopologyStatus.Unavailable }; }
        finally { initial?.Dispose(); }
    }
}
