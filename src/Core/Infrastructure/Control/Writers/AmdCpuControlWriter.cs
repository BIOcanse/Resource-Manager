using System.Globalization;
using System.Text.Json;
using ResourceManager.App.Application.Control;
using ResourceManager.App.Domain.Control;

namespace ResourceManager.App.Infrastructure.Control.Writers;

/// <summary>
/// AMD 处理器的功耗上限和电压（Curve Optimizer），经由硬件写入辅助进程。
///
/// **不自己拼 SMU 邮箱、不自己编码 Curve Optimizer 的负值。** 那些都在辅助进程里
/// 由 ZenStates-Core 负责 —— 各代处理器的邮箱地址、命令号、参数编码由上游维护，
/// 内核也由它按机器情况选（开着内存完整性的机器上走 PawnIO）。
/// 电压调错代价很大，照二手资料猜编码是不能接受的。
///
/// **策略全在这一侧。** 辅助进程只回原始状态码和回读值，"算不算写成功"、
/// "上界是多少"、"撤销恢复到哪儿"由这里判断 —— 两边各存一份迟早对不上。
/// </summary>
public sealed class AmdCpuControlWriter(
    HardwareBridgeClient bridge,
    IHostEnvironment environment,
    ILogger<AmdCpuControlWriter>? logger = null) : IControlWriter
{
    internal const string PowerLimitCapabilityId = "cpu.power-limit";
    internal const string SlowPowerLimitCapabilityId = "cpu.slow-power-limit";
    internal const string FastPowerLimitCapabilityId = "cpu.fast-power-limit";
    internal const string TdcLimitCapabilityId = "cpu.tdc-limit";
    internal const string EdcLimitCapabilityId = "cpu.edc-limit";
    internal const string TemperatureLimitCapabilityId = "cpu.temperature-limit";
    internal const string PowerBoostScalarCapabilityId = "cpu.pbo-scalar";
    internal const string CurveOptimizerCapabilityId = "cpu.curve-optimizer";

    /// <summary>
    /// 这颗处理器上一项可写的东西：怎么写、怎么读回来、基线记在哪。
    ///
    /// **一项一行，没有例外分支。** 先前这里是「功耗上限走一套路径，
    /// Curve Optimizer 走另一套」，于是每加一项都要在探测、写入、读取三处
    /// 各加一个 if。它们的差别其实只是几个字段值，那就让它们只是几个字段值。
    ///
    /// <see cref="ReadingField"/> 为 null 表示**这一项读不回来**。
    /// 那时只能以 SMU 的状态码为准 —— 不拿一个看着像的数冒充回读值。
    /// </summary>
    private sealed record CpuWritable(
        string CapabilityId,
        /// <summary>辅助进程的操作名。</summary>
        string Operation,
        /// <summary>同一个操作下的分支（哪一条功耗墙、哪一条电流墙）。没有就是 null。</summary>
        string? Which,
        /// <summary>回读字段。null 表示这一项读不回来。</summary>
        string? ReadingField,
        string Unit,
        string BaselineFileName,
        /// <summary>describe 里哪个标志说明这颗处理器有这一项。</summary>
        string DescribeFlag,
        /// <summary>回读允许的误差。SMU 报的是浮点，不要求逐位相等。</summary>
        double Tolerance,
        /// <summary>
        /// 这一项为什么还不敢写。null 表示可以写。
        ///
        /// **认不出当前值就不写。** 这几条都是"限制"类的项：写下去之后没法确认
        /// 到底生效没有，撤销时也没法把它放回原处。与其写下去再祈祷，
        /// 不如如实列出来、说清楚差什么 —— 这和"根本不显示"是两回事。
        /// </summary>
        string? Unverified = null);

    private static readonly CpuWritable[] Writables =
    [
        // 三条功耗墙。**它们是三件不同的事，所以是三项。**
        // 持续决定机器长期跑在多少瓦，短时决定几十秒的爬坡能到多高，
        // 瞬时决定毫秒级的峰值。先前持续和短时合成一项，等于替用户砍掉一条。
        new(PowerLimitCapabilityId, "set-power-limit", "stapm",
            "stapmLimitWatts", ControlUnits.Watt, "cpu-power-baseline.txt", "powerTable", 1),
        new(SlowPowerLimitCapabilityId, "set-power-limit", "slow",
            "slowLimitWatts", ControlUnits.Watt, "cpu-slow-power-baseline.txt", "powerTable", 1),
        new(FastPowerLimitCapabilityId, "set-power-limit", "fast",
            "fastLimitWatts", ControlUnits.Watt, "cpu-fast-power-baseline.txt", "powerTable", 1),

        // 两条电流墙。功耗墙管的是瓦，这两条管的是安 —— 供电扛不扛得住
        // 和散热扛不扛得住是两件事，别家（Ryzen Master）也是分开摆的。
        new(TdcLimitCapabilityId, "set-current-limit", "tdc-vdd",
            "tdcLimitAmps", ControlUnits.Ampere, "cpu-tdc-baseline.txt", "powerTable", 1,
            CurrentLimitMappingUnconfirmed),
        new(EdcLimitCapabilityId, "set-current-limit", "edc-vdd",
            "edcLimitAmps", ControlUnits.Ampere, "cpu-edc-baseline.txt", "powerTable", 1,
            CurrentLimitMappingUnconfirmed),

        // 温度墙。**读不回来**，见 ReadingField 的说明。
        new(TemperatureLimitCapabilityId, "set-temperature-limit", null,
            null, ControlUnits.Celsius, "cpu-temperature-baseline.txt", "powerTable", 1,
            TemperatureLimitUnverifiable),

        // PBO 标量。1 倍就是厂商默认，所以基线不用读 —— 它是有文档的固定值。
        new(PowerBoostScalarCapabilityId, "set-pbo-scalar", null,
            null, ControlUnits.Multiplier, "cpu-pbo-scalar-baseline.txt", "powerTable", 0.5),

        new(CurveOptimizerCapabilityId, "set-curve-optimizer", null,
            "curveOptimizerCounts", ControlUnits.Step, "cpu-curve-optimizer-baseline.txt",
            "curveOptimizer", 0.5)
    ];

    private static CpuWritable? WritableOf(string capabilityId)
        => Writables.FirstOrDefault(
            (entry) => string.Equals(entry.CapabilityId, capabilityId, StringComparison.Ordinal));

    private const string BridgeMissing = "需要硬件写入辅助进程。";
    private const string BridgeSilent = "辅助进程没应答。";
    private const string NotSupported = "这颗处理器没有这一项。";
    private const string NotEffective = "写下去了，回读没变。";

    /// <summary>
    /// 温度墙现在为什么不让调。
    ///
    /// 它是这一页上唯一一条**拆掉保护**的项，而这台机器的 PM table 里还没认出
    /// 哪一项是它的当前值。没有回读就没法确认写进去没有，也没法在撤销时把它放回原处
    /// —— 这种项宁可先不开放，也不能写下去之后只能祈祷。
    /// </summary>
    private const string TemperatureLimitUnverifiable =
        "还认不出这颗处理器温度墙的当前值，没有回读就不写。";

    /// <summary>
    /// 两条电流墙为什么都关着。
    ///
    /// **实测踩到过：发"持续电流墙"的命令，动的却是峰值那一格。**
    /// 写之前表里是 [6]=85 A、[8]=125 A；发了一条 tdc-vdd 之后 [6] 纹丝不动，
    /// [8] 掉到 0.1 A，处理器被掐到二十几瓦。
    ///
    /// 也就是说"哪条命令对应哪道墙"在这颗处理器上还没对准 —— 可能是 MP1 的
    /// 命令号映射和 ZenStates 表里写的不一致，也可能是这两个表位本身就该反过来读。
    /// 没对准之前不写：写错一道电流墙不会报错，只会安静地把机器掐住。
    ///
    /// 要解开它，得在**管理员**下把 PM table 倒出来、逐条命令对照着看哪一格动了。
    /// 在那之前这两项照常列出来并说明原因，不假装没有。
    /// </summary>
    private const string CurrentLimitMappingUnconfirmed =
        "命令和电流墙的对应关系还没对准，写错会把处理器掐住，先不开放。";

    private readonly object gate = new();
    private readonly Dictionary<string, double> baselines = [];

    /// <summary>
    /// 处理器这几项全走 AMD 的 SMU，经硬件写入辅助进程。
    /// </summary>
    public string? ChannelOf(ControlObject target, string capabilityId)
        => IsMine(target, capabilityId) && WritableOf(capabilityId) is not null
            ? ControlChannels.AmdSmu
            : null;

    public ControlWriteAvailability Probe(ControlObject target, ControlCapability capability)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(capability);

        if (!IsMine(target, capability.Id) || WritableOf(capability.Id) is not { } writable)
        {
            return ControlWriteAvailability.NotMine;
        }
        if (!bridge.IsInstalled)
        {
            return ControlWriteAvailability.No(BridgeMissing, kind: ControlUnavailableKinds.Component);
        }

        var described = Ask("describe", null);
        if (described is not { } description || !Flag(description, "available"))
        {
            return ControlWriteAvailability.No(Text(described, "reason") ?? BridgeSilent);
        }

        // 能设什么就显示什么：辅助进程按这代处理器有没有这条 SMU 命令来报。
        if (!Flag(description, writable.DescribeFlag))
        {
            return ControlWriteAvailability.No(NotSupported);
        }

        if (writable.Unverified is { } unverified)
        {
            // 这几条是**我们**还没查清（电流墙映射、温度墙回读），不是硬件做不到。
            // 标成平台不支持等于把自己欠的说成用户的机器不行。
            return ControlWriteAvailability.No(unverified, kind: ControlUnavailableKinds.NotImplemented);
        }

        if (Ask("read", null) is not { } reading || !IsUsableReading(reading, writable.ReadingField))
        {
            return ControlWriteAvailability.No(BridgeSilent);
        }

        return ControlWriteAvailability.Yes(RangeOf(writable, capability, reading));
    }

    /// <summary>
    /// 这一项能设到哪儿。
    ///
    /// **形状来自目录，默认值来自硬件。** 上下界是"这类东西该有的范围"，
    /// 目录一处写清楚；默认值是"我们动手之前它是多少"，只有机器知道。
    ///
    /// 功耗墙和电流墙的上界**不收到出厂值**：调高了自有温度墙兜底，撞上去照样降频。
    /// 先前这里把上界夹在基线，理由是"笔记本能加多少由整机厂定"——
    /// 可这是台 Windows 上的软件，不是笔记本专用软件，那条理由对台式机根本不成立。
    /// </summary>
    internal static bool IsUsableReading(JsonElement reading, string? field)
        => reading.ValueKind == JsonValueKind.Object && Flag(reading, "ok")
            && (field is null || Number(reading, field) is { } value && double.IsFinite(value));

    private ControlNumberRange RangeOf(
        CpuWritable writable,
        ControlCapability capability,
        JsonElement reading)
    {
        var declared = capability.Range;
        // 读不回来的项（PBO 标量）用目录里写的默认值 —— 那个 1 倍是有文档的固定值，
        // 不是猜的。
        var current = writable.ReadingField is { } field
            ? Number(reading, field)
            : declared?.DefaultValue;
        double? baseline = current is { } value ? CaptureBaseline(writable, value) : null;

        return declared is { } shape
            ? shape with { DefaultValue = baseline is { } found ? Math.Round(found, 2) : null }
            : new ControlNumberRange(0, 100, 1, writable.Unit, baseline);
    }

    public async Task<ControlApplyOutcome> WriteAsync(
        ControlObject target,
        ControlCapability capability,
        ControlSetting setting,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(capability);
        ArgumentNullException.ThrowIfNull(setting);

        if (WritableOf(capability.Id) is not { } writable)
        {
            return Failed(target, capability, NotSupported);
        }
        if (setting.Number is not { } value || !double.IsFinite(value))
        {
            return Failed(target, capability, "这一项要一个数值，收到的不是。");
        }
        var bounds = capability.Range;
        var wanted = bounds is null ? value : Math.Clamp(value, bounds.Minimum, bounds.Maximum);

        /*
         * **值和它的量纲一起过边界。**
         *
         * 辅助进程那一侧的 SMU 邮箱不校验量纲，发多少收多少。先前这里发的是
         * 一个裸数字、量纲藏在参数名里（watts / amps），于是"85 A"被当成
         * 85 mA 写了下去，处理器被掐到二十几瓦，而回执一路正常。
         *
         * 单位用的就是这一项自己声明的那个（统一的 ControlUnits）——
         * 写入层第四步已经把值归一到它了，这里原样带过去即可，不另起一套名字。
         */
        var arguments = new Dictionary<string, object?>
        {
            ["value"] = wanted,
            ["unit"] = writable.Unit
        };
        if (writable.Which is { } which)
        {
            arguments["which"] = which;
        }

        var response = await bridge.SendAsync(writable.Operation, arguments, cancellationToken)
            .ConfigureAwait(false);
        if (response is not { } result || !Flag(result, "ok"))
        {
            return Failed(target, capability, BridgeSilent);
        }

        // 辅助进程说 SMU 拒绝了就是拒绝，不用再看回读。
        if (result.TryGetProperty("accepted", out var accepted)
            && accepted.ValueKind == JsonValueKind.False)
        {
            return Failed(target, capability, "SMU 拒绝了这次写入。");
        }

        /*
         * SMU 自己报的状态码。**不是 OK 就是没写进去。**
         *
         * 辅助进程的 ok 只表示"这条请求它处理完了"，不表示硬件收下了 ——
         * 邮箱超时（TIMEOUT_MAILBOX_READY）时它照样回 ok。
         * 有回读的项还能靠下面那一步兜住，没有回读的项（PBO 标量）
         * 就会把一次超时当成"已应用"报给用户。
         */
        if (Text(result, "status") is { } status
            && !string.Equals(status, "OK", StringComparison.OrdinalIgnoreCase))
        {
            logger?.LogWarning(
                "处理器 {Capability} 写入被 SMU 拒绝或超时：{Status}。",
                capability.Id,
                status);
            return Failed(target, capability, $"SMU 没收下这次写入（{status}）。");
        }

        // 回执只说"收到了"。真正算数的是回读那个**上限** —— 不是实际值，
        // 实际值由固件按温度调度，通常远低于上限。
        if (writable.ReadingField is not { } field)
        {
            // 读不回来的项只能以状态码为准，上面已经看过了。
            return Applied(target, capability);
        }
        if (Number(result, field) is not { } after
            || Math.Abs(after - wanted) > writable.Tolerance)
        {
            logger?.LogWarning(
                "处理器 {Capability}（{Field}）没写进去：要 {Wanted}，回读 {After}。",
                capability.Id,
                field,
                wanted,
                Number(result, field));
            return Failed(target, capability, NotEffective);
        }

        return Applied(target, capability);
    }

    /// <summary>
    /// 这一项现在实际是多少。
    ///
    /// 功耗读的是那个**上限**，不是实际功耗 —— 实际值由固件按温度调度，
    /// 通常远低于上限，拿它当"当前设定"显示会让用户以为自己的设定没生效。
    /// </summary>
    public async Task<ControlActualValue?> ReadAsync(
        ControlObject target,
        ControlCapability capability,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(capability);

        if (WritableOf(capability.Id) is not { } writable)
        {
            return null;
        }
        if (writable.ReadingField is not { } field)
        {
            return new ControlActualValue(
                target.Id,
                capability.Id,
                UnreadableReason: "这一项没有可读的当前值。");
        }

        var reading = await bridge.SendAsync("read", null, cancellationToken)
            .ConfigureAwait(false);
        if (reading is not { } result || !Flag(result, "ok"))
        {
            return new ControlActualValue(
                target.Id,
                capability.Id,
                UnreadableReason: BridgeSilent);
        }

        return Number(result, field) is { } value
            ? new ControlActualValue(
                target.Id,
                capability.Id,
                Number: Math.Round(value, 1),
                Unit: writable.Unit)
            : new ControlActualValue(
                target.Id,
                capability.Id,
                UnreadableReason: "这颗处理器没有报出这一项的当前值。");
    }

    /// <summary>
    /// 我们动手之前的值。**记在盘上，只记一次。**
    ///
    /// 只放在内存里不够：用户调低之后重启，下次读到的"当前值"就是那个低的，
    /// "恢复默认"会回到他调过的数而不是原本的数，而且一次比一次低。
    /// </summary>
    private double CaptureBaseline(CpuWritable writable, double current)
    {
        lock (gate)
        {
            if (baselines.TryGetValue(writable.CapabilityId, out var captured))
            {
                return captured;
            }
            var path = BaselinePath(writable.BaselineFileName);
            var baseline = ReadPersisted(path) ?? current;
            baselines[writable.CapabilityId] = baseline;
            WritePersisted(path, baseline);
            return baseline;
        }
    }

    private double? ReadPersisted(string path)
    {
        try
        {
            return File.Exists(path)
                && double.TryParse(
                    File.ReadAllText(path).Trim(),
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var value)
                    ? value
                    : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            logger?.LogWarning(error, "读不到基线：{Path}", path);
            return null;
        }
    }

    private void WritePersisted(string path, double value)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, value.ToString("0.###", CultureInfo.InvariantCulture));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // 记不下来不影响这次调节，只是下次重启后"恢复默认"可能不准。
            logger?.LogWarning(error, "存不下基线：{Path}", path);
        }
    }

    private string BaselinePath(string fileName) => Path.Combine(
        environment.ContentRootPath,
        "UserData",
        "Control",
        fileName);

    /// <summary>探测在界面读取的路径上，只能同步等 —— 这条通道本来就是串行的。</summary>
    private JsonElement? Ask(string operation, IReadOnlyDictionary<string, object?>? arguments)
        => bridge.SendAsync(operation, arguments, CancellationToken.None)
            .GetAwaiter()
            .GetResult();

    private static bool Flag(JsonElement? element, string name)
        => element is { } value
            && value.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.True;

    private static double? Number(JsonElement? element, string name)
        => element is { } value
            && value.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.Number
                ? property.GetDouble()
                : null;

    private static string? Text(JsonElement? element, string name)
        => element is { } value
            && value.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.String
                ? property.GetString()
                : null;

    private static bool IsMine(ControlObject target, string capabilityId)
        => string.Equals(target.Kind, ControlObjectKinds.Cpu, StringComparison.Ordinal)
            && string.Equals(target.Platform.Vendor, ControlVendors.Amd, StringComparison.Ordinal)
            && WritableOf(capabilityId) is not null;

    private static ControlApplyOutcome Applied(ControlObject target, ControlCapability capability)
        => new(target.Id, capability.Id, ControlApplyStatuses.Applied, null);

    private static ControlApplyOutcome Failed(
        ControlObject target,
        ControlCapability capability,
        string reason)
        => new(target.Id, capability.Id, ControlApplyStatuses.Failed, reason);
}
