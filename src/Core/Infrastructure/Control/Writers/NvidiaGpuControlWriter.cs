using ResourceManager.ReadWrite.Windows.Gpu;
using ResourceManager.App.Application.Control;
using ResourceManager.App.Domain.Control;
using ResourceManager.ReadWrite.Vendors.Nvidia;
using ResourceManager.App.Infrastructure.Monitoring;
namespace ResourceManager.App.Infrastructure.Control.Writers;

/// <summary>Product control IDs/policy/copy adapter over the single original SDK NVIDIA resource.</summary>
public sealed class NvidiaGpuControlWriter : IControlWriter, IControlDetectionCache, IDisposable
{
    private readonly WindowsGpuAdapterOrderReader adapterReader = new();
    internal NvidiaGpuControlResource Resource { get; }
    public NvidiaGpuControlWriter(ILogger<NvidiaGpuControlWriter>? logger = null)
        => Resource = new(describe: NvidiaGpuControlCopy.Describe,
            warning: logger is null ? null : (message, arguments) => logger.LogWarning(message, arguments));
    internal NvidiaGpuControlWriter(NvidiaGpuControlResource resource) => Resource = resource;
    internal const string PowerLimitCapabilityId = "gpu.power-limit";
    internal const string CoreClockMinimumCapabilityId = "gpu.core-clock-minimum";
    internal const string CoreClockMaximumCapabilityId = "gpu.core-clock-maximum";
    internal const string MemoryClockMinimumCapabilityId = "gpu.memory-clock-minimum";
    internal const string MemoryClockMaximumCapabilityId = "gpu.memory-clock-maximum";
    internal const string TemperatureLimitCapabilityId = "gpu.temperature-limit";
    internal const string SlowdownTemperatureCapabilityId = "gpu.slowdown-temperature";
    internal const string ShutdownTemperatureCapabilityId = "gpu.shutdown-temperature";
    internal const string CoreClockOffsetCapabilityId = "gpu.core-clock-offset";
    internal const string MemoryClockOffsetCapabilityId = "gpu.memory-clock-offset";
    private static readonly (string Minimum, string Maximum)[] ClockPairs =
    [ (CoreClockMinimumCapabilityId, CoreClockMaximumCapabilityId), (MemoryClockMinimumCapabilityId, MemoryClockMaximumCapabilityId) ];

