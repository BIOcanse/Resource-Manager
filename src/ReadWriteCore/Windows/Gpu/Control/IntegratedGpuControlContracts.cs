namespace ResourceManager.ReadWrite.Windows.Gpu.Control;
public enum IntegratedGpuControlRoute { Rejected, AmdIntegrated, IntelIntegrated }
public enum IntegratedGpuControlField { CurveOptimizer, CoreClockOffset }
public enum IntegratedGpuControlStatus { Applied, Failed }
public enum IntegratedGpuControlUnavailableKind { Platform, Component, Prerequisite }
public enum IntegratedGpuControlMessage { BridgeMissing, BridgeSilent, NotSupported, NotEffective, IntelDriverMissing, IntelTargetMissing, OverclockNotAccepted, BaselineUnavailable, NumberRequired, SmuRejected, ReadingMissing, WaiverRejected, DriverRejected, BaselineWarning, SmuRejectedWarning, IntelRejectedWarning }
public sealed record IntegratedGpuControlTarget(string Id, IntegratedGpuControlRoute Route, ulong? AdapterKey = null);
public sealed record IntegratedGpuControlRange(double Minimum,double Maximum,double Step,string Unit,double? DefaultValue=null);
public sealed record IntegratedGpuControlCapability(IntegratedGpuControlField Field,IntegratedGpuControlRange? Range=null)
{ public string Id => IntegratedGpuControlFormatter.Id(Field); }
public sealed record IntegratedGpuControlSetting(IntegratedGpuControlField Field,double? Number=null,string? Unit=null)
{ public string CapabilityId => IntegratedGpuControlFormatter.Id(Field); }
public sealed record IntegratedGpuControlValue(string ObjectId,string CapabilityId,double? Number=null,string? Unit=null,string? UnreadableReason=null);
public sealed record IntegratedGpuControlOutcome(string ObjectId,string CapabilityId,IntegratedGpuControlStatus Status,string? Message)
{
    /// <summary>True only when the original setter response/read actually compared the configured setting.
    /// Missing AMD optional readback remains accepted success, never verified.</summary>
    public bool ReadbackVerified { get; init; }
}
/// <summary>The caller owns consent recording; this resource only reads acceptance and never defaults it to true.</summary>
public sealed record IntegratedGpuControlOptions(string AmdBaselinePath,Func<CancellationToken,Task<bool>> IsOverclockAccepted);
public readonly record struct IntegratedGpuControlAvailability(bool CanWrite,string? Reason=null,IntegratedGpuControlRange? Range=null,
    IntegratedGpuControlUnavailableKind? UnavailableKind=null,bool CanRead=false)
{
    public bool IsMine => CanWrite || Reason is not null;
    public static IntegratedGpuControlAvailability Yes(IntegratedGpuControlRange? range=null) => new(true,Range:range);
    public static IntegratedGpuControlAvailability No(string reason,IntegratedGpuControlRange? range=null,
        IntegratedGpuControlUnavailableKind kind=IntegratedGpuControlUnavailableKind.Platform,bool canRead=false)
        => new(false,reason,range,kind,canRead);
    public static IntegratedGpuControlAvailability NotMine => new(false);
}
public static class IntegratedGpuControlFormatter
{
    public static string Id(IntegratedGpuControlField field) => field switch
    { IntegratedGpuControlField.CurveOptimizer => "gpu.curve-optimizer", IntegratedGpuControlField.CoreClockOffset => "gpu.core-clock-offset", _ => "gpu.unsupported" };
    public static IntegratedGpuControlField? Field(string id) => id switch
    { "gpu.curve-optimizer" => IntegratedGpuControlField.CurveOptimizer, "gpu.core-clock-offset" => IntegratedGpuControlField.CoreClockOffset, _ => null };
}
