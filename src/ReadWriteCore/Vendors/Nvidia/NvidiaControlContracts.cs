using ResourceManager.ReadWrite.Windows.Gpu;
namespace ResourceManager.ReadWrite.Vendors.Nvidia;

public sealed record NvidiaControlObject(string Id, int? AdapterIndex, ulong? AdapterKey = null, bool IsDiscreteNvidia = true)
{
    public WindowsGpuAdapterInventoryRead? Inventory { get; init; }
}
public sealed record NvidiaControlCapability(string Id, NvidiaControlNumberRange? Range = null);
public sealed record NvidiaControlNumberRange(double Minimum, double Maximum, double Step, string Unit, double? DefaultValue = null);
public sealed record NvidiaControlSetting(string CapabilityId, double? Number = null, string? Unit = null);
public sealed record NvidiaControlApplyOutcome(string ObjectId, string CapabilityId, string Status, string? Message = null)
{
    /// <summary>Original NVML setter status when that setter was invoked; acceptance is not readback verification.</summary>
    public int? NativeStatus { get; init; }
    public double? Requested { get; init; }
    public double? Observed { get; init; }
    public double? Minimum { get; init; }
    public double? Maximum { get; init; }
    public string? Unit { get; init; }
}
public sealed record NvidiaControlActualValue(string ObjectId, string CapabilityId, double? Number = null, string? Unit = null, string? UnreadableReason = null);
public readonly record struct NvidiaControlWriteAvailability(bool CanWrite, string? Reason = null,
    NvidiaControlNumberRange? Range = null, string? UnavailableKind = null, bool CanRead = false)
{
    public bool IsReadable => CanWrite || CanRead;
    public bool IsMine => CanWrite || Reason is not null;
    public static NvidiaControlWriteAvailability Yes(NvidiaControlNumberRange? range = null) => new(true, null, range);
    public static NvidiaControlWriteAvailability No(string reason, NvidiaControlNumberRange? range = null,
        string kind = NvidiaControlUnavailableKinds.Platform, bool canRead = false) => new(false, reason, range, kind, canRead);
    public static NvidiaControlWriteAvailability NotMine => new(false, null);
}
internal static class NvidiaControlStatuses { internal const string Applied = "applied", Failed = "failed", Unsupported = "unsupported"; }
internal static class NvidiaControlUnavailableKinds { internal const string Platform = "platform", Component = "component"; }
internal static class NvidiaControlUnits { internal const string Watt = "W", Megahertz = "MHz", Celsius = "°C"; }
internal sealed class NvidiaControlLog(Action<string, object?[]> warning)
{ internal void LogWarning(string message, params object?[] arguments) => warning(message, arguments); }
