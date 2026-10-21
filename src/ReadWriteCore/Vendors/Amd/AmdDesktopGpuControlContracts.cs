namespace ResourceManager.ReadWrite.Vendors.Amd;

// Values are the original ADLX bridge field numbers, not telemetry fields.
public enum AmdDesktopGpuControlField
{
    PowerLimitOffset = 0, CoreClockMinimum = 1, CoreClockMaximum = 2, CoreVoltage = 3,
    MemoryClockMaximum = 4, FanMinimumRpm = 5, FanTargetRpm = 6, FanZeroRpm = 7,
    CoreClockOffset = 8, CoreVoltageOffset = 9
}
public enum AmdDesktopGpuControlRoute { Rejected, WindowsAmdFixedDiscrete }
public enum AmdDesktopGpuControlObjectKind { Gpu, Fan }
public enum AmdDesktopGpuControlStatus { Applied, Failed, Unsupported }
public enum AmdDesktopGpuControlFailureKind { None, Platform, Component, NotImplemented }
public enum AmdDesktopGpuControlMessage { Applied, RouteRejected, InvalidRangeOrStep, ReadbackMismatch, HardwareFailure }

/// <summary>Explicit admission facts; ExpectedPnp optionally binds an already discovered exact device.</summary>
public sealed record AmdDesktopGpuControlTarget(int? AdapterIndex, AmdDesktopGpuControlRoute Route,
    AmdDesktopGpuControlObjectKind Kind = AmdDesktopGpuControlObjectKind.Gpu, string? ExpectedPnp = null);
/// <summary>Caller-selected original baseline file; construction performs no IO or native operation.</summary>
public sealed record AmdDesktopGpuControlOptions(string BaselinePath);
public sealed record AmdDesktopGpuControlSetting(double? Number = null, bool? Toggle = null);
public sealed record AmdDesktopGpuControlRange(int Minimum, int Maximum, int Step, string Unit);
public sealed record AmdDesktopGpuControlAvailability(bool IsAdmitted, bool CanWrite,
    AmdDesktopGpuControlRange? Range = null, AmdDesktopGpuControlFailureKind FailureKind = AmdDesktopGpuControlFailureKind.None,
    string? Error = null)
{
    public AdlxGfxMode? GfxMode { get; init; }
}
public sealed record AmdDesktopGpuControlValue(string Pnp, double? Number = null, bool? Toggle = null, string? Unit = null)
{
    public AdlxGfxMode? GfxMode { get; init; }
}
public sealed record AmdDesktopGpuControlOutcome(AmdDesktopGpuControlStatus Status, AmdDesktopGpuControlMessage Message,
    string? Error = null);

/// <summary>Pure original standard field IDs and units; no hardware or product policy.</summary>
public static class AmdDesktopGpuControlFormatter
{
    public static AmdDesktopGpuControlField? Field(string id) => id switch
    {
        "gpu.power-limit-offset" => AmdDesktopGpuControlField.PowerLimitOffset,
        "gpu.core-clock-minimum" => AmdDesktopGpuControlField.CoreClockMinimum,
        "gpu.core-clock-maximum" => AmdDesktopGpuControlField.CoreClockMaximum,
        "gpu.core-voltage" => AmdDesktopGpuControlField.CoreVoltage,
        "gpu.memory-clock-maximum" => AmdDesktopGpuControlField.MemoryClockMaximum,
        "fan.minimum-rpm" => AmdDesktopGpuControlField.FanMinimumRpm,
        "fan.target-rpm" => AmdDesktopGpuControlField.FanTargetRpm,
        "fan.zero-rpm" => AmdDesktopGpuControlField.FanZeroRpm,
        "gpu.core-clock-offset" => AmdDesktopGpuControlField.CoreClockOffset,
        "gpu.core-voltage-offset" => AmdDesktopGpuControlField.CoreVoltageOffset, _ => null
    };
    public static string Id(AmdDesktopGpuControlField field) => field switch
    {
        AmdDesktopGpuControlField.PowerLimitOffset => "gpu.power-limit-offset",
        AmdDesktopGpuControlField.CoreClockMinimum => "gpu.core-clock-minimum",
        AmdDesktopGpuControlField.CoreClockMaximum => "gpu.core-clock-maximum",
        AmdDesktopGpuControlField.CoreVoltage => "gpu.core-voltage",
        AmdDesktopGpuControlField.MemoryClockMaximum => "gpu.memory-clock-maximum",
        AmdDesktopGpuControlField.FanMinimumRpm => "fan.minimum-rpm",
        AmdDesktopGpuControlField.FanTargetRpm => "fan.target-rpm",
        AmdDesktopGpuControlField.FanZeroRpm => "fan.zero-rpm",
        AmdDesktopGpuControlField.CoreClockOffset => "gpu.core-clock-offset",
        AmdDesktopGpuControlField.CoreVoltageOffset => "gpu.core-voltage-offset",
        _ => throw new ArgumentOutOfRangeException(nameof(field))
    };
    public static string Describe(AmdDesktopGpuControlOutcome outcome) => outcome.Message switch
    {
        AmdDesktopGpuControlMessage.Applied => "adlx-control-applied-and-verified",
        AmdDesktopGpuControlMessage.RouteRejected => "adlx-control-requires-windows-amd-fixed-discrete-route",
        AmdDesktopGpuControlMessage.InvalidRangeOrStep => "adlx-control-invalid-range-or-step",
        AmdDesktopGpuControlMessage.ReadbackMismatch => "adlx-control-pnp-or-value-readback-mismatch",
        _ => outcome.Error ?? "adlx-control-unavailable"
    };
    public static string Unit(AmdDesktopGpuControlField field) => field switch
    {
        AmdDesktopGpuControlField.PowerLimitOffset => "%",
        AmdDesktopGpuControlField.FanMinimumRpm or AmdDesktopGpuControlField.FanTargetRpm => "RPM",
        AmdDesktopGpuControlField.FanZeroRpm => "",
        AmdDesktopGpuControlField.CoreVoltage or AmdDesktopGpuControlField.CoreVoltageOffset => "mV",
        _ => "MHz"
    };
}
