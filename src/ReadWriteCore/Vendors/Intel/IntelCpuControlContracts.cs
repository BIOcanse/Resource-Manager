namespace ResourceManager.ReadWrite.Vendors.Intel;

/// <summary>Caller-selected signed module, runtime and original durable restoration store. No deployment or probing.</summary>
public sealed record IntelCpuControlOptions(string ModulePath, string LibraryPath, string BaselinePath);
public sealed record IntelCpuControlTarget(string Id, bool IsWindowsIntelCpu = true);
public sealed record IntelCpuControlCapability(string Id, IntelCpuControlNumberRange? Range = null);
public sealed record IntelCpuControlNumberRange(double Minimum, double Maximum, double Step, string Unit, double? DefaultValue = null);
public sealed record IntelCpuControlSetting(string CapabilityId, double? Number = null, string? Unit = null);
public sealed record IntelCpuControlOutcome(string ObjectId, string CapabilityId, string Status, string? Message = null)
{
    public double? Requested { get; init; }
    public double? Observed { get; init; }
    public string? Unit { get; init; }
}
public sealed record IntelCpuControlActualValue(string ObjectId, string CapabilityId, double? Number = null, string? Unit = null);
public readonly record struct IntelCpuControlAvailability(bool CanWrite, string? Reason = null,
    IntelCpuControlNumberRange? Range = null, string? UnavailableKind = null, bool CanRead = false)
{
    public bool IsReadable => CanWrite || CanRead;
    public static IntelCpuControlAvailability Yes(IntelCpuControlNumberRange? range = null) => new(true, null, range);
    public static IntelCpuControlAvailability No(string reason, IntelCpuControlNumberRange? range = null,
        string kind = "platform", bool canRead = false) => new(false, reason, range, kind, canRead);
    public static IntelCpuControlAvailability NotMine => new(false, null);
}

public enum IntelCpuControlMessage
{
    MmioWriteNotImplemented, RegisterWriteNotImplemented, MailboxModuleUnavailable,
    FirmwarePowerLimitLocked, PowerLimitModuleUnavailable, NumberRequired, OutOfRange,
    VoltageVerified, VoltageReadbackMismatch, FirmwarePowerRegisterLocked,
    PowerReadbackMismatch, PowerVerified, BaselineUnavailable
}

/// <summary>Neutral raw resource reasons; the Host supplies its original copy formatter.</summary>
public static class IntelCpuControlFormatter
{
    public static string Describe(IntelCpuControlMessage message, IReadOnlyList<object?> arguments)
    {
        var reason = message switch
        {
            IntelCpuControlMessage.MmioWriteNotImplemented => "intel-mmio-write-not-implemented",
            IntelCpuControlMessage.RegisterWriteNotImplemented => "intel-register-write-not-implemented",
            IntelCpuControlMessage.MailboxModuleUnavailable => "intel-oc-mailbox-module-unavailable",
            IntelCpuControlMessage.FirmwarePowerLimitLocked => "intel-firmware-power-limit-locked",
            IntelCpuControlMessage.PowerLimitModuleUnavailable => "intel-power-limit-module-unavailable",
            IntelCpuControlMessage.NumberRequired => "intel-control-number-required",
            IntelCpuControlMessage.OutOfRange => "intel-control-out-of-range",
            IntelCpuControlMessage.VoltageVerified => "intel-voltage-write-readback-verified",
            IntelCpuControlMessage.VoltageReadbackMismatch => "intel-voltage-readback-mismatch",
            IntelCpuControlMessage.FirmwarePowerRegisterLocked => "intel-firmware-power-register-locked",
            IntelCpuControlMessage.PowerReadbackMismatch => "intel-power-readback-mismatch",
            IntelCpuControlMessage.PowerVerified => "intel-power-write-readback-verified",
            IntelCpuControlMessage.BaselineUnavailable => "intel-restoration-baseline-unavailable",
            _ => throw new ArgumentOutOfRangeException(nameof(message))
        };
        return arguments.Count == 0 ? reason : reason + ": " + string.Join(", ", arguments);
    }
}
