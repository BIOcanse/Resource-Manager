namespace ResourceManager.App.Domain.Metrics;

public sealed record MetricValue(
    string Id,
    string Label,
    string Group,
    // 后端已经拼好的一句显示值。容量类指标不再走这里：它们的 Unit 是 MetricUnits.Bytes，
    // NumericValue 是原始字节，换算、跳档和单位标签一律由前端负责。
    string DisplayValue,
    double? NumericValue,
    string Unit,
    double? Percent,
    string? Detail,
    // 和 NumericValue 同单位的总量。只有存在有意义总量的指标才有（内存、虚拟内存、显存）。
    double? Total = null)
{
    public string? FieldName { get; init; }
    public string? DeviceIdentity { get; init; }
    // Exact integers are decimal strings on the wire so JavaScript cannot round 64/128-bit integers.
    public string? IntegerValue { get; init; }
    // JSON serializes byte[] as Base64; these bytes never pass through numeric conversion.
    public byte[]? BytesValue { get; init; }
    public bool? BooleanValue { get; init; }
    public string? ValueKind { get; init; }
    public string? SourceId { get; init; }
    // Actual interval reported by the committed source, never the subscription cadence.
    public ulong? SampleDurationMilliseconds { get; init; }
}
