namespace ResourceManager.App.Domain.Settings;

// Action history capacity is a user setting; reaching it never evicts old records.
public sealed record AppDeviceRestartSettings
{
    public int RecordCapacity { get; init; } = 4096;
}
