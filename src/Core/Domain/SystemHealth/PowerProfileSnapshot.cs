namespace ResourceManager.App.Domain.SystemHealth;

public sealed record PowerProfileSnapshot(
    DateTimeOffset CapturedAt,
    bool Available,
    bool OnAcPower,
    bool BatterySaverEnabled,
    Guid? ActiveSchemeId,
    string ActiveSchemeName,
    uint? ProcessorMaximumAcPercent,
    uint? ProcessorEnergyPreferenceAc,
    uint? ProcessorBoostModeAc,
    uint? DiskIdleAcSeconds,
    uint? DiskIdleDcSeconds,
    string? Error)
{
    public static PowerProfileSnapshot Unavailable(string error)
    {
        return new PowerProfileSnapshot(
            DateTimeOffset.UtcNow,
            false,
            false,
            false,
            null,
            "未知",
            null,
            null,
            null,
            null,
            null,
            error);
    }
}
