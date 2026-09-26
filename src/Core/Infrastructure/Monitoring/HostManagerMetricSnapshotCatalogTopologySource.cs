using System.Text;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Infrastructure.NativeCore;

namespace ResourceManager.App.Infrastructure.Monitoring;

internal enum NativeMetricSnapshotCatalogTopologyStatus
{
    Complete = 1,
    Unavailable = 2,
    Unsupported = 3
}

internal sealed record NativeMetricSnapshotCatalogTopologySnapshot(
    ulong Generation,
    NativeMetricSnapshotCatalogTopologyStatus GpuStatus,
    IReadOnlyList<NativeMetricSnapshotGpuCatalogIdentity> GpuAdapters,
    NativeMetricSnapshotCatalogTopologyStatus StorageStatus,
    IReadOnlyList<NativeMetricSnapshotOrdinalCatalogIdentity> StorageSensors,
    NativeMetricSnapshotCatalogTopologyStatus FanStatus,
    IReadOnlyList<NativeMetricSnapshotOrdinalCatalogIdentity> SystemFans);

public sealed class HostManagerMetricSnapshotCatalogTopologySource(
    WindowsGpuAdapterOrderMonitoringZone gpuAdapterOrderZone)
{
    internal NativeMetricSnapshotCatalogTopologySnapshot Capture()
    {
        var inventory = gpuAdapterOrderZone.ReadInventory(requested: true);
        if (inventory.Status != SamplingObservationStatus.Current
            || inventory.Generation == 0
            || inventory.ObservedAtUtcTicks <= 0
            || inventory.ObservedCount != inventory.Adapters.Count
            || inventory.SkippedCount != 0
            || inventory.OverflowCount != 0)
        {
            return Unavailable(inventory.Generation);
        }

        var rows = new List<NativeMetricSnapshotGpuCatalogIdentity>(
            inventory.Adapters.Count);
        foreach (var adapter in inventory.Adapters)
        {
            if (adapter.Index < 0
                || adapter.IsSoftware
                || adapter.PhysicalIdentityStatus
                    != SamplingObservationStatus.Current
                || adapter.PhysicalIdentities.Count == 0)
            {
                return Unavailable(inventory.Generation);
            }
            var luid = NativePdhAdapterIdentity.Pack(adapter.Luid);
            if (luid == 0)
            {
                return Unavailable(inventory.Generation);
            }

            var exactKey = CreateExactGpuKey(adapter);
            if (string.IsNullOrEmpty(exactKey))
            {
                return Unavailable(inventory.Generation);
            }
            rows.Add(new NativeMetricSnapshotGpuCatalogIdentity(
                adapter.Index,
                adapter.Name,
                exactKey,
                luid,
                inventory.Generation,
                VendorMask(adapter.VendorId),
                adapter.SupportsDedicatedMemoryMetrics));
        }

        if (rows.Select(static row => row.DisplayIndex)
                .Distinct()
                .Count() != rows.Count
            || rows.Select(static row => row.ExactIdentityKey)
                .Distinct(StringComparer.Ordinal)
                .Count() != rows.Count
            || rows.Select(static row => row.AdapterLuid)
                .Distinct()
                .Count() != rows.Count)
        {
            return Unavailable(inventory.Generation);
        }

        return new NativeMetricSnapshotCatalogTopologySnapshot(
            inventory.Generation,
            NativeMetricSnapshotCatalogTopologyStatus.Complete,
            rows,
            NativeMetricSnapshotCatalogTopologyStatus.Unavailable,
            [],
            NativeMetricSnapshotCatalogTopologyStatus.Unavailable,
            []);
    }

    private static string CreateExactGpuKey(WindowsGpuAdapter adapter)
    {
        var identities = adapter.PhysicalIdentities
            .OrderBy(
                static identity => identity.PnpInstanceId,
                StringComparer.OrdinalIgnoreCase)
            .ThenBy(static identity => identity.PhysicalAdapterIndex)
            .ToArray();
        if (identities.Any(identity =>
                identity.Status
                    is not (
                        WindowsGpuPhysicalIdentityStatus.Bound
                        or WindowsGpuPhysicalIdentityStatus.NonPci)
                || string.IsNullOrWhiteSpace(identity.PnpInstanceId)
                || identity.VendorId != adapter.VendorId
                || identity.DeviceId != adapter.DeviceId))
        {
            return string.Empty;
        }

        var builder = new StringBuilder("windows-gpu|");
        foreach (var identity in identities)
        {
            builder.Append(identity.PnpInstanceId.ToUpperInvariant());
            builder.Append('|');
            builder.Append(identity.VendorId.ToString("X8"));
            builder.Append(':');
            builder.Append(identity.DeviceId.ToString("X8"));
            builder.Append(':');
            builder.Append(identity.SubVendorId.ToString("X8"));
            builder.Append(':');
            builder.Append(identity.SubsystemId.ToString("X8"));
            builder.Append(':');
            builder.Append(identity.RevisionId.ToString("X8"));
            builder.Append(';');
        }
        var result = builder.ToString();
        return Encoding.UTF8.GetByteCount(result)
                <= NativeMetricSnapshotCatalogHandleMap
                    .MaximumExactKeyByteCount
            ? result
            : string.Empty;
    }

    private static uint VendorMask(uint vendorId)
        => vendorId switch
        {
            0x10DE => 1,
            0x1002 => 2,
            0x8086 => 4,
            _ => 8
        };

    private static NativeMetricSnapshotCatalogTopologySnapshot Unavailable(
        ulong generation)
        => new(
            generation,
            NativeMetricSnapshotCatalogTopologyStatus.Unavailable,
            [],
            NativeMetricSnapshotCatalogTopologyStatus.Unavailable,
            [],
            NativeMetricSnapshotCatalogTopologyStatus.Unavailable,
            []);
}
