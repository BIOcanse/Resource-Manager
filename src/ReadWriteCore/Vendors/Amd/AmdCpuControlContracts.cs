namespace ResourceManager.ReadWrite.Vendors.Amd;
public enum AmdCpuControlField { PowerLimit, SlowPowerLimit, FastPowerLimit, TdcLimit, EdcLimit, TemperatureLimit, PowerBoostScalar, CurveOptimizer }
public enum AmdCpuControlStatus { Applied, Failed }
public enum AmdCpuControlUnavailableKind { Platform, Component, NotImplemented }
public enum AmdCpuControlMessage { BridgeMissing, BridgeSilent, NotSupported, NotEffective, TemperatureLimitUnverifiable, CurrentLimitMappingUnconfirmed, NumberRequired, SmuRejected, Unreadable, ReadingMissing, StatusRejected, StatusWarning, ReadbackWarning, ReadBaselineWarning, WriteBaselineWarning }
public sealed record AmdCpuControlTarget(string Id, bool IsAmdCpu);
public sealed record AmdCpuControlRange(double Minimum, double Maximum, double Step, string Unit, double? DefaultValue = null);
public sealed record AmdCpuControlCapability(AmdCpuControlField Field, AmdCpuControlRange? Range = null)
{ public string Id => AmdCpuControlFormatter.Id(Field); }
public sealed record AmdCpuControlSetting(AmdCpuControlField Field, double? Number = null, string? Unit = null)
{ public string CapabilityId => AmdCpuControlFormatter.Id(Field); }
public sealed record AmdCpuControlValue(string ObjectId, string CapabilityId, double? Number = null, string? Unit = null, string? UnreadableReason = null);
public sealed record AmdCpuControlOutcome(string ObjectId, string CapabilityId, AmdCpuControlStatus Status, string? Message);
public sealed record AmdCpuControlOptions(Func<string,string> BaselinePath)
{
    public static AmdCpuControlOptions InDirectory(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path); var directory = Path.GetFullPath(path);
        return new(fileName => Path.Combine(directory, fileName));
    }
}
public readonly record struct AmdCpuControlAvailability(bool CanWrite, string? Reason = null, AmdCpuControlRange? Range = null,
    AmdCpuControlUnavailableKind? UnavailableKind = null, bool CanRead = false)
{
    public bool IsMine => CanWrite || Reason is not null;
    public static AmdCpuControlAvailability Yes(AmdCpuControlRange? range = null) => new(true, Range: range);
    public static AmdCpuControlAvailability No(string reason, AmdCpuControlRange? range = null,
        AmdCpuControlUnavailableKind kind = AmdCpuControlUnavailableKind.Platform, bool canRead = false)
        => new(false, reason, range, kind, canRead);
    public static AmdCpuControlAvailability NotMine => new(false);
}
public static class AmdCpuControlFormatter
{
    public static string Id(AmdCpuControlField field) => field switch
    {
        AmdCpuControlField.PowerLimit => "cpu.power-limit",
        AmdCpuControlField.SlowPowerLimit => "cpu.slow-power-limit",
        AmdCpuControlField.FastPowerLimit => "cpu.fast-power-limit",
        AmdCpuControlField.TdcLimit => "cpu.tdc-limit",
        AmdCpuControlField.EdcLimit => "cpu.edc-limit",
        AmdCpuControlField.TemperatureLimit => "cpu.temperature-limit",
        AmdCpuControlField.PowerBoostScalar => "cpu.pbo-scalar",
        AmdCpuControlField.CurveOptimizer => "cpu.curve-optimizer",
        _ => "cpu.unsupported"
    };
    public static AmdCpuControlField? Field(string id) => id switch
    {
        "cpu.power-limit" => AmdCpuControlField.PowerLimit,
        "cpu.slow-power-limit" => AmdCpuControlField.SlowPowerLimit,
        "cpu.fast-power-limit" => AmdCpuControlField.FastPowerLimit,
        "cpu.tdc-limit" => AmdCpuControlField.TdcLimit,
        "cpu.edc-limit" => AmdCpuControlField.EdcLimit,
        "cpu.temperature-limit" => AmdCpuControlField.TemperatureLimit,
        "cpu.pbo-scalar" => AmdCpuControlField.PowerBoostScalar,
        "cpu.curve-optimizer" => AmdCpuControlField.CurveOptimizer,
        _ => null
    };
}
