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
    ILogger<NvidiaGpuControlWriter>? logger = null) : IControlWriter, IControlDetectionCache
{
    internal const string PowerLimitCapabilityId = "gpu.power-limit";

    /// <summary>
    /// 三个温度阈值。**它们是三件不同的事，不是一个滑块的三个位置。**
    ///
    /// 温度墙决定什么时候开始降频；降速阈值是更硬的那一道；
    /// 保护关机是最后一道防线 —— 撞到它本来就是硬件在自救。
    /// 所以三项的档位也不同（见目录），不能合成一项。
    /// </summary>
    /// <summary>
    /// 时钟锁的四项。**它们两两成对**：驱动那边一次调用同时给上下限，
    /// 所以"核心下限"和"核心上限"落到硬件上是同一个操作的两个参数。
    /// 拆成两项是因为用户想的就是两件事（降频省电只设上限、防掉频只设下限），
    /// 配对那一步是这一层的事，不该漏给用户。
    /// </summary>
    internal const string CoreClockMinimumCapabilityId = "gpu.core-clock-minimum";
    internal const string CoreClockMaximumCapabilityId = "gpu.core-clock-maximum";
    internal const string MemoryClockMinimumCapabilityId = "gpu.memory-clock-minimum";
    internal const string MemoryClockMaximumCapabilityId = "gpu.memory-clock-maximum";

    internal const string TemperatureLimitCapabilityId = "gpu.temperature-limit";
    internal const string SlowdownTemperatureCapabilityId = "gpu.slowdown-temperature";
    internal const string ShutdownTemperatureCapabilityId = "gpu.shutdown-temperature";
    internal const string CoreClockOffsetCapabilityId = "gpu.core-clock-offset";
    internal const string MemoryClockOffsetCapabilityId = "gpu.memory-clock-offset";

    private const string DriverMissing = "读不到 NVAPI，需要 NVIDIA 驱动。";
    private const string GpuMissing = "认不出这块卡，可能已被切走或停用。";
    private const string PowerNotExposed = "这块卡没开放功耗上限。";
    private const string WriteRejected = "驱动拒绝了这次写入。";

    private readonly NvidiaNvapiControlBridge bridge = new();
    private readonly NvidiaNvmlControlBridge powerBridge = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(string Object, string Capability), byte> unsupportedWrites = new();
    public void ResetDetection() => unsupportedWrites.Clear();

    internal void RecordWriteResult(string objectId, string capabilityId, int code)
    {
        if (code == 3) unsupportedWrites[(objectId, capabilityId)] = 0;
    }

    internal bool HasUnsupportedWrite(string objectId, string capabilityId)
        => unsupportedWrites.ContainsKey((objectId, capabilityId));

    /// <summary>
    /// 频率偏移走 NVAPI，功耗上限走 NVML —— **同一块卡，两条完全不同的链路。**
    ///
    /// 它们的失败原因也毫无共同点：NVAPI 那条在这台机器上写得进去，
    /// NVML 那条被驱动整个拒掉。摆出链路，用户看"这一项调不了"时
    /// 第一眼就知道是谁说不行。
    /// </summary>
    public string? ChannelOf(ControlObject target, string capabilityId)
        => !IsMine(target, capabilityId) ? null : capabilityId switch
    {
        CoreClockOffsetCapabilityId or MemoryClockOffsetCapabilityId => ControlChannels.Nvapi,
        PowerLimitCapabilityId
            or TemperatureLimitCapabilityId
            or SlowdownTemperatureCapabilityId
            or ShutdownTemperatureCapabilityId
            or CoreClockMinimumCapabilityId
            or CoreClockMaximumCapabilityId
            or MemoryClockMinimumCapabilityId
            or MemoryClockMaximumCapabilityId => ControlChannels.Nvml,
        _ => null
    };

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

        if (HasUnsupportedWrite(target.Id, capability.Id))
        {
            return ControlWriteAvailability.No("驱动已返回这一写入接口不受支持。", canRead: ClockLockOf(capability.Id) is null);
        }

        if (ThresholdOf(capability.Id) is { } thresholdType)
        {
            return ProbeTemperature(adapterIndex, thresholdType);
        }

        if (ClockLockOf(capability.Id) is { } clockLock)
        {
            return ProbeClockLock(adapterIndex, clockLock);
        }

        if (!string.Equals(capability.Id, PowerLimitCapabilityId, StringComparison.Ordinal))
        {
            // 频率偏移的范围是厂商给的软上限，卡本身不报 —— 沿用目录里的形状。
            return bridge.FindHandleByAdapterIndex(adapterIndex) is null
                ? ControlWriteAvailability.No(DriverMissing, kind: ControlUnavailableKinds.Component)
                : ControlWriteAvailability.Yes();
        }

        // 功耗上限：范围由卡自己报，界面上的上下限就是它。
        if (powerBridge.FindHandleByAdapterIndex(adapterIndex) is not { } device)
        {
            return ControlWriteAvailability.No(DriverMissing, kind: ControlUnavailableKinds.Component);
        }
        if (powerBridge.ReadPowerLimit(device) is not { } limits)
        {
            return ControlWriteAvailability.No(PowerNotExposed);
        }
        // 范围由卡自己报：上下限就是驱动里那道硬件锁，不是我们估的。
        var range = new ControlNumberRange(
            Math.Round(limits.MinimumWatts),
            Math.Round(limits.MaximumWatts),
            1,
            ControlUnits.Watt,
            limits.DefaultWatts is { } standard ? Math.Round(standard, 1) : null);

        return range.Maximum > range.Minimum
            ? ControlWriteAvailability.Yes(range)
            : ControlWriteAvailability.No("驱动报告的功耗范围不可调整。", range, canRead: true);
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

        // 温度阈值各走各的一条：一次调用只动一档，三档互不影响。
        if (ThresholdOf(capability.Id) is { } thresholdType)
        {
            return WriteTemperature(target, capability, adapterIndex, thresholdType, value);
        }

        // 时钟锁反过来：两项合成一次调用，所以要先把另一头配上。
        if (ClockLockOf(capability.Id) is { } clockLock)
        {
            return WriteClockLock(target, capability, adapterIndex, clockLock, value);
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
            RecordWriteResult(target.Id, capability.Id, powerLimitCode);
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
    /// NVML 写不了之后，NVAPI 怎么说。说不出话就返回 null，让 NVML 那句话留着。
    ///
    /// **"这块卡锁死了"和"还有一条路只是没接"要分开说。** 前者用户只能死心，
    /// 后者是我们欠的功能 —— 混成一句"不支持"，等于把自己没做的事说成硬件不行。
    /// </summary>
    private string? NvapiPowerReason(int adapterIndex)
    {
        if (bridge.FindHandleByAdapterIndex(adapterIndex) is not { } gpu)
        {
            return null;
        }
        if (bridge.ReadPowerLimitRange(gpu) is not { } nvapi)
        {
            return null;
        }
        return nvapi.IsAdjustable
            ? $"这块卡的功耗上限要走 NVAPI 通道（可调 {nvapi.MinimumPercent:0}%~{nvapi.MaximumPercent:0}%），写入还没接入。"
            : "这块卡把功耗上限锁死了，驱动不接受改动。";
    }

    /// <summary>
    /// 这一项现在实际是多少。
    ///
    /// 频率偏移从 P-State 表里读回来 —— 用户可能用别的工具改过，
    /// 界面上要显示的是卡里真实的那个值，不是我们以为写进去的那个。
    /// </summary>
    public Task<ControlActualValue?> ReadAsync(
        ControlObject target,
        ControlCapability capability,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(capability);
        cancellationToken.ThrowIfCancellationRequested();

        /*
         * 功耗上限现在是多少。
         *
         * 改不了也要读：**当前生效的瓦数是这块卡最重要的一个事实**，
         * 用户想知道"我这张卡现在到底跑在多少瓦"，而那和"能不能改"是两件事。
         */
        if (string.Equals(capability.Id, PowerLimitCapabilityId, StringComparison.Ordinal))
        {
            if (target.AdapterIndex is not { } powerIndex
                || powerBridge.FindHandleByAdapterIndex(powerIndex) is not { } powerDevice
                || powerBridge.ReadPowerLimit(powerDevice) is not { CurrentWatts: { } watts })
            {
                return Task.FromResult<ControlActualValue?>(null);
            }
            return Task.FromResult<ControlActualValue?>(new ControlActualValue(
                target.Id,
                capability.Id,
                Math.Round(watts, 1),
                Unit: ControlUnits.Watt));
        }

        /*
         * 温度阈值现在是多少。**这一条纯读**，而且读得到就是真值 ——
         * 用户在界面上看到的"当前"就该是卡自己报的这个数，
         * 不是我们上次写下去的那个（两者可能不一样，那正是他要知道的）。
         */
        if (ThresholdOf(capability.Id) is { } thresholdType)
        {
            if (target.AdapterIndex is not { } index
                || powerBridge.FindHandleByAdapterIndex(index) is not { } device
                || powerBridge.ReadTemperatureThreshold(device, thresholdType) is not { } celsius)
            {
                return Task.FromResult<ControlActualValue?>(null);
            }
            return Task.FromResult<ControlActualValue?>(new ControlActualValue(
                target.Id,
                capability.Id,
                celsius,
                Unit: ControlUnits.Celsius));
        }

        var domain = capability.Id switch
        {
            CoreClockOffsetCapabilityId => NvidiaNvapiControlBridge.GraphicsClockDomain,
            MemoryClockOffsetCapabilityId => NvidiaNvapiControlBridge.MemoryClockDomain,
            _ => (uint?)null
        };
        if (domain is not { } clockDomain
            || target.AdapterIndex is not { } adapterIndex
            || bridge.FindHandleByAdapterIndex(adapterIndex) is not { } handle)
        {
            return Task.FromResult<ControlActualValue?>(null);
        }

        return Task.FromResult<ControlActualValue?>(
            bridge.ReadClockOffsetMhz(handle, clockDomain) is { } offset
                ? new ControlActualValue(
                    target.Id,
                    capability.Id,
                    Number: offset,
                    Unit: ControlUnits.Megahertz)
                // 读不到和"读到 0"是两回事，必须分得开。
                : new ControlActualValue(
                    target.Id,
                    capability.Id,
                    UnreadableReason: "驱动没有报出这一项的当前值。"));
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
        // 本机实测：NVML 新旧两条写入路径在管理员下都报这个，
        // 而 NVAPI 的功耗策略表这张卡干脆是空的（条目数 0）。
        // 两条通道都问过了，所以话可以说死。
        3 => "这块卡的功耗上限由整机固件锁定，驱动不接受改动",
        4 => "权限不足，需要管理员",
        6 => "找不到这块卡",
        _ => $"NVML 状态码 {code}"
    };

    /// <summary>
    /// 锁一个时钟域的上限或下限。
    ///
    /// **驱动那边一次调用要两个数，用户是分两项设的**，所以这里记住配对：
    /// 设上限时沿用已记住的下限，设下限时沿用已记住的上限；
    /// 没设过的那一头就是"不限制"（下限 0 / 上限取卡报的最大值）。
    ///
    /// 两项都要设的时候，先落地的那一项会短暂地配上"另一头不限制"的区间 ——
    /// 那不是错的状态，只是还没设完。它和用户要的方向一致
    /// （只设了上限的那一刻，行为就是"上限是它、下限不限"），
    /// 所以不必为了避免这个瞬间去搞什么延迟提交。
    ///
    /// **0 是"这一头不限制"，不是零频率。** 驱动接受 0，实测过。
    /// </summary>
    private ControlApplyOutcome WriteClockLock(
        ControlObject target,
        ControlCapability capability,
        int adapterIndex,
        (uint ClockType, bool IsMinimum) clockLock,
        double mhz)
    {
        if (powerBridge.FindHandleByAdapterIndex(adapterIndex) is not { } device)
        {
            return Failed(target, capability, DriverMissing);
        }
        if (powerBridge.ReadMaximumClockMhz(device, clockLock.ClockType) is not { } deviceMaximum)
        {
            return Failed(target, capability, "读不到这个时钟域的上限。");
        }

        var wanted = (uint)Math.Clamp(Math.Round(mhz), 0, deviceMaximum);
        uint minimum;
        uint maximum;
        var code = WriteClockRange(adapterIndex, clockLock.ClockType, clockLock.IsMinimum, wanted,
            (uint)Math.Round(deviceMaximum),
            (min, max) => powerBridge.WriteLockedClocks(device, clockLock.ClockType, min, max),
            out minimum, out maximum);
        if (code != 0)
        {
            RecordWriteResult(target.Id, capability.Id, code);
            logger?.LogWarning(
                "锁显卡时钟失败：{Capability} → [{Min}, {Max}] MHz，NVML 返回 {Code}。",
                capability.Id,
                minimum,
                maximum,
                code);
            return Failed(target, capability, code switch
            {
                2 => $"驱动不接受 [{minimum}, {maximum}] MHz 这个区间。",
                4 => "锁定显卡时钟需要管理员权限。",
                _ => $"驱动拒绝了这次时钟锁定（NVML {code}）。"
            });
        }
        return new ControlApplyOutcome(target.Id, capability.Id, ControlApplyStatuses.Applied, null);
    }

    internal int WriteClockRange(int adapterIndex, uint clockType, bool isMinimum, uint wanted,
        uint deviceMaximum, Func<uint, uint, int> write, out uint minimum, out uint maximum)
    {
        lock (clockGate)
        {
            var key = (adapterIndex, clockType);
            var pair = clockRange.GetValueOrDefault(key, (0u, deviceMaximum));
            minimum = isMinimum ? wanted : pair.Item1;
            maximum = isMinimum ? pair.Item2 : wanted;
            if (minimum > maximum) return 2;
            var code = write(minimum, maximum);
            if (code == 0) clockRange[key] = (minimum, maximum);
            return code;
        }
    }

    /// <summary>
    /// 交还默认。**时钟锁必须真做点什么**：锁上之后不解开就一直锁着，
    /// 用户取消勾选却发现显卡还被按在那儿，是这一页最糟的一种失败。
    ///
    /// 上下限任一项被取消，整个域就解锁 —— 驱动那边本来就只有"锁着"和"没锁"
    /// 两个状态，没有"只解开一头"这回事。
    ///
    /// 其余的项什么都不用做：频率偏移写 0 就等于没偏移，本来就是无状态的。
    /// </summary>
    public Task<bool> RestoreAsync(
        ControlObject target,
        ControlCapability capability,
        IReadOnlySet<string> stillConfigured,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(capability);
        ArgumentNullException.ThrowIfNull(stillConfigured);
        cancellationToken.ThrowIfCancellationRequested();

        if (ClockLockOf(capability.Id) is not { } clockLock)
        {
            return ControlWriterRestoration.RestoreDefaultAsync(this, target, capability, cancellationToken);
        }
        if (target.AdapterIndex is not { } adapterIndex
            || powerBridge.FindHandleByAdapterIndex(adapterIndex) is not { } device)
        {
            return Task.FromResult(false);
        }

        /*
         * **同伴还在就不解锁。**
         *
         * 上限和下限落到驱动上是同一次调用，只设了上限的时候，下限那一项
         * 会走到这里 —— 这时候解锁等于把用户刚设好的上限当场抹掉。
         * 这个 bug 实测撞到过：回执报 applied，频率却一点没变。
         */
        if (stillConfigured.Contains(SiblingOf(capability.Id)))
        {
            lock (clockGate)
            {
                if (clockRange.TryGetValue((adapterIndex, clockLock.ClockType), out var previous))
                {
                    var maximum = powerBridge.ReadMaximumClockMhz(device, clockLock.ClockType);
                    if (!clockLock.IsMinimum && maximum is null)
                        return Task.FromResult(false);
                    clockRange[(adapterIndex, clockLock.ClockType)] = clockLock.IsMinimum
                        ? (0, previous.Max)
                        : (previous.Min, (uint)Math.Round(maximum!.Value));
                }
            }
            return Task.FromResult(true);
        }

        if (powerBridge.ResetLockedClocks(device, clockLock.ClockType) != 0)
            return Task.FromResult(false);
        lock (clockGate)
        {
            // 忘掉记住的那个配对：下次再设就是从"两头都不限制"重新开始。
            clockRange.Remove((adapterIndex, clockLock.ClockType));
        }
        return Task.FromResult(true);
    }

    /// <summary>
    /// 写一个温度阈值，然后**回读核对**。
    ///
    /// 回读是必须的：驱动对这类调用未必报错，而"设了没生效"和"设成功"
    /// 对用户是两件完全不同的事。核对不上就如实说没写进去，
    /// 并把读回来的那个值一并说出来 —— 用户至少知道现在到底是多少。
    /// </summary>
    private ControlApplyOutcome WriteTemperature(
        ControlObject target,
        ControlCapability capability,
        int adapterIndex,
        uint thresholdType,
        double celsius)
    {
        if (powerBridge.FindHandleByAdapterIndex(adapterIndex) is not { } device)
        {
            return Failed(target, capability, DriverMissing);
        }

        var wanted = (int)Math.Round(celsius);
        var code = powerBridge.WriteTemperatureThreshold(device, thresholdType, wanted);
        if (code != 0)
        {
            logger?.LogWarning(
                "写显卡温度阈值失败：{Capability} = {Celsius} °C，NVML 返回 {Code}。",
                capability.Id,
                wanted,
                code);
            return Failed(target, capability, TemperatureWriteRefused(code));
        }

        if (powerBridge.ReadTemperatureThreshold(device, thresholdType) is not { } readBack)
        {
            return Failed(target, capability, "下发了，但读不回来核对。");
        }
        if (Math.Abs(readBack - wanted) > 0.5)
        {
            return Failed(
                target,
                capability,
                $"下发了，但驱动把它保持在 {Math.Round(readBack)} °C。");
        }
        return new ControlApplyOutcome(
            target.Id,
            capability.Id,
            ControlApplyStatuses.Applied,
            null);
    }

    /// <summary>
    /// 驱动拒绝时那句话。**照它给的原因分，不要笼统说"失败"。**
    ///
    /// 这几个码的含义差别很大：不支持是这块卡做不到，没权限是我们起的方式不对，
    /// 而"参数越界"是用户那个数字的问题 —— 三种对应的下一步完全不同。
    /// </summary>
    /// <summary>
    /// 空操作试写被拒时那句话。
    ///
    /// 和 <see cref="TemperatureWriteRefused"/> 分开，因为**试的是当前值** ——
    /// 任何"你给的数不对"的说法在这里都是假的。
    /// </summary>
    private static string TemperatureProbeRefused(int code) => code switch
    {
        4 => "改温度阈值需要管理员权限。",
        _ => "这块卡的驱动不接受改这一档温度阈值（把当前值原样写回去也被拒）。"
    };

    private static string TemperatureWriteRefused(int code) => code switch
    {
        3 => "这块卡的驱动不接受改这一档温度阈值。",
        4 => "没有权限改温度阈值，需要管理员。",
        2 => "这个温度超出驱动允许的范围。",
        _ => $"驱动拒绝了这次温度阈值写入（NVML {code}）。"
    };

    /// <summary>
    /// 同一个时钟域的**另一头**。不是时钟锁就返回它自己（那样永远匹配不上）。
    /// </summary>
    private static string SiblingOf(string capabilityId) => capabilityId switch
    {
        CoreClockMinimumCapabilityId => CoreClockMaximumCapabilityId,
        CoreClockMaximumCapabilityId => CoreClockMinimumCapabilityId,
        MemoryClockMinimumCapabilityId => MemoryClockMaximumCapabilityId,
        MemoryClockMaximumCapabilityId => MemoryClockMinimumCapabilityId,
        _ => capabilityId
    };

    /// <summary>
    /// 这一项是哪个时钟域的哪一头。不是时钟锁就是 null。
    /// </summary>
    private static (uint ClockType, bool IsMinimum)? ClockLockOf(string capabilityId)
        => capabilityId switch
        {
            CoreClockMinimumCapabilityId =>
                (NvidiaNvmlControlBridge.ClockTypes.Graphics, true),
            CoreClockMaximumCapabilityId =>
                (NvidiaNvmlControlBridge.ClockTypes.Graphics, false),
            MemoryClockMinimumCapabilityId =>
                (NvidiaNvmlControlBridge.ClockTypes.Memory, true),
            MemoryClockMaximumCapabilityId =>
                (NvidiaNvmlControlBridge.ClockTypes.Memory, false),
            _ => null
        };

    /// <summary>Read the clock domain range; only explicit apply/release may change a lock.</summary>
    private ControlWriteAvailability ProbeClockLock(
        int adapterIndex,
        (uint ClockType, bool IsMinimum) clockLock)
    {
        if (powerBridge.FindHandleByAdapterIndex(adapterIndex) is not { } device)
        {
            return ControlWriteAvailability.No(DriverMissing, kind: ControlUnavailableKinds.Component);
        }
        if (powerBridge.ReadMaximumClockMhz(device, clockLock.ClockType) is not { } maximum)
        {
            return ControlWriteAvailability.No(
                "这块卡不报这个时钟域的上限。",
                kind: ControlUnavailableKinds.Platform);
        }

        /*
         * 0 表示"不设这一头"，所以它是合法的最小值，不是"零频率"。
         * 步长给 15 MHz：驱动会把值对齐到它自己的档位上，给 1 MHz 的步长
         * 只会让用户拖出一堆最后被对齐掉的数字。
         */
        return ControlWriteAvailability.Yes(new ControlNumberRange(
            0,
            Math.Round(maximum),
            15,
            ControlUnits.Megahertz,
            0));
    }

    /// <summary>
    /// 每个时钟域**现在想锁成什么区间**。
    ///
    /// 驱动那边一次调用要上下限两个数，而用户是分两项设的 ——
    /// 所以这里记住配对。没设过的那一头取 0（下限）或卡报的最大（上限），
    /// 也就是**"那一头不限制"**，正是"只设上限"该有的意思。
    /// </summary>
    private readonly Dictionary<(int Adapter, uint ClockType), (uint Min, uint Max)> clockRange = [];
    private readonly object clockGate = new();

    /// <summary>这一项对应 NVML 的哪一种温度阈值。不是温度项就是 null。</summary>
    private static uint? ThresholdOf(string capabilityId) => capabilityId switch
    {
        TemperatureLimitCapabilityId => NvidiaNvmlControlBridge.TemperatureThresholds.GpuMaximum,
        SlowdownTemperatureCapabilityId => NvidiaNvmlControlBridge.TemperatureThresholds.Slowdown,
        ShutdownTemperatureCapabilityId => NvidiaNvmlControlBridge.TemperatureThresholds.Shutdown,
        _ => null
    };

    /// <summary>Read temperature thresholds without modifying hardware.</summary>
    private ControlWriteAvailability ProbeTemperature(int adapterIndex, uint thresholdType)
    {
        if (powerBridge.FindHandleByAdapterIndex(adapterIndex) is not { } device)
        {
            return ControlWriteAvailability.No(DriverMissing, kind: ControlUnavailableKinds.Component);
        }
        if (powerBridge.ReadTemperatureThreshold(device, thresholdType) is not { } current)
        {
            return ControlWriteAvailability.No(
                "这块卡不报这一档温度阈值。",
                kind: ControlUnavailableKinds.Platform);
        }

        // NVML exposes these readings, but not a writable range. Do not probe by writing.
        return ControlWriteAvailability.No(
            "驱动未提供这一温度阈值的可写范围。",
            kind: ControlUnavailableKinds.Platform,
            canRead: true);
    }

    private static bool IsMine(ControlObject target, string capabilityId)
        => string.Equals(target.Kind, ControlObjectKinds.Gpu, StringComparison.Ordinal)
            && string.Equals(target.Platform.Vendor, ControlVendors.Nvidia, StringComparison.Ordinal)
            && string.Equals(
                target.GpuAttachment,
                ControlGpuAttachments.Discrete,
                StringComparison.Ordinal)
            && capabilityId is PowerLimitCapabilityId
                or CoreClockOffsetCapabilityId
                or MemoryClockOffsetCapabilityId
                or TemperatureLimitCapabilityId
                or SlowdownTemperatureCapabilityId
                or ShutdownTemperatureCapabilityId
                or CoreClockMinimumCapabilityId
                or CoreClockMaximumCapabilityId
                or MemoryClockMinimumCapabilityId
                or MemoryClockMaximumCapabilityId;

    private static ControlApplyOutcome Failed(
        ControlObject target,
        ControlCapability capability,
        string reason)
        => new(target.Id, capability.Id, ControlApplyStatuses.Failed, reason);
}
