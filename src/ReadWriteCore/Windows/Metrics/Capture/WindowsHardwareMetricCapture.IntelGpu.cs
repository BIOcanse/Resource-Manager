using ResourceManager.ReadWrite.Windows.Metrics.Topology;
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
    private NativeMetricSnapshotSourceCompletion CollectIntelGpu(NativeMetricSnapshotCollectionPlan plan,
        NativeMetricSnapshotSourcePlanOutput sourcePlan, NativeMetricSnapshotSourceObservationIdentity identity,
        DateTimeOffset capturedAt, CancellationToken cancellationToken)
    {
        var selected = SelectedBindings(plan, sourcePlan.SourceHandle);
        var requested = selected.Where(static binding => binding.IgclFan is null).GroupBy(static binding => binding.DisplayIndex).ToDictionary(static group => group.Key,
            static group => (IReadOnlySet<MetricFieldCode>)group.Select(static binding => binding.FieldCode).ToHashSet());
        var inventory = RequireCurrentGpuInventory(plan.Catalog, plan.Catalog.SourceIds[sourcePlan.SourceHandle]);
        IReadOnlyList<IntelGclMetricRead> values = requested.Count == 0 ? [] : resources.ReadIntelGpu(inventory, requested, cancellationToken);
        var targets = selected.Where(static binding => binding.IgclFan is not null)
            .Select(static binding => binding.IgclFan!).DistinctBy(static fan => fan.ExactIdentityKey).ToArray();
        IReadOnlyList<IntelGclFanReading> fans = targets.Length == 0 ? []
            : resources.ReadIgclFans(targets, inventory,
                plan.Catalog.IgclFanEffectiveMaximumInstanceCount, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (values.Count == 0 && fans.Count == 0) return Unavailable(sourcePlan.SourceHandle, identity, capturedAt);
        var byFan = fans.GroupBy(static fan => fan.Fan.ExactIdentityKey, StringComparer.Ordinal)
            .Where(static group => group.Count() == 1).ToDictionary(static group => group.Key, static group => group.Single(), StringComparer.Ordinal);
        var byIndex = values.ToDictionary(static value => value.Index);
        return CompleteWithValues(plan, sourcePlan, identity, capturedAt,
            binding => binding.IgclFan is { } fan
                ? byFan.TryGetValue(fan.ExactIdentityKey, out var reading) && ReferenceEquals(reading.Fan, fan) ? reading.Rpm : null
                : byIndex.TryGetValue(binding.DisplayIndex, out var row)
                    && row.Values.TryGetValue(binding.FieldCode, out var value) ? value : null,
            unsignedValue: binding => binding.IgclFan is null && byIndex.TryGetValue(binding.DisplayIndex, out var row)
                && row.Counters.TryGetValue(binding.FieldCode, out var value) ? value : null,
            booleanValue: binding => binding.IgclFan is null && byIndex.TryGetValue(binding.DisplayIndex, out var row)
                && row.Flags.TryGetValue(binding.FieldCode, out var value) ? value : null,
            floatingCounterPairValue: binding => binding.IgclFan is null && byIndex.TryGetValue(binding.DisplayIndex, out var row)
                && row.FloatingCounters.TryGetValue(binding.FieldCode, out var value) ? value : null);
    }
}


