namespace ResourceManager.App.Domain.SystemHealth;

public sealed record StorageHealthSnapshot(
    DateTimeOffset CapturedAt,
    bool Available,
    IReadOnlyList<PhysicalDiskHealthSnapshot> Disks,
    string? Error)
{
    public static StorageHealthSnapshot Unavailable(string error)
    {
        return new StorageHealthSnapshot(DateTimeOffset.UtcNow, false, [], error);
    }
}

public sealed record PhysicalDiskHealthSnapshot(
    string DeviceKey,
    string DeviceId,
    string FriendlyName,
    string? SerialNumber,
    string? UniqueId,
    ulong? SizeBytes,
    uint? MediaType,
    uint? BusType,
    uint? HealthStatus,
    IReadOnlyList<uint> OperationalStatus,
    string? PhysicalLocation,
    bool IsExternal,
    double? TemperatureCelsius,
    double? TemperatureMaximumCelsius,
    double? WearPercent,
    ulong? ReadErrorsUncorrected,
    ulong? WriteErrorsUncorrected,
    ulong? StartStopCycleCount,
    ulong? LoadUnloadCycleCount,
    ulong? ReadLatencyMaximumMilliseconds,
    ulong? WriteLatencyMaximumMilliseconds,
    ulong? FlushLatencyMaximumMilliseconds);
