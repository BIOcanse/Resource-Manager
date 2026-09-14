namespace ResourceManager.App.Domain.Monitoring;

internal readonly record struct HardwareMetricHistorySample(
    DateTimeOffset ObservedAt,
    double? Value);
