namespace ResourceManager.ReadWrite.Vendors.Amd.Smu;

public static class AmdSmuSensorIndexResolver
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

public sealed record AmdSmuSensorLayout(
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

    // Measured on an 8940HX (table 0x00540208, 10-28, 12 samples under a 75 W load): [9] follows the core rail's
    // power / voltage ([20] / [18]) within 2 %, limit [8] = 125 A; [63] swings 58-95 A and never falls below [9],
    // limit [62] = 176 A, so it is the EDC peak. [64] stays near 49 A, mostly below [9], and is not EDC.
    public static AmdSmuSensorLayout DragonRange { get; } = new(
        StapmPower: 1,
        ActualPower: 3,
        AveragePower: 5,
        TdcCurrent: 9,
        EdcCurrent: 63,
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
