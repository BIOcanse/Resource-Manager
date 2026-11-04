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
    private NativeMetricSnapshotSourceCompletion CollectIntelMsr(NativeMetricSnapshotCollectionPlan plan,
        NativeMetricSnapshotSourcePlanOutput sourcePlan, NativeMetricSnapshotSourceObservationIdentity identity,
        DateTimeOffset capturedAt, NativeMetricSnapshotCatalogTopologySnapshot? topology)
    {
        // An unavailable platform directory retains declarations only. A prior target
        // is not fresh platform evidence even if its old representative still reads.
        var unavailablePlatforms = topology?.IntelPlatformStatus == NativeMetricSnapshotCatalogTopologyStatus.Complete
            ? new HashSet<string>(StringComparer.Ordinal)
            : plan.Catalog.IntelCpuIdentities.Where(static target => target.IsPlatform)
                .Select(static target => target.ExactIdentityKey).ToHashSet(StringComparer.Ordinal);
        var requested = SelectedBindings(plan, sourcePlan.SourceHandle)
            .Where(binding => !unavailablePlatforms.Contains(binding.DeviceIdentity))
            .GroupBy(static binding => binding.DeviceIdentity)
            .ToDictionary(static group => group.Key,
                static group => (IReadOnlySet<MetricFieldCode>)group.Select(static binding => binding.FieldCode).ToHashSet(), StringComparer.Ordinal);
        IReadOnlyList<IntelMsrMetricRead> values = requested.Count == 0 ? []
            : resources.ReadIntelMsr(plan.Catalog.IntelCpuIdentities, requested);
        ObserveTopology(NativeMetricSnapshotSources.IntelMsr, "selected-identity-changed",
            values.Any(static value => value.RequiresTopologyRefresh));
        return CreateIntelMsrCompletion(plan, sourcePlan, identity, capturedAt, values);
    }
    internal static NativeMetricSnapshotSourceCompletion CreateIntelMsrCompletion(NativeMetricSnapshotCollectionPlan plan,
        NativeMetricSnapshotSourcePlanOutput sourcePlan, NativeMetricSnapshotSourceObservationIdentity identity,
        DateTimeOffset capturedAt, IReadOnlyList<IntelMsrMetricRead> values)
    {
        var devices = values.ToDictionary(static value => value.DeviceIdentity, StringComparer.Ordinal);
        return CompleteWithValues(plan, sourcePlan, identity, capturedAt,
            binding => devices.TryGetValue(binding.DeviceIdentity, out var device)
                && device.Values.TryGetValue(binding.FieldCode, out var value) ? value : null,
            unsignedValue: binding => devices.TryGetValue(binding.DeviceIdentity, out var device)
                && device.UnsignedValues.TryGetValue(binding.FieldCode, out var value) ? value : null,
            counterPairValue: binding => devices.TryGetValue(binding.DeviceIdentity, out var device)
                && device.CounterPairs.TryGetValue(binding.FieldCode, out var pair) ? pair : null,
            booleanValue: binding => devices.TryGetValue(binding.DeviceIdentity, out var device)
                && device.BooleanValues.TryGetValue(binding.FieldCode, out var value) ? value : null);
    }
}


