using ResourceManager.ReadWrite.Vendors.Amd.Smu;
using System.Text;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite.Native;
using ResourceManager.ReadWrite.Windows.Cpu;
using ResourceManager.ReadWrite.Windows.Cpu.Models;
using ResourceManager.ReadWrite.Windows.Gpu;
using ResourceManager.ReadWrite.Vendors.Intel.Msr;
using ResourceManager.ReadWrite.Windows.Metrics.Resources;
using ResourceManager.ReadWrite.Vendors.Nvidia;
namespace ResourceManager.ReadWrite.Windows.Metrics.Topology;

public sealed partial class WindowsHardwareMetricTopologySource(WindowsMetricTopologyResources resources,
    Func<CpuOsTopologyInformation?> readCpuTopology)
{
    private NativeMetricSnapshotCatalogTopologySnapshot? published;
    public NativeMetricSnapshotCatalogTopologySnapshot Capture() => Capture(0, 0);

    public NativeMetricSnapshotCatalogTopologySnapshot Capture(uint effectiveNpuMemoryMaximumInstanceCount)
        => Capture(effectiveNpuMemoryMaximumInstanceCount, 0);

    public NativeMetricSnapshotCatalogTopologySnapshot Capture(uint effectiveNpuMemoryMaximumInstanceCount, uint effectiveIntelFanMaximumInstanceCount, NativeMetricSnapshotCatalogProjection? prior = null, uint effectiveNpuEngineMaximumInstanceCount = 0, uint effectiveAmdCcdMaximumInstanceCount = 0)
    {
        NpuMemoryTopologyRead npus;
        try { npus = effectiveNpuMemoryMaximumInstanceCount == 0
            ? new(NativeMetricSnapshotCatalogTopologyStatus.Complete, [])
            : resources.ReadNpuMemoryIdentities(effectiveNpuMemoryMaximumInstanceCount); }
        catch (Exception) { npus = new(NativeMetricSnapshotCatalogTopologyStatus.Unavailable, []); }
        var network = resources.ReadNetworkIdentities();
        var numa = resources.ReadNumaIdentities();
        CpuOsTopologyInformation? cpu = null;
        try { cpu = readCpuTopology(); } catch (Exception) { }
        AmdCcdTopologyRead ccd;
        try { ccd = effectiveAmdCcdMaximumInstanceCount == 0
            ? new(NativeMetricSnapshotCatalogTopologyStatus.Complete, [])
            : resources.ReadAmdCcdIdentities(cpu, effectiveAmdCcdMaximumInstanceCount); }
        catch (Exception) { ccd = new(NativeMetricSnapshotCatalogTopologyStatus.Unavailable, []); }
        IntelMsrTopologyRead intel;
        try { intel = IntelMsrTopologyProjection.Create(cpu); }
        catch (Exception) { intel = new(NativeMetricSnapshotCatalogTopologyStatus.Unavailable,
            NativeMetricSnapshotCatalogTopologyStatus.Unavailable, NativeMetricSnapshotCatalogTopologyStatus.Unavailable, [])
            { PlatformStatus = NativeMetricSnapshotCatalogTopologyStatus.Unavailable }; }
        var result = CaptureDeviceTopology(effectiveIntelFanMaximumInstanceCount, prior) with { AmdCcdStatus = ccd.Status, AmdCcdIdentities = ccd.Identities, NpuMemoryStatus = npus.Status, NpuMemoryIdentities = npus.Identities, NetworkInterfaceStatus = network.Status, NetworkInterfaces = network.Identities,
            NumaStatus = numa.Status, NumaNodes = numa.Identities, IntelCpuCoreStatus = intel.CoreStatus,
            IntelCpuPackageStatus = intel.PackageStatus, IntelCpuLogicalProcessorStatus = intel.LogicalProcessorStatus,
            IntelCpuDieStatus = intel.DieStatus, IntelPlatformStatus = intel.PlatformStatus, IntelCpuIdentities = intel.Identities };
        result = WindowsNpuEngineTopology.AddDirectory(result, effectiveNpuEngineMaximumInstanceCount, resources.OpenNpuEngineDirectory, prior?.NpuEngineDirectory);
        if (prior is not null)
            result = WindowsMetricCatalogTopologyPublication.RetainUnavailableOptionalIdentities(result, prior);
        return Publish(result);
    }

    private static (IReadOnlyList<NativeMetricSnapshotGpuCatalogIdentity> Rows, string? FailureCode) CaptureGpuIdentities(WindowsGpuAdapterInventoryRead inventory)
    {
        if (inventory.Status != SamplingObservationStatus.Current
            || inventory.Generation == 0
            || inventory.ObservedAtUtcTicks <= 0
            || inventory.ObservedCount != inventory.Adapters.Count
            || inventory.SkippedCount != 0
            || inventory.OverflowCount != 0)
        {
            return ([], "gpu-inventory-unavailable");
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
                return ([], "gpu-identity-unavailable");
            }
            var luid = WindowsGpuAdapterIdentity.Pack(adapter.Luid);
            if (luid == 0)
            {
                return ([], "gpu-identity-unavailable");
            }

            var exactKey = CreateExactGpuKey(adapter);
            if (string.IsNullOrEmpty(exactKey))
            {
                return ([], "gpu-identity-unavailable");
            }
            rows.Add(new NativeMetricSnapshotGpuCatalogIdentity(
                adapter.Index,
                adapter.Name,
                exactKey,
                luid,
                inventory.Generation,
                VendorMask(adapter.VendorId),
                adapter.SupportsDedicatedMemoryMetrics)
            {
                PhysicalPnpInstanceIds = adapter.PhysicalIdentities
                    .OrderBy(static identity => identity.PnpInstanceId, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(static identity => identity.PhysicalAdapterIndex)
                    .Select(static identity => identity.PnpInstanceId)
                    .ToArray()
            });
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
            return ([], "gpu-identity-ambiguous");
        }

        return (rows, null);
    }

    private NativeMetricSnapshotCatalogTopologySnapshot CaptureDeviceTopology(uint effectiveIntelFanMaximumInstanceCount, NativeMetricSnapshotCatalogProjection? prior)
    {
        WindowsGpuAdapterInventoryRead inventory;
        try { inventory = resources.ReadGpuInventory(); }
        catch (Exception) { inventory = new(SamplingObservationStatus.Unavailable, 0, 0, 0, 0, 0, 0, []); }
        var gpu = CaptureGpuIdentities(inventory);
        var generation = checked((published?.Generation ?? 0) + 1);

        var storage = resources.ReadStorageIdentities(generation);
        NativeMetricSnapshotOrdinalTopologyRead fans;
        var fanConfigurationKnown = true;
        try { fans = resources.ReadFanIdentities(generation); }
        catch (Exception) { fans = new(NativeMetricSnapshotCatalogTopologyStatus.Unavailable, []); fanConfigurationKnown = false; }
        IReadOnlyList<NvidiaNvmlFanIdentity>? nvmlFans;
        try
        {
            nvmlFans = resources.ReadNvmlFanIdentities(inventory);
            if (nvmlFans is not null && (nvmlFans.Count > NvidiaNvmlReader.MaxFanCount
                || nvmlFans.Any(static fan => fan.FanIndex >= NvidiaNvmlReader.MaxFansPerDevice)
                || nvmlFans.Select(static fan => fan.ExactIdentityKey).Distinct(StringComparer.Ordinal).Count() != nvmlFans.Count))
                nvmlFans = null;
        }
        catch (Exception) { nvmlFans = null; }
        var fanRows = fans.Identities.Where(static fan => fan.NvmlFan is null).ToList();
        var nextFanIndex = fanRows.Count == 0 ? 0 : checked(fanRows.Max(static fan => fan.DisplayIndex) + 1);
        if (nvmlFans is not null)
            foreach (var fan in nvmlFans.OrderBy(static fan => fan.ExactIdentityKey, StringComparer.Ordinal))
                fanRows.Add(new(nextFanIndex++, fan.ExactIdentityKey, generation)
                {
                    NvmlFan = fan,
                    Device = new("fan", MetricDeviceAddress.GpuFan(fan.GpuPnpInstanceId, fan.FanIndex), null, nextFanIndex - 1)
                });
        var batteries = resources.ReadBatteryIdentities();
        var topology = new NativeMetricSnapshotCatalogTopologySnapshot(
            generation,
            gpu.FailureCode is null ? NativeMetricSnapshotCatalogTopologyStatus.Complete : NativeMetricSnapshotCatalogTopologyStatus.Unavailable,
            gpu.Rows,
            storage.Status,
            storage.Identities,
            fans.Status,
            fanRows)
        {
            GpuFailureCode = gpu.FailureCode,
            NvmlFanStatus = nvmlFans is null ? NativeMetricSnapshotCatalogTopologyStatus.Unavailable
                : NativeMetricSnapshotCatalogTopologyStatus.Complete,
            NotebookOemFanStatus = fanConfigurationKnown ? NativeMetricSnapshotCatalogTopologyStatus.Complete
                : NativeMetricSnapshotCatalogTopologyStatus.Unavailable,
            BatteryStatus = batteries.Status,
            Batteries = batteries.Identities,
            CpuVendor = CpuIdentityInformationReader.Read()?.Vendor switch
            {
                "GenuineIntel" => MetricVendor.Intel,
                "AuthenticAMD" => MetricVendor.Amd,
                _ => MetricVendor.Unknown
            }
        };
        return WindowsIntelFanTopology.AddDirectory(topology, inventory, effectiveIntelFanMaximumInstanceCount, resources.OpenIgclFanDirectory,
            WindowsIntelFanTopology.DirectoryOf(prior?.SystemFanIdentities ?? []));
    }

    internal static string CreateExactGpuKey(WindowsGpuAdapter adapter)
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

}


