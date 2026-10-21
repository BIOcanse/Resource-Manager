using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite.Native;
using ResourceManager.ReadWrite.Vendors.Intel;
using ResourceManager.ReadWrite.Windows.Gpu;

namespace ResourceManager.ReadWrite.Windows.Metrics.Topology;

public static class WindowsIntelFanTopology
{
    public static uint EffectiveMaximumInstanceCount(IEnumerable<NativeMetricSnapshotRuleTemplate> templates)
        => templates.Where(static template => template.SourceId == NativeMetricSnapshotSources.IntelGcl
                && template.ScopeExpansion == 6)
            .Select(static template => template.MaximumInstanceCount).DefaultIfEmpty(0U).Min();

    public static IntelFanTopologyRead OpenDirectory(Func<WindowsGpuAdapterInventoryRead, uint, IntelGclFanDirectoryLease?> open,
        WindowsGpuAdapterInventoryRead inventory, uint effectiveMaximumInstanceCount, IntelGclFanDirectoryLease? prior = null)
    {
        if (effectiveMaximumInstanceCount == 0) return new(NativeMetricSnapshotCatalogTopologyStatus.Complete, []);
        if (prior is not null && prior.IsCurrent(inventory, effectiveMaximumInstanceCount, CancellationToken.None))
            return new(NativeMetricSnapshotCatalogTopologyStatus.Complete, prior.Identities) { InitialReference = prior.AcquireLease() };
        var directory = open(inventory, effectiveMaximumInstanceCount);
        return directory is null ? new(NativeMetricSnapshotCatalogTopologyStatus.Unavailable, [])
            : new(NativeMetricSnapshotCatalogTopologyStatus.Complete, directory.Identities) { InitialReference = directory };
    }

    public static IntelGclFanDirectoryLease? DirectoryOf(IEnumerable<NativeMetricSnapshotOrdinalCatalogIdentity> fans)
    {
        var directories = fans.Where(static fan => fan.IgclFan?.Directory is not null)
            .Select(static fan => fan.IgclFan!.Directory!).Distinct().ToArray();
        if (directories.Length == 0) return null;
        if (directories.Length != 1) throw new InvalidOperationException("An Intel fan catalog must belong to one retained session directory.");
        return directories[0];
    }

    public static IDisposable? RetainDirectory(IEnumerable<NativeMetricSnapshotOrdinalCatalogIdentity> fans)
        => DirectoryOf(fans)?.AcquireLease();

    internal static NativeMetricSnapshotCatalogTopologySnapshot AddDirectory(
        NativeMetricSnapshotCatalogTopologySnapshot topology, WindowsGpuAdapterInventoryRead inventory,
        uint effectiveMaximumInstanceCount, Func<WindowsGpuAdapterInventoryRead, uint, IntelGclFanDirectoryLease?, IntelFanTopologyRead> open,
        IntelGclFanDirectoryLease? prior)
    {
        if (effectiveMaximumInstanceCount == 0)
            return topology with { IgclFanStatus = NativeMetricSnapshotCatalogTopologyStatus.Complete };
        IDisposable? directory = null;
        try
        {
            var read = open(inventory, effectiveMaximumInstanceCount, prior);
            directory = read.InitialReference;
            if (read.Status != NativeMetricSnapshotCatalogTopologyStatus.Complete)
                return topology with { IgclFanStatus = NativeMetricSnapshotCatalogTopologyStatus.Unavailable };
            var identities = read.Identities;
            if (identities.Count > effectiveMaximumInstanceCount
                || identities.Any(static fan => fan.DirectoryId == Guid.Empty)
                || identities.Select(static fan => fan.ExactIdentityKey).Distinct(StringComparer.Ordinal).Count() != identities.Count)
                throw new InvalidOperationException("The Intel fan directory is incomplete or exceeds the compiled template budget.");
            var fans = topology.SystemFans.ToList();
            var index = fans.Count == 0 ? 0 : checked(fans.Max(static fan => fan.DisplayIndex) + 1);
            foreach (var fan in identities.OrderBy(static fan => fan.ExactIdentityKey, StringComparer.Ordinal))
                fans.Add(new(index++, fan.ExactIdentityKey, topology.Generation) { IgclFan = fan });
            var result = topology with { SystemFans = fans, IgclFanStatus = NativeMetricSnapshotCatalogTopologyStatus.Complete,
                IgclFanDirectoryReference = directory };
            directory = null;
            return result;
        }
        catch (Exception)
        {
            return topology with { IgclFanStatus = NativeMetricSnapshotCatalogTopologyStatus.Unavailable };
        }
        finally { directory?.Dispose(); }
    }
}

/// <summary>Passive topology transport. A real reader transfers its initial directory reference to the capturing operation.</summary>
public sealed record IntelFanTopologyRead(NativeMetricSnapshotCatalogTopologyStatus Status, IReadOnlyList<IntelGclFanIdentity> Identities)
{
    public IDisposable? InitialReference { get; init; }
}
