namespace ResourceManager.App.Domain.Monitoring;

public readonly record struct HardwareMetricHistorySample(
    DateTimeOffset ObservedAt,
    double? Value);
