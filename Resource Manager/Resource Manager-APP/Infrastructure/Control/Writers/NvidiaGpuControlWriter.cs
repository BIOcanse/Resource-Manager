using ResourceManager.App.Application.Control;
using ResourceManager.App.Domain.Control;

namespace ResourceManager.App.Infrastructure.Control.Writers;

/// <summary>
/// N 卡的写入器：功耗上限走 NVML，频率偏移走 NVAPI。
///
/// 只认独显。核显不归它管 —— 那是 CPU 封装里的事，走另一条路。
///
/// **一项能力走哪条路，看厂商把它开在哪条路上，不是随便挑一条。**
/// 笔记本显卡的 NVAPI 功耗策略表往往是空的（驱动返回成功、内容全零），
/// 而同一块卡在 NVML 上报得出完整的「当前 / 默认 / 最小 / 最大」四个值；
/// 频率偏移反过来只有 NVAPI 的 P-State 表能写。所以两条路各管各的，
/// 也各自把真实范围交给界面 —— 界面上的上下限就是硬件说的那个，不是估出来的。
/// </summary>
public sealed class NvidiaGpuControlWriter(
    ILogger<NvidiaGpuControlWriter>? logger = null) : IControlWriter
{
    internal const string PowerLimitCapabilityId = "gpu.power-limit";
    internal const string CoreClockOffsetCapabilityId = "gpu.core-clock-offset";
    internal const string MemoryClockOffsetCapabilityId = "gpu.memory-clock-offset";

    private const string DriverMissing = "读不到 NVAPI，装上 NVIDIA 驱动之后才能调。";
    private const string GpuMissing = "这块卡现在认不出来，可能已经被切走或者停用了。";
    private const string PowerNotExposed = "这块卡没有把功耗上限开放出来。";
    private const string WriteRejected = "驱动拒绝了这次写入。";

    private readonly NvidiaNvapiControlBridge bridge = new();
    private readonly NvidiaNvmlControlBridge powerBridge = new();
    private readonly object gate = new();
    private bool powerWritableProbed;
    private string? powerNotWritableReason;

    public ControlWriteAvailability Probe(ControlObject target, ControlCapability capability)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(capability);

        if (!IsMine(target, capability.Id))
        {
            return ControlWriteAvailability.NotMine;
        }

        if (target.AdapterIndex is not { } adapterIndex)
        {
            return ControlWriteAvailability.No(GpuMissing);
        }

        if (!string.Equals(capability.Id, PowerLimitCapabilityId, StringComparison.Ordinal))
        {
            // 频率偏移的范围是厂商给的软上限，卡本身不报 —— 沿用目录里的形状。
            return bridge.FindHandleByAdapterIndex(adapterIndex) is null
                ? ControlWriteAvailability.No(DriverMissing)
                : ControlWriteAvailability.Yes();
        }

        // 功耗上限：范围由卡自己报，界面上的上下限就是它。
        if (powerBridge.FindHandleByAdapterIndex(adapterIndex) is not { } device)
        {
            return ControlWriteAvailability.No(DriverMissing);
        }
        if (powerBridge.ReadPowerLimit(device) is not { } limits)
        {
            return ControlWriteAvailability.No(PowerNotExposed);
        }
        // 读得到不等于写得进去。很多笔记本显卡读得出范围却拒绝写入，
        // 而探测说能调、写下去报失败，是这一层最不该出现的事。
        if (ProbeWritable(device, limits) is { } refused)
        {
            return ControlWriteAvailability.No(refused);
        }
        return ControlWriteAvailability.Yes(new ControlNumberRange(
            Math.Round(limits.MinimumWatts),
            Math.Round(limits.MaximumWatts),
            1,
            "W",
            limits.DefaultWatts is { } standard ? Math.Round(standard) : null));
    }

    public Task<ControlApplyOutcome> WriteAsync(
        ControlObject target,
        ControlCapability capability,
        ControlSetting setting,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(capability);
        ArgumentNullException.ThrowIfNull(setting);
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(Write(target, capability, setting));
    }

    private ControlApplyOutcome Write(
        ControlObject target,
        ControlCapability capability,
        ControlSetting setting)
    {
        if (target.AdapterIndex is not { } adapterIndex)
        {
            return Failed(target, capability, GpuMissing);
        }
        var handle = bridge.FindHandleByAdapterIndex(adapterIndex) ?? IntPtr.Zero;
        if (setting.Number is not { } value || !double.IsFinite(value))
        {
            return Failed(target, capability, "这一项要一个数值，收到的不是。");
        }

        // 超出范围就夹回来再写：范围是硬件给的，用户那边可能存的是上一块卡的设定。
        var bounds = capability.Range;
        if (bounds is not null)
        {
            value = Math.Clamp(value, bounds.Minimum, bounds.Maximum);
        }

        var powerLimitCode = 0;
        var written = capability.Id switch
        {
            PowerLimitCapabilityId => WritePowerLimit(adapterIndex, value, out powerLimitCode),
            CoreClockOffsetCapabilityId => bridge.TryWriteClockOffsetMhz(
                handle,
                NvidiaNvapiControlBridge.GraphicsClockDomain,
                value),
            MemoryClockOffsetCapabilityId => bridge.TryWriteClockOffsetMhz(
                handle,
                NvidiaNvapiControlBridge.MemoryClockDomain,
                value),
            _ => false
        };

        if (!written)
        {
            logger?.LogWarning(
                "显卡拒绝了写入：{Object} / {Capability} = {Value}，NVML 状态码 {Code}",
                target.Id,
                capability.Id,
                value,
                powerLimitCode);
            // 驱动给了状态码就带上：用户看到的不该只是"失败"，
            // 而是"没权限"还是"这张卡不支持"这种能据以行动的区别。
            return Failed(
                target,
                capability,
                powerLimitCode == 0
                    ? WriteRejected
                    : $"{WriteRejected}（{DescribeNvmlCode(powerLimitCode)}）");
        }

        return new ControlApplyOutcome(
            target.Id,
            capability.Id,
            ControlApplyStatuses.Applied,
            null);
    }

    /// <summary>
    /// 这块卡到底让不让改功耗上限。写不了就返回原因，写得了返回 null。
    ///
    /// 用「把当前值原样写回去」来问 —— 这是一次空操作，硬件状态不变，
    /// 但驱动会如实告诉我们它接不接受。只在**读得到当前值**时才问：
    /// 读不到就没有"原样"可写，那时宁可说验不了，也不拿一个猜来的值去写用户的卡。
    /// 一个进程只问一次，结果记下来 —— 界面每刷新一次就写一次硬件是不能接受的。
    /// </summary>
    private string? ProbeWritable(IntPtr device, NvidiaPowerLimitWatts limits)
    {
        lock (gate)
        {
            if (powerWritableProbed)
            {
                return powerNotWritableReason;
            }

            powerWritableProbed = true;
            if (limits.CurrentWatts is not { } current)
            {
                powerNotWritableReason = "读不到这块卡当前的功耗上限，没法确认能不能改。";
                return powerNotWritableReason;
            }

            var code = powerBridge.WritePowerLimitWatts(device, current);
            powerNotWritableReason = code == 0
                ? null
                : $"这块卡不接受改功耗上限（{DescribeNvmlCode(code)}）。";
            return powerNotWritableReason;
        }
    }

    private bool WritePowerLimit(int adapterIndex, double watts, out int code)
    {
        if (powerBridge.FindHandleByAdapterIndex(adapterIndex) is not { } device)
        {
            code = 0;
            return false;
        }
        code = powerBridge.WritePowerLimitWatts(device, watts);
        return code == 0;
    }

    /// <summary>把 NVML 的状态码翻成用户能据以行动的一句话。不认识的原样报出编号。</summary>
    private static string DescribeNvmlCode(int code) => code switch
    {
        1 => "驱动没有就绪",
        2 => "这块卡不认这个调用",
        3 => "这块卡不支持改功耗上限",
        4 => "权限不足，需要管理员",
        6 => "找不到这块卡",
        _ => $"NVML 状态码 {code}"
    };

    private static bool IsMine(ControlObject target, string capabilityId)
        => string.Equals(target.Kind, ControlObjectKinds.Gpu, StringComparison.Ordinal)
            && string.Equals(target.Platform.Vendor, ControlVendors.Nvidia, StringComparison.Ordinal)
            && string.Equals(
                target.GpuAttachment,
                ControlGpuAttachments.Discrete,
                StringComparison.Ordinal)
            && capabilityId is PowerLimitCapabilityId
                or CoreClockOffsetCapabilityId
                or MemoryClockOffsetCapabilityId;

    private static ControlApplyOutcome Failed(
        ControlObject target,
        ControlCapability capability,
        string reason)
        => new(target.Id, capability.Id, ControlApplyStatuses.Failed, reason);
}
