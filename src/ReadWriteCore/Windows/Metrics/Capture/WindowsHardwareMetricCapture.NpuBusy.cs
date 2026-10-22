using System.Globalization;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite.Native;

namespace ResourceManager.ReadWrite.Windows.Metrics.Capture;

internal sealed partial class WindowsHardwareMetricCapture
{
    internal static HardwareMetricSnapshot ProjectNpuDeviceBusyFields(
        HardwareMetricSnapshot snapshot,
        MetricSampleRequest request)
    {
        if (request.IsCatalogProbe)
            return snapshot;

        var selected = snapshot.FieldBindings.Values
            .Where(binding => binding.Field.Code == MetricFieldCode.NpuDeviceEngineBusyPercent
                && (request.IsAll || request.Includes(binding.RequestAddress)
                    || request.Includes(binding.EffectiveSampleRequestAddress)))
            .ToArray();
        if (selected.Length == 0)
            return snapshot;

        var items = new Dictionary<string, MetricValue>(snapshot.Items, StringComparer.OrdinalIgnoreCase);
        var datasets = new Dictionary<string, HardwareMetricDatasetObservation>(snapshot.Datasets,
            StringComparer.OrdinalIgnoreCase);

        foreach (var binding in selected)
        {
            var engines = snapshot.FieldBindings.Values
                .Where(engine => engine.Field.Code == MetricFieldCode.NpuEngineReportedRunningTimeRatioPercent
                    && engine.NpuEngine is { } identity
                    && identity.ParentDeviceIdentity == binding.DeviceIdentity)
                .OrderBy(static engine => engine.NpuEngine!.EngineIndex)
                .ToArray();

            var maximum = 0d;
            var sourceGeneration = 0UL;
            var complete = IsCompleteEngineSet(engines);
            if (complete)
                complete = TryGetCompleteBusyRatio(snapshot, engines, out maximum, out sourceGeneration);
            if (complete)
            {
                var capturedAt = snapshot.CapturedAt.UtcTicks;
                var observation = new HardwareMetricDatasetObservation(
                    binding.RequestAddress,
                    SamplingObservationStatus.Current,
                    sourceGeneration,
                    capturedAt,
                    snapshot.WorkspaceIdentity,
                    snapshot.ConfigurationGeneration,
                    snapshot.CatalogGeneration,
                    snapshot.CommittedGeneration)
                {
                    LastAttemptAtUtcTicks = capturedAt,
                    LastSuccessAtUtcTicks = capturedAt
                };
                datasets[binding.RequestAddress] = observation;
                items[binding.RequestAddress] = new MetricValue(
                    binding.RequestAddress,
                    binding.Field.Name,
                    "npu",
                    maximum.ToString("F1", CultureInfo.InvariantCulture) + "%",
                    maximum,
                    binding.Field.Unit,
                    maximum,
                    null)
                {
                    FieldName = binding.Field.Name,
                    DeviceIdentity = binding.DeviceIdentity,
                    ValueKind = binding.Field.ValueKind,
                    SourceId = binding.SourceId
                };
            }
            else
            {
                datasets[binding.RequestAddress] = new HardwareMetricDatasetObservation(
                    binding.RequestAddress,
                    SamplingObservationStatus.Unavailable,
                    0,
                    0,
                    snapshot.WorkspaceIdentity,
                    snapshot.ConfigurationGeneration,
                    snapshot.CatalogGeneration,
                    snapshot.CommittedGeneration)
                {
                    LastAttemptAtUtcTicks = snapshot.CapturedAt.UtcTicks,
                    FailureCode = "npu-engine-set-incomplete"
                };
                items.Remove(binding.RequestAddress);
            }

            foreach (var alias in binding.RequestAliases)
            {
                if (complete && datasets.TryGetValue(binding.RequestAddress, out var observation)
                    && items.TryGetValue(binding.RequestAddress, out var value))
                {
                    datasets[alias] = observation with { DatasetId = alias };
                    items[alias] = value with { Id = alias };
                }
                else
                {
                    datasets.Remove(alias);
                    items.Remove(alias);
                }
            }
        }

        return snapshot with { Items = items, Datasets = datasets };
    }

    private static bool IsCompleteEngineSet(IReadOnlyList<MetricDeviceFieldBinding> engines)
    {
        if (engines.Count == 0 || engines.Any(static binding => binding.NpuEngine is null))
            return false;
        var first = engines[0].NpuEngine!;
        if (first.EngineCount == 0 || engines.Count != first.EngineCount)
            return false;

        for (var index = 0; index < engines.Count; index++)
        {
            var engine = engines[index].NpuEngine!;
            if (engine.EngineIndex != index
                || engine.EngineCount != first.EngineCount
                || engine.ParentDeviceIdentity != first.ParentDeviceIdentity
                || engine.PnpInstanceId != first.PnpInstanceId
                || engine.DirectoryId != first.DirectoryId
                || engine.AdapterLuid != first.AdapterLuid
                || engine.VendorId != first.VendorId
                || engine.DeviceId != first.DeviceId
                || engine.DriverVersionDeclared != first.DriverVersionDeclared
                || engine.PnpDriverVersionDeclared != first.PnpDriverVersionDeclared)
                return false;
        }

        return true;
    }

    private static bool TryGetCompleteBusyRatio(
        HardwareMetricSnapshot snapshot,
        IReadOnlyList<MetricDeviceFieldBinding> engines,
        out double maximum,
        out ulong sourceGeneration)
    {
        maximum = 0;
        sourceGeneration = 0;
        var hasValue = false;
        foreach (var binding in engines)
        {
            if (binding.SourceId != NativeMetricSnapshotSources.WindowsNpuEngine
                || !snapshot.Datasets.TryGetValue(binding.RequestAddress, out var observation)
                || observation.Status != SamplingObservationStatus.Current
                || observation.FailureCode is not null
                || observation.ObservedAtUtcTicks <= 0
                || observation.LastSuccessAtUtcTicks != observation.ObservedAtUtcTicks
                || observation.WorkspaceIdentity != snapshot.WorkspaceIdentity
                || observation.ConfigurationGeneration != snapshot.ConfigurationGeneration
                || observation.CatalogGeneration != snapshot.CatalogGeneration
                || observation.CommittedGeneration != snapshot.CommittedGeneration
                || !snapshot.Items.TryGetValue(binding.RequestAddress, out var value)
                || value.NumericValue is not { } ratio
                || !double.IsFinite(ratio)
                || ratio < 0)
                return false;

            if (!hasValue)
            {
                sourceGeneration = observation.SourceGeneration;
                hasValue = true;
            }
            else if (sourceGeneration != observation.SourceGeneration)
            {
                return false;
            }

            maximum = Math.Max(maximum, ratio);
        }

        return hasValue;
    }
}
