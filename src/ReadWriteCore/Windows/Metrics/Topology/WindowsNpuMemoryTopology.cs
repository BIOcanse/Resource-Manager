using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite.Native;
using ResourceManager.ReadWrite.Windows.Devices;
using ResourceManager.ReadWrite.Windows.Devices.Models;
using ResourceManager.ReadWrite.Windows.Devices.Npu;

namespace ResourceManager.ReadWrite.Windows.Metrics.Topology;

public sealed record NpuMemoryTopologyRead(NativeMetricSnapshotCatalogTopologyStatus Status,
    IReadOnlyList<NpuMemoryCatalogIdentity> Identities);

public static class WindowsNpuMemoryTopology
{
    // A directory must satisfy every applicable template's original ExpandBounded condition.
    // Its effective maximum is the conjunction of those bounds, not a second configuration value.
    public static uint EffectiveMaximumInstanceCount(IEnumerable<NativeMetricSnapshotRuleTemplate> templates)
        => templates.Where(static template => template.SourceId == NativeMetricSnapshotSources.WindowsNpuMemory
                && template.ScopeExpansion == 16)
            .Select(static template => template.MaximumInstanceCount).DefaultIfEmpty(0U).Min();

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static NpuMemoryTopologyRead Read(uint effectiveMaximumInstanceCount)
        => effectiveMaximumInstanceCount == 0 ? new(NativeMetricSnapshotCatalogTopologyStatus.Complete, [])
            : Project(new WindowsNpuInformationReader().Read(WindowsDeviceTopologyNativeReader.ReadSnapshot()), effectiveMaximumInstanceCount);

    public static NpuMemoryTopologyRead Project(IReadOnlyList<DeviceTopologyNpuInformation>? rows, uint effectiveMaximumInstanceCount)
    {
        if (effectiveMaximumInstanceCount == 0)
            return new(NativeMetricSnapshotCatalogTopologyStatus.Complete, []);
        if (rows is null || rows.Count > effectiveMaximumInstanceCount)
            return new(NativeMetricSnapshotCatalogTopologyStatus.Unavailable, []);
        var identities = rows.Select(static row => new NpuMemoryCatalogIdentity(row.PnpInstanceId ?? string.Empty)).ToArray();
        if (identities.Any(static identity => string.IsNullOrWhiteSpace(identity.PnpInstanceId)
                || identity.ExactIdentityKey.Contains('\0')
                || System.Text.Encoding.UTF8.GetByteCount(identity.ExactIdentityKey) > NativeMetricSnapshotCatalogHandleMap.MaximumExactKeyByteCount)
            || identities.Select(static identity => identity.ExactIdentityKey).Distinct(StringComparer.Ordinal).Count() != rows.Count
            || rows.Where((row, index) => row.DeviceIdentity != identities[index].ExactIdentityKey).Any())
            return new(NativeMetricSnapshotCatalogTopologyStatus.Unavailable, []);
        return new(NativeMetricSnapshotCatalogTopologyStatus.Complete, identities);
    }
}
