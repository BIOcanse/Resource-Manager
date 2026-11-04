using ResourceManager.App.Domain.Metrics;

namespace ResourceManager.App.Domain.Metrics;

// HTTP/subscription materialization. Backend readers use MetricReadingValue.
// Static identity strings reference catalog declarations; exact integer text is
// produced by MetricIntegerJsonConverter only while writing a response.
public sealed record MetricValue(string Id, double? NumericValue, string Unit,
    double? Percent = null, double? Total = null)
{
    public MetricFieldCode FieldCode { get; init; }
    public string? FieldName { get; init; }
    public string? DeviceIdentity { get; init; }
    public MetricInteger? IntegerValue { get; init; }
    public byte[]? BytesValue { get; init; }
    public bool? BooleanValue { get; init; }
    public string? ValueKind { get; init; }
    public string? SourceId { get; init; }
    public SamplingObservationStatus Status { get; init; }
    public DateTimeOffset? ObservedAt { get; init; }
    public ulong? SampleDurationMilliseconds { get; init; }

    public static MetricValue From(MetricReadingValue value) => new(value.Id, value.NumericValue,
        value.Unit, value.Percent, value.Total)
    {
        FieldCode = value.FieldCode, FieldName = value.FieldName, DeviceIdentity = value.DeviceIdentity,
        IntegerValue = value.IntegerValue, BytesValue = value.BytesValue, BooleanValue = value.BooleanValue,
        ValueKind = value.ValueKind, SourceId = value.SourceId, Status = value.Status,
        ObservedAt = value.ObservedAt, SampleDurationMilliseconds = value.SampleDurationMilliseconds
    };
}