    public IReadOnlyDictionary<string, string> ValidateSettings(ControlObject target, IReadOnlyList<ControlSetting> settings)
    {
        var errors = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!Target(target).IsDiscreteNvidia) return errors;
        foreach (var pair in ClockPairs)
        {
            var minimum = settings.FirstOrDefault(setting => setting.CapabilityId == pair.Minimum);
            var maximum = settings.FirstOrDefault(setting => setting.CapabilityId == pair.Maximum);
            if (ClockMhz(minimum) is not { } low || ClockMhz(maximum) is not { } high) continue;
            if (high == 0 || low <= high) continue;
            var reason = NvidiaGpuControlCopy.Describe(NvidiaControlMessage.M36, [low, high]);
            errors[pair.Minimum] = reason;
            errors[pair.Maximum] = reason;
        }
        return errors;
    }

    public IReadOnlyList<ControlSetting> OrderSettings(ControlObject target, IReadOnlyList<ControlSetting> settings)
    {
        if (!Target(target).IsDiscreteNvidia) return settings;
        var ordered = settings.ToArray();
        foreach (var pair in ClockPairs)
        {
            var minimum = Array.FindIndex(ordered, setting => setting.CapabilityId == pair.Minimum);
            var maximum = Array.FindIndex(ordered, setting => setting.CapabilityId == pair.Maximum);
            if (minimum < 0 || maximum < 0 || ClockMhz(ordered[minimum]) is not { } low) continue;
            var maximumFirst = Resource.ShouldWriteClockMaximumFirst(Target(target), pair.Minimum, low);
            if (maximumFirst ? maximum > minimum : minimum > maximum)
                (ordered[minimum], ordered[maximum]) = (ordered[maximum], ordered[minimum]);
        }
        return ordered;
    }

    private static double? ClockMhz(ControlSetting? setting)
        => setting?.Number is { } number && double.IsFinite(number) && number >= 0
            ? ControlUnitAdapter.Convert(number, setting.Unit, ControlUnits.Megahertz) : null;
    public void ResetDetection() => Resource.ResetDetection();
    public void Dispose() => Resource.Dispose();
    internal void RecordWriteResult(string objectId, string capabilityId, int code) => Resource.RecordWriteResult(objectId, capabilityId, code);
    internal bool HasUnsupportedWrite(string objectId, string capabilityId) => Resource.HasUnsupportedWrite(objectId, capabilityId);
    internal int WriteClockRange(int index, uint type, bool minimum, uint wanted, uint maximum,
        Func<uint, uint, int> write, out uint low, out uint high)
        => Resource.WriteClockRange(index, type, minimum, wanted, maximum, write, out low, out high);
    public string? ChannelOf(ControlObject target, string capabilityId) => Resource.ChannelOf(Target(target), capabilityId) switch
    { NativeMetricSnapshotSources.NvidiaNvml => ControlChannels.Nvml, NativeMetricSnapshotSources.NvidiaNvapi => ControlChannels.Nvapi, _ => null };
    public ControlWriteAvailability Probe(ControlObject target, ControlCapability capability)
    {
        var value = Resource.Probe(Target(target), Capability(capability));
        return new(value.CanWrite, value.Reason, Range(value.Range), value.UnavailableKind, value.CanRead);
    }
    public async Task<ControlApplyOutcome> WriteAsync(ControlObject target, ControlCapability capability, ControlSetting setting, CancellationToken token)
    {
        var executionTarget = Target(target);
        if (target.AdapterKey is > 0 && capability.Id == PowerLimitCapabilityId)
            executionTarget = executionTarget with { Inventory = adapterReader.ReadInventory() };
        var result = await Resource.WriteAsync(executionTarget, Capability(capability),
            new(setting.CapabilityId, setting.Number, setting.Unit), token);
        return new(result.ObjectId, result.CapabilityId, result.Status, result.Message);
    }
    public async Task<ControlActualValue?> ReadAsync(ControlObject target, ControlCapability capability, CancellationToken token)
    {
        var value = await Resource.ReadAsync(Target(target), Capability(capability), token);
        return value is null ? null : new(value.ObjectId, value.CapabilityId, Number: value.Number, Unit: value.Unit, UnreadableReason: value.UnreadableReason);
    }
    public Task<bool> RestoreAsync(ControlObject target, ControlCapability capability, IReadOnlySet<string> configured,
        CancellationToken token, ControlSetting? previousSetting = null)
    {
        var executionTarget = Target(target);
        if (target.AdapterKey is > 0 && capability.Id == PowerLimitCapabilityId)
            executionTarget = executionTarget with { Inventory = adapterReader.ReadInventory() };
        return Resource.RestoreAsync(executionTarget, Capability(capability), configured, token,
            previousSetting is null ? null : new(previousSetting.CapabilityId, previousSetting.Number, previousSetting.Unit));
    }
    private static NvidiaControlObject Target(ControlObject target) => new(target.Id, target.AdapterIndex, target.AdapterKey,
        target.Kind == ControlObjectKinds.Gpu && target.Platform.Vendor == ControlVendors.Nvidia && target.GpuAttachment == ControlGpuAttachments.Discrete);
    private static NvidiaControlCapability Capability(ControlCapability capability) => new(capability.Id,
        capability.Range is { } value ? new(value.Minimum, value.Maximum, value.Step, value.Unit, value.DefaultValue) : null);
    private static ControlNumberRange? Range(NvidiaControlNumberRange? value)
        => value is null ? null : new(value.Minimum, value.Maximum, value.Step, value.Unit, value.DefaultValue);
}
internal static class NvidiaGpuControlCopy
{
    internal static string Describe(NvidiaControlMessage code, IReadOnlyList<object?> arguments)
    {
        var format = code switch
        {
            NvidiaControlMessage.M0 => "核心下限",
            NvidiaControlMessage.M1 => "核心上限",
            NvidiaControlMessage.M2 => "读不到 NVAPI，需要 NVIDIA 驱动。",
            NvidiaControlMessage.M3 => "认不出这块卡，可能已被切走或停用。",
            NvidiaControlMessage.M4 => "这块卡没开放功耗上限。",
            NvidiaControlMessage.M5 => "驱动拒绝了这次写入。",
            NvidiaControlMessage.M6 => "这一项调不了",
            NvidiaControlMessage.M7 => "驱动已返回这一写入接口不受支持。",
            NvidiaControlMessage.M8 => "驱动报告的功耗范围不可调整。",
            NvidiaControlMessage.M9 => "这一项要一个数值，收到的不是。",
            NvidiaControlMessage.M10 => "显卡拒绝了写入：{Object} / {Capability} = {Value}，NVML 状态码 {Code}",
            NvidiaControlMessage.M11 => "失败",
            NvidiaControlMessage.M12 => "没权限",
            NvidiaControlMessage.M13 => "这张卡不支持",
            NvidiaControlMessage.M14 => "这块卡锁死了",
            NvidiaControlMessage.M15 => "还有一条路只是没接",
            NvidiaControlMessage.M16 => "不支持",
            NvidiaControlMessage.M17 => "这块卡的功耗上限要走 NVAPI 通道（可调 {0:0}%~{1:0}%），写入还没接入。",
            NvidiaControlMessage.M18 => "这块卡把功耗上限锁死了，驱动不接受改动。",
            NvidiaControlMessage.M19 => "我这张卡现在到底跑在多少瓦",
            NvidiaControlMessage.M20 => "能不能改",
            NvidiaControlMessage.M21 => "当前",
            NvidiaControlMessage.M22 => "读到 0",
            NvidiaControlMessage.M23 => "驱动没有报出这一项的当前值。",
            NvidiaControlMessage.M24 => "驱动没有就绪",
            NvidiaControlMessage.M25 => "这块卡不认这个调用",
            NvidiaControlMessage.M26 => "这块卡的功耗上限由整机固件锁定，驱动不接受改动",
            NvidiaControlMessage.M27 => "权限不足，需要管理员",
            NvidiaControlMessage.M28 => "找不到这块卡",
            NvidiaControlMessage.M29 => "NVML 状态码 {0}",
            NvidiaControlMessage.M30 => "不限制",
            NvidiaControlMessage.M31 => "另一头不限制",
            NvidiaControlMessage.M32 => "上限是它、下限不限",
            NvidiaControlMessage.M33 => "这一头不限制",
            NvidiaControlMessage.M34 => "读不到这个时钟域的上限。",
            NvidiaControlMessage.M35 => "锁显卡时钟失败：{Capability} → [{Min}, {Max}] MHz，NVML 返回 {Code}。",
            NvidiaControlMessage.M36 => "驱动不接受 [{0}, {1}] MHz 这个区间。",
            NvidiaControlMessage.M37 => "锁定显卡时钟需要管理员权限。",
            NvidiaControlMessage.M38 => "驱动拒绝了这次时钟锁定（NVML {0}）。",
            NvidiaControlMessage.M39 => "锁着",
            NvidiaControlMessage.M40 => "没锁",
            NvidiaControlMessage.M41 => "只解开一头",
            NvidiaControlMessage.M42 => "两头都不限制",
            NvidiaControlMessage.M43 => "设了没生效",
            NvidiaControlMessage.M44 => "设成功",
            NvidiaControlMessage.M45 => "写显卡温度阈值失败：{Capability} = {Celsius} °C，NVML 返回 {Code}。",
            NvidiaControlMessage.M46 => "下发了，但读不回来核对。",
            NvidiaControlMessage.M47 => "下发了，但驱动把它保持在 {0} °C。",
            NvidiaControlMessage.M48 => "失败",
            NvidiaControlMessage.M49 => "参数越界",
            NvidiaControlMessage.M50 => "你给的数不对",
            NvidiaControlMessage.M51 => "改温度阈值需要管理员权限。",
            NvidiaControlMessage.M52 => "这块卡的驱动不接受改这一档温度阈值（把当前值原样写回去也被拒）。",
            NvidiaControlMessage.M53 => "这块卡的驱动不接受改这一档温度阈值。",
            NvidiaControlMessage.M54 => "没有权限改温度阈值，需要管理员。",
            NvidiaControlMessage.M55 => "这个温度超出驱动允许的范围。",
            NvidiaControlMessage.M56 => "驱动拒绝了这次温度阈值写入（NVML {0}）。",
            NvidiaControlMessage.M57 => "这块卡不报这个时钟域的上限。",
            NvidiaControlMessage.M58 => "不设这一头",
            NvidiaControlMessage.M59 => "零频率",
            NvidiaControlMessage.M60 => "那一头不限制",
            NvidiaControlMessage.M61 => "只设上限",
            NvidiaControlMessage.M62 => "这块卡不报这一档温度阈值。",
            NvidiaControlMessage.M63 => "驱动未提供这一温度阈值的可写范围。",
            _ => code.ToString()
        };
        return arguments.Count == 0 ? format : string.Format(System.Globalization.CultureInfo.CurrentCulture, format, arguments.ToArray());
    }
}


