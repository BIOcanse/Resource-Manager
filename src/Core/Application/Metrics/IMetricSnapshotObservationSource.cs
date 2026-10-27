using ResourceManager.App.Domain.Metrics;
using System.Collections.Immutable;

namespace ResourceManager.App.Application.Metrics;

public interface IMetricSnapshotObservationSource
{
    IDisposable AcquireSubscription(
        string subscriptionId,
        MetricSampleRequest request,
        TimeSpan refreshInterval);

    /// <summary>Reads published slots beneath an owned subscription, or for an
    /// explicit cache inspector. One-shot consumers use the push source's FetchOnceAsync.</summary>
    HardwareMetricSnapshot? ReadLatest(MetricSampleRequest request);

    IReadOnlyDictionary<string, MetricReadingValue> ReadItems(ImmutableArray<string> ids)
    {
        var result = new Dictionary<string, MetricReadingValue>(StringComparer.OrdinalIgnoreCase);
        var snapshot = ReadLatest(MetricSampleRequest.ForIds(ids));
        if (snapshot is null) return result;
        foreach (var id in ids.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!snapshot.FieldBindings.TryGetValue(id, out var binding))
            {
                result.Add(id, new(id, null, string.Empty) { Status = SamplingObservationStatus.Unavailable });
                continue;
            }
            var observation = snapshot.Datasets.GetValueOrDefault(id);
            var status = observation?.Status ?? SamplingObservationStatus.Warming;
            var value = status == SamplingObservationStatus.Current ? snapshot.Items.GetValueOrDefault(id) : null;
            result.Add(id, (value ?? new MetricReadingValue(id, null, binding.Field.Unit)) with
            {
                FieldCode = binding.Field.Code, FieldName = binding.Field.Name,
                Unit = binding.Field.Unit,
                DeviceIdentity = binding.DeviceIdentity, ValueKind = binding.Field.ValueKind,
                SourceId = value?.SourceId ?? binding.SourceId, Status = status,
                ObservedAt = status == SamplingObservationStatus.Current && observation!.ObservedAtUtcTicks > 0
                    ? new DateTimeOffset(observation.ObservedAtUtcTicks, TimeSpan.Zero) : null
            });
        }
        return result;
    }
}
