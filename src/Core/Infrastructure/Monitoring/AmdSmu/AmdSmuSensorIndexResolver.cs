namespace ResourceManager.App.Infrastructure.Monitoring.AmdSmu;

internal static class AmdSmuSensorIndexResolver
{
    public static AmdSmuSensorLayout? Resolve(uint tableVersion)
    {
        return tableVersion switch
        {
            0x004C0009 => AmdSmuSensorLayout.HawkPoint,
            0x00540208 => AmdSmuSensorLayout.DragonRange,
            _ => null
        };
    }
}

internal sealed record AmdSmuSensorLayout(
    int StapmPower,
    int ActualPower,
    int AveragePower,
    int TdcCurrent,
    int EdcCurrent,
    int CpuTemperature,
    int SocPower,
    int SocVoltage,
    int ApuFrequency,
    int ApuVoltage,
    int ApuVoltageFallback,
    int ApuTemperature,
    int CpuFrequencyStart,
    int CpuVoltageStart,
    bool IsHawkPointVoltageLayout)
{
    public static AmdSmuSensorLayout HawkPoint { get; } = new(
        StapmPower: 1,
        ActualPower: 3,
        AveragePower: 5,
        TdcCurrent: 9,
        EdcCurrent: 13,
        CpuTemperature: 17,
        SocPower: 112,
        SocVoltage: 110,
        ApuFrequency: 216,
        ApuVoltage: 42,
        ApuVoltageFallback: 39,
        ApuTemperature: 214,
        CpuFrequencyStart: 553,
        CpuVoltageStart: 105,
        IsHawkPointVoltageLayout: true);

    public static AmdSmuSensorLayout DragonRange { get; } = new(
        StapmPower: 1,
        ActualPower: 3,
        AveragePower: 5,
        TdcCurrent: 9,
        EdcCurrent: 64,
        CpuTemperature: 11,
        SocPower: 21,
        SocVoltage: 53,
        ApuFrequency: 101,
        ApuVoltage: 98,
        ApuVoltageFallback: -1,
        ApuTemperature: 99,
        CpuFrequencyStart: 346,
        CpuVoltageStart: 314,
        IsHawkPointVoltageLayout: false);
}
