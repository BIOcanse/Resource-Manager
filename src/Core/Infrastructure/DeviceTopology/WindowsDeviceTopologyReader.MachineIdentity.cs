using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.ReadWrite.Windows.Cpu;
using ResourceManager.ReadWrite.Windows.Devices.Models;
using ResourceManager.ReadWrite.Windows.Devices.Battery;

namespace ResourceManager.App.Infrastructure.DeviceTopology;

public sealed partial class WindowsDeviceTopologyReader
{
    internal static DeviceTopologyMachineIdentity ReadMachineIdentity(DeviceTopologyMachineClassifierOwner classifier,
        SmbiosFirmwareCapture? firmware, BatteryTopologyRead batteries, WindowsGpuAdapterInventoryRead? gpuInventory)
    {
        // Reuse the CPU topology's brand source; no WMI query, fallback model or periodic sampler.
        var brand = CpuIdentityInformationReader.Read([])?.Brand;
        return classifier.Classify(new(
            new(firmware?.ChassisTypes, firmware is null ? "source-unavailable"
                : firmware.ChassisTypes is null ? "no-record" : null),
            SystemBatteryEvidence(batteries),
            new(string.IsNullOrWhiteSpace(brand) ? null : [brand],
                string.IsNullOrWhiteSpace(brand) ? "missing-model" : null),
            GpuModelEvidence(gpuInventory), []) { GpuAttachments = GpuAttachmentEvidence(gpuInventory) });
    }

    internal static DeviceTopologyIdentityFact<IReadOnlyList<DeviceTopologyMachineGpuAttachment>?> GpuAttachmentEvidence(
        WindowsGpuAdapterInventoryRead? inventory)
    {
        var models = GpuModelEvidence(inventory);
        if (models.Value is null) return new(null, models.Reason);
        return new(inventory!.Adapters.Where(static adapter => !adapter.IsSoftware && !string.IsNullOrWhiteSpace(adapter.Name))
            .Select(static adapter => new DeviceTopologyMachineGpuAttachment(adapter.Name, adapter.IsDetachable,
                adapter.AdapterTypeFlags)).ToArray(), null);
    }

    internal static DeviceTopologyIdentityFact<IReadOnlyList<string>?> GpuModelEvidence(WindowsGpuAdapterInventoryRead? inventory)
    {
        if (inventory?.Status != SamplingObservationStatus.Current)
            return new(null, "source-unavailable");
        // A known adapter name does not require a successful LUID/PNP/driver-version binding.
        var hardware = inventory.Adapters.Where(static adapter => !adapter.IsSoftware).ToArray();
        var models = hardware.Select(static adapter => adapter.Name)
            .Where(static name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal).ToArray();
        if (models.Length == 0 && (inventory.SkippedCount != 0 || inventory.OverflowCount != 0))
            return new(null, "partial-enumeration");
        return models.Length == 0 && hardware.Length > 0 ? new(null, "missing-model") : new(models, null);
    }

    internal static DeviceTopologyIdentityFact<bool?> SystemBatteryEvidence(BatteryTopologyRead batteries)
    {
        if (batteries.Status != NativeMetricSnapshotCatalogTopologyStatus.Complete)
            return new(null, "source-unavailable");
        // Windows BATTERY_INFORMATION: a UPS can also carry SYSTEM_BATTERY, so exclude SHORT_TERM.
        if (batteries.Identities.Any(static battery =>
            (battery.Information.Capabilities & 0xa0000000u) == 0x80000000u)) return new(true, null);
        return batteries.RejectedCount > 0 ? new(null, "partial-enumeration") : new(false, null);
    }
}
