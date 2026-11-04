using System.Collections.Immutable;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite.Native;
using ResourceManager.ReadWrite.Native.Pdh;
using ResourceManager.ReadWrite.Windows.Pdh;
using ResourceManager.ReadWrite.Windows.Gpu;
using ResourceManager.ReadWrite.Windows.Platform;
using ResourceManager.ReadWrite.Windows.Devices;
using ResourceManager.ReadWrite.Windows.Devices.Models;
using ResourceManager.ReadWrite.Windows.Devices.Battery;
using ResourceManager.ReadWrite.Windows.Cpu;
using ResourceManager.ReadWrite.Windows.Cpu.Models;
using ResourceManager.ReadWrite.Vendors.Nvidia;
using ResourceManager.ReadWrite.Vendors.Amd;
using ResourceManager.ReadWrite.Vendors.Amd.Smu;
using ResourceManager.ReadWrite.Vendors.Intel;
using ResourceManager.ReadWrite.Vendors.Intel.Msr;
using ResourceManager.ReadWrite.Windows.Metrics.Resources;
namespace ResourceManager.ReadWrite.Windows.Metrics.Capture;

internal sealed partial class WindowsHardwareMetricCapture
{
    private NativeMetricSnapshotSourceCompletion CollectNetworkInterfaces(NativeMetricSnapshotCollectionPlan plan,
        NativeMetricSnapshotSourcePlanOutput sourcePlan, NativeMetricSnapshotSourceObservationIdentity identity,
        DateTimeOffset capturedAt)
    {
        var requested = SelectedBindings(plan, sourcePlan.SourceHandle).GroupBy(static binding => binding.DeviceIdentity)
            .ToDictionary(static group => group.Key,
                static group => (IReadOnlySet<MetricFieldCode>)group.Select(static binding => binding.FieldCode).ToHashSet(), StringComparer.Ordinal);
        var values = resources.ReadNetworkInterfaces(plan.Catalog.NetworkInterfaceIdentities, requested);
        ObserveTopology(NativeMetricSnapshotSources.WindowsNetworkInterface, "selected-identity-changed",
            values.Any(static value => value.RequiresTopologyRefresh));
        return CreateNetworkInterfaceCompletion(plan, sourcePlan, identity, capturedAt, values);
    }

    internal static NativeMetricSnapshotSourceCompletion CreateNetworkInterfaceCompletion(NativeMetricSnapshotCollectionPlan plan,
        NativeMetricSnapshotSourcePlanOutput sourcePlan, NativeMetricSnapshotSourceObservationIdentity identity,
        DateTimeOffset capturedAt, IReadOnlyList<NetworkInterfaceMetricRead> values)
    {
        var devices = values.ToDictionary(static value => value.DeviceIdentity, StringComparer.Ordinal);
        return CompleteWithValues(plan, sourcePlan, identity, capturedAt, _ => null,
            unsignedValue: binding => devices.TryGetValue(binding.DeviceIdentity, out var device)
                && device.Counters.TryGetValue(binding.FieldCode, out var value) ? value : null,
            timedCounterValue: binding => devices.TryGetValue(binding.DeviceIdentity, out var device)
                && device.Rates.TryGetValue(binding.FieldCode, out var value) ? value : null);
    }
}


