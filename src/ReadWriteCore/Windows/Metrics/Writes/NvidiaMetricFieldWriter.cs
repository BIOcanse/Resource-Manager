using System.Collections.Immutable;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite.Vendors.Nvidia;
using ResourceManager.ReadWrite.Windows.Gpu;
using ResourceManager.ReadWrite.Windows.Metrics.Topology;

namespace ResourceManager.ReadWrite.Windows.Metrics.Writes;

// Declaration metadata is refreshed only by explicit discovery. No numeric current is retained here.
internal sealed class NvidiaMetricFieldWriter(NvidiaGpuControlResource resource,
    Func<WindowsGpuAdapterInventoryRead> readInventory)
{
    private ImmutableDictionary<string, NvidiaControlWriteAvailability> declarations = ImmutableDictionary<string, NvidiaControlWriteAvailability>.Empty;
    private const string Capability = "gpu.power-limit";
    private static bool Matches(MetricDeviceFieldBinding binding)
        => binding.Field.Code == MetricFieldCode.GpuPowerLimit && binding.Field.Vendor == MetricVendor.Nvidia
            && binding.SourceId == NativeMetricSnapshotSources.NvidiaNvml && binding.Field.Unit == "W"
            && binding.Field.ValueKind == "float64" && binding.AdapterKey is > 0;

    internal void Discover(HardwareMetricSnapshot snapshot, bool frozen)
    {
        var next = ImmutableDictionary.CreateBuilder<string, NvidiaControlWriteAvailability>();
        if (!frozen)
        {
            var inventory = readInventory();
            foreach (var binding in snapshot.FieldBindings.Values.Where(Matches))
                next[binding.DeviceIdentity] = Resolve(binding, inventory) is { } target
                    ? resource.Probe(target, new(Capability))
                    : NvidiaControlWriteAvailability.No("nvidia-device-identity-unavailable");
        }
        Volatile.Write(ref declarations, next.ToImmutable());
    }

    internal OperationSupport Query(MetricDeviceFieldBinding binding)
    {
        if (!Matches(binding)) return OperationSupport.Unsupported("metric-write-owner-not-bound");
        var facts = Volatile.Read(ref declarations);
        return facts.TryGetValue(binding.DeviceIdentity, out var support)
            ? new(support.CanWrite, support.Reason, [NativeMetricSnapshotSources.NvidiaNvml])
            : new(false, "nvidia-write-support-not-discovered", [NativeMetricSnapshotSources.NvidiaNvml]);
    }

    internal async ValueTask<FieldWriteResult> WriteAsync(FieldReference field, MetricDeviceFieldBinding binding,
        FieldValue value, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!Matches(binding)) return new(field, "unsupported", "metric-write-owner-not-bound");
        if (!value.Matches("float64") || value.Number is not { } number)
            return new(field, "failed", "control-write-value-invalid");
        // Re-read exact Windows PNP/PCI identity and the driver's current range; never redirect by ordinal.
        var target = Resolve(binding, readInventory());
        if (target is null) return new(field, "failed", "nvidia-device-identity-unavailable");
        var support = resource.Probe(target, new(Capability));
        if (!support.CanWrite || support.Range is not { } range)
            return new(field, "unsupported", support.Reason ?? "nvidia-power-limit-unavailable");
        if (number < range.Minimum - 0.000001 || number > range.Maximum + 0.000001)
            return new(field, "failed", "control-write-value-out-of-range");
        token.ThrowIfCancellationRequested();
        var result = await resource.WriteAsync(target, new(Capability, range), new(Capability, number, "W"), token);
        // Original NVML outcome means setter acceptance. Collect performs the explicit readback.
        return new(field, result.Status, result.NativeStatus is > 0
            ? "nvidia-nvml-status-" + result.NativeStatus.Value : result.Message);
    }

    private static NvidiaControlObject? Resolve(MetricDeviceFieldBinding binding, WindowsGpuAdapterInventoryRead inventory)
    {
        if (inventory.Status != SamplingObservationStatus.Current || inventory.Generation == 0
            || inventory.ObservedAtUtcTicks <= 0 || inventory.SkippedCount != 0 || inventory.OverflowCount != 0
            || inventory.ObservedCount != inventory.Adapters.Count) return null;
        var matches = inventory.Adapters.Where(adapter => !adapter.IsSoftware && adapter.VendorId == 0x10DE
            && adapter.Kind == WindowsGpuAdapterKind.Dedicated
            && adapter.PhysicalIdentityStatus == SamplingObservationStatus.Current
            && WindowsGpuAdapterIdentity.Pack(adapter.Luid) == binding.AdapterKey
            && WindowsHardwareMetricTopologySource.CreateExactGpuKey(adapter) == binding.DeviceIdentity).ToArray();
        return matches.Length == 1
            ? new(binding.DeviceIdentity, matches[0].Index, binding.AdapterKey) { Inventory = inventory }
            : null;
    }
}
