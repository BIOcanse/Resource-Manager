namespace ResourceManager.App.Domain.Metrics;

// Current authority facts. Unit and value kind are stable contract codes, not
// labels. Presentation is deliberately absent from both the SDK and the wire.
public sealed record MetricReadingValue(string Id, double? NumericValue, string Unit,
    double? Percent = null, double? Total = null)
{
    public MetricFieldCode FieldCode { get; init; }
    public string? FieldName { get; init; }
    public string? DeviceIdentity { get; init; }
    public MetricInteger? IntegerValue { get; init; }
    private readonly byte[]? bytesValue;
    // The public byte-array wire/SDK contract stays intact. Neither a producer
    // nor a reader can mutate this value's privately owned bytes.
    public byte[]? BytesValue { get => bytesValue?.ToArray(); init => bytesValue = value?.ToArray(); }
    public bool? BooleanValue { get; init; }
    public string? ValueKind { get; init; }
    public string? SourceId { get; init; }
    public SamplingObservationStatus Status { get; init; } = SamplingObservationStatus.Current;
    public DateTimeOffset? ObservedAt { get; init; }
    public ulong? SampleDurationMilliseconds { get; init; }
}
