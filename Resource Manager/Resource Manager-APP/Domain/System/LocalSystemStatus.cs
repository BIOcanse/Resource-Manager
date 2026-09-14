namespace ResourceManager.App.Domain.LocalSystem;

public sealed record LocalSystemStatus(
    DateTimeOffset CapturedAt,
    DateTimeOffset BootedAt,
    long UptimeSeconds);
