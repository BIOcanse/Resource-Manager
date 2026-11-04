using System.Globalization;
using ResourceManager.App.Application.Monitoring;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Endpoints.Transport;
using ResourceManager.ReadWrite.ProcessFields;

namespace ResourceManager.App.Endpoints;

public static partial class ResourceManagerEndpointRouteBuilderExtensions
{
    private sealed record HookReading(double? Value, double? Total, string Unit,
        string? Reason = null, bool Ended = false, IReadOnlyList<MonitoringHookOwner>? ProcessOwners = null)
    {
        internal IReadOnlyList<MonitoringHookOwner> Owners => ProcessOwners ?? [];
    }

    private static HookReading ReadHookMetric(HardwareMetricSnapshot current, string address, MonitoringHookTarget target,
        HardwareMetricCatalog catalog, string unit)
    {
        if (target.Binding == "device" && !catalog.FieldBindings.Values.Any(item => item.DeviceIdentity == target.DeviceIdentity)
            && !catalog.Items.Values.Any(item => item.DeviceIdentity == target.DeviceIdentity)
            && !catalog.Gpus.Any(item => item.IdentityKey == target.DeviceIdentity))
            return new(null, null, unit, "bound-device-ended", true);
        if (!current.Items.TryGetValue(address, out var value)) return new(null, null, unit, "metric-not-published");
        if (target.Binding == "device" && value.DeviceIdentity is not null && value.DeviceIdentity != target.DeviceIdentity)
            return new(null, null, value.Unit, "bound-device-ended", true);
        if (target.Binding == "device" && value.DeviceIdentity is null)
            return new(null, null, unit, "bound-device-unavailable");
        return value.Status == SamplingObservationStatus.Current
            ? new(HookNumber(value.NumericValue, value.IntegerValue), value.Total, value.Unit)
            : new(null, null, value.Unit, "metric-" + value.Status.ToString().ToLowerInvariant());
    }

    private static HookReading ReadHookMachineOwners(ResourceBreakdownSnapshot current, string metricId)
    {
        var facts = ResourceFactsSnapshot.Create(current, [metricId]);
        var metric = facts.Metrics.FirstOrDefault(item => item.MetricId == metricId);
        if (metric is null || metric.CapturedAt is null) return new(null, null, metric?.Unit ?? "", metric?.UnavailableReason ?? "metric-unavailable");
        var owners = metric.Software.SelectMany(item => item.Processes).Where(item => facts.Processes[item.ProcessIndex].ProcessId > 0)
            .Select(item => new MonitoringHookOwner(facts.Processes[item.ProcessIndex].Name, facts.Processes[item.ProcessIndex].ProcessId, item.Value)).ToArray();
        return new(metric.TotalValue, metric.CapacityValue, metric.Unit, metric.UnavailableReason, ProcessOwners: owners);
    }

    private static HookReading ReadHookObject(ResourceBreakdownSnapshot current, SchedulingProcessFactSnapshot? foundations,
        MonitoringHookTarget target, ProcessMetricFieldDefinition definition)
    {
        var unit = definition.Unit;
        if (foundations?.TryGetCurrentFoundationDataset(SamplingDatasetIds.ProcessInventory, out _) != true)
            return new(null, null, unit, "process-inventory-unavailable");
        var inventory = foundations.InventoryProcesses;
        if (target.Binding == "process" && !foundations.HasUnidentifiedProcesses && !inventory.Any(item => item.ProcessId == target.ProcessId
            && item.ProcessStartKey.ToString(CultureInfo.InvariantCulture) == target.StartKey))
            return new(null, null, unit, "bound-process-ended", true);
        if (target.Binding == "software" && !foundations.TryGetCurrentFoundationDataset(SamplingDatasetIds.ProcessAttribution, out _))
            return new(null, null, unit, "process-attribution-unavailable");
        var identities = target.Binding == "process"
            ? inventory.Where(item => item.ProcessId == target.ProcessId && item.ProcessStartKey.ToString(CultureInfo.InvariantCulture) == target.StartKey).ToArray()
            : foundations.AttributionProcesses.Where(item => item.SoftwareId == target.SoftwareId
                && inventory.Any(live => live.ProcessId == item.ProcessId && live.ProcessStartKey == item.ProcessStartKey)).ToArray();
        if (target.Binding == "software" && identities.Length == 0 && !foundations.HasUnidentifiedProcesses)
            return new(null, null, unit, "bound-software-ended", true);
        var fields = current.ProcessFields;
        if (fields?.Sources.FirstOrDefault(item => item.SourceId == definition.SourceId)?.CapturedAt is null)
            return new(null, null, unit, "process-field-unavailable");
        var owners = new List<MonitoringHookOwner>();
        double sum = 0;
        foreach (var identity in identities)
        {
            var field = fields.FindProcess(identity.ProcessId, identity.ProcessStartKey.ToString(CultureInfo.InvariantCulture), gpu: definition.GpuEngine)
                ?.Values.FirstOrDefault(item => item.Field.Equals(definition.Name, StringComparison.OrdinalIgnoreCase));
            var number = HookNumber(field?.NumericValue, field?.IntegerValue);
            if (number is null || !double.IsFinite(number.Value)) return new(null, null, unit, "process-field-unavailable");
            sum += number.Value;
            owners.Add(new(identity.ProcessName, identity.ProcessId, number.Value));
        }
        return new(sum, null, unit, ProcessOwners: owners);
    }

    private static double? HookNumber(double? number, MetricInteger? integer)
        => number ?? (integer is { } value ? (double)value.ToBigInteger() : null);
}
