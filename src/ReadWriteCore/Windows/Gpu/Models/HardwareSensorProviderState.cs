namespace ResourceManager.App.Domain.Metrics;

public sealed record HardwareSensorProviderState(
    string Provider,
    string State,
    string? Message);

