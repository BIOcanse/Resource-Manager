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
    internal const string FastPowerLimitCapabilityId = "cpu.fast-power-limit";
    internal const string CurveOptimizerCapabilityId = "cpu.curve-optimizer";

    /// <summary>
    /// 一项功耗上限：读辅助进程回的哪个字段、写哪几条 SMU 命令、基线记在哪个文件。
    ///
    /// 底层本来就是三条独立的命令（持续 / 长时 / 瞬时）。持续和长时合成一项，
    /// 是因为它们一起决定"机器长期跑在多少瓦"这一件事；瞬时决定的是另一件事
    /// —— 短暂加速能冲到多高 —— 所以它单独一项，不替用户把这半边砍掉。
    /// </summary>
    private sealed record PowerLimitKind(
        string CapabilityId,
        string ReadingField,
        IReadOnlyList<string> Which,
        string BaselineFileName);

    private static readonly PowerLimitKind[] PowerLimitKinds =
    [
        new(PowerLimitCapabilityId, "stapmLimitWatts", ["stapm", "slow"], "cpu-power-baseline.txt"),
        new(FastPowerLimitCapabilityId, "fastLimitWatts", ["fast"], "cpu-fast-power-baseline.txt")
    ];

    private static PowerLimitKind? PowerLimitKindOf(string capabilityId)
        => PowerLimitKinds.FirstOrDefault(
            (kind) => string.Equals(kind.CapabilityId, capabilityId, StringComparison.Ordinal));

    /// <summary>回读允许的误差，单位瓦。SMU 报的是浮点，不要求逐位相等。</summary>
    private const double WattTolerance = 1;

    private const string BridgeMissing = "需要先安装硬件写入辅助进程。";
    private const string BridgeSilent = "硬件写入辅助进程没有应答。";
    private const string NotSupported = "这颗处理器上没有这一项。";
    private const string NotEffective = "写下去了，但回读的值没有变。";

    private readonly object gate = new();
    private readonly Dictionary<string, double> baselinePowerLimitWatts = [];
    private int? baselineCurveOptimizerCounts;

    public ControlWriteAvailability Probe(ControlObject target, ControlCapability capability)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(capability);

        if (!IsMine(target, capability.Id))
        {
            return ControlWriteAvailability.NotMine;
        }
        if (!bridge.IsInstalled)
        {
            return ControlWriteAvailability.No(BridgeMissing);
        }

        var described = Ask("describe", null);
        if (described is not { } description || !Flag(description, "available"))
        {
            return ControlWriteAvailability.No(Text(described, "reason") ?? BridgeSilent);
        }

        // 能设什么就显示什么：辅助进程按这代处理器有没有这条 SMU 命令来报。
        var powerLimit = PowerLimitKindOf(capability.Id);
        if (!Flag(description, powerLimit is not null ? "powerTable" : "curveOptimizer"))
        {
            return ControlWriteAvailability.No(NotSupported);
        }

        if (Ask("read", null) is not { } reading)
        {
            return ControlWriteAvailability.No(BridgeSilent);
        }

        return powerLimit is { } kind
            ? PowerLimitAvailability(kind, capability, reading)
            : CurveOptimizerAvailability(capability, reading);
    }

    /// <summary>
    /// 功耗上限的范围。
    ///
    /// **上界就是基线，只能往下调不能往上。** 这是笔记本：往上加功耗要散热和供电扛得住，
    /// 那是整机厂连同散热模组定下来的，我们既读不到余量也没资格替它加。
    /// 上界也不能从当前值推 —— 那样调低之后上界跟着变低，用户想调回去会被静默夹住
    /// 还报"已应用"，等于悄悄改掉他的意图。
    /// </summary>
    private ControlWriteAvailability PowerLimitAvailability(
        PowerLimitKind kind,
        ControlCapability capability,
        JsonElement reading)
    {
        if (Number(reading, kind.ReadingField) is not { } current)
        {
            return ControlWriteAvailability.No(BridgeSilent);
        }

        var baseline = CaptureBaselinePowerLimit(kind, current);
        var minimum = capability.Range is { } declared
            ? Math.Min(declared.Minimum, Math.Round(baseline))
            : 5;
        return ControlWriteAvailability.Yes(new ControlNumberRange(
            minimum,
            Math.Round(baseline),
            1,
            "W",
            Math.Round(baseline)));
    }

    /// <summary>
    /// Curve Optimizer 的范围。单位是**档**不是伏 —— 一档大概几毫伏，具体多少随体质变，
    /// 厂商也不给换算，所以照它本来的单位显示，不编一个伏特数出来骗人。
    /// </summary>
    private ControlWriteAvailability CurveOptimizerAvailability(
        ControlCapability capability,
        JsonElement reading)
    {
        var current = (int)(Number(reading, "curveOptimizerCounts") ?? 0);
        var baseline = CaptureBaselineCurveOptimizer(current);
        return ControlWriteAvailability.Yes(
            capability.Range is { } declared
                ? declared with { DefaultValue = baseline }
                : new ControlNumberRange(-30, 10, 1, "档", baseline));
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

        if (setting.Number is not { } value || !double.IsFinite(value))
        {
            return Failed(target, capability, "这一项要一个数值，收到的不是。");
        }
        var bounds = capability.Range;
        var wanted = bounds is null ? value : Math.Clamp(value, bounds.Minimum, bounds.Maximum);

        return PowerLimitKindOf(capability.Id) is { } kind
            ? await WritePowerLimitAsync(kind, target, capability, wanted, cancellationToken)
                .ConfigureAwait(false)
            : await WriteCurveOptimizerAsync(target, capability, (int)wanted, cancellationToken)
                .ConfigureAwait(false);
    }

    /// <summary>
    /// 把这一项对应的那几条 SMU 命令都设成这个值，**别的不动**。
    /// 持续和长时是一项，瞬时是另一项，各写各的。
    /// </summary>
    private async Task<ControlApplyOutcome> WritePowerLimitAsync(
        PowerLimitKind kind,
        ControlObject target,
        ControlCapability capability,
        double watts,
        CancellationToken cancellationToken)
    {
        JsonElement? last = null;
        foreach (var which in kind.Which)
        {
            last = await bridge.SendAsync(
                "set-power-limit",
                new Dictionary<string, object?> { ["which"] = which, ["watts"] = watts },
                cancellationToken).ConfigureAwait(false);
            if (last is not { } response || !Flag(response, "ok"))
            {
                return Failed(target, capability, BridgeSilent);
            }
        }

        // 回执只说"收到了"。真正算数的是回读那个**上限** —— 不是实际功耗，
        // 实际值由固件按温度调度，通常远低于上限。
        if (Number(last, kind.ReadingField) is not { } after
            || Math.Abs(after - watts) > WattTolerance)
        {
            logger?.LogWarning(
                "处理器功耗上限（{Field}）没写进去：要 {Watts} W，回读 {After}。",
                kind.ReadingField,
                watts,
                Number(last, kind.ReadingField));
            return Failed(target, capability, NotEffective);
        }

        return Applied(target, capability);
    }

    private async Task<ControlApplyOutcome> WriteCurveOptimizerAsync(
        ControlObject target,
        ControlCapability capability,
        int counts,
        CancellationToken cancellationToken)
    {
        var response = await bridge.SendAsync(
            "set-curve-optimizer",
            new Dictionary<string, object?> { ["counts"] = counts },
            cancellationToken).ConfigureAwait(false);
        if (response is not { } result || !Flag(result, "ok"))
        {
            return Failed(target, capability, BridgeSilent);
        }
        if (!Flag(result, "accepted"))
        {
            return Failed(target, capability, "SMU 拒绝了这次写入。");
        }
        // 读得回来就核对；有些处理器读不回来，那时只能以 SMU 的回执为准。
        if (Number(result, "curveOptimizerCounts") is { } after && (int)after != counts)
        {
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

        var reading = await bridge.SendAsync("read", null, cancellationToken)
            .ConfigureAwait(false);
        if (reading is not { } result || !Flag(result, "ok"))
        {
            return new ControlActualValue(
                target.Id,
                capability.Id,
                UnreadableReason: BridgeSilent);
        }

        var powerLimit = PowerLimitKindOf(capability.Id);
        var number = Number(
            result,
            powerLimit?.ReadingField ?? "curveOptimizerCounts");
        return number is { } value
            ? new ControlActualValue(
                target.Id,
                capability.Id,
                Number: powerLimit is not null ? Math.Round(value, 1) : Math.Round(value),
                Unit: powerLimit is not null ? ControlUnits.Watt : ControlUnits.Step)
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
    private double CaptureBaselinePowerLimit(PowerLimitKind kind, double current)
    {
        lock (gate)
        {
            if (baselinePowerLimitWatts.TryGetValue(kind.CapabilityId, out var captured))
            {
                return captured;
            }
            var path = BaselinePath(kind.BaselineFileName);
            var baseline = ReadPersisted(path) ?? current;
            baselinePowerLimitWatts[kind.CapabilityId] = baseline;
            WritePersisted(path, baseline);
            return baseline;
        }
    }

    private int CaptureBaselineCurveOptimizer(int current)
    {
        lock (gate)
        {
            if (baselineCurveOptimizerCounts is { } captured)
            {
                return captured;
            }
            var baseline = (int)(ReadPersisted(CurveOptimizerBaselinePath) ?? current);
            baselineCurveOptimizerCounts = baseline;
            WritePersisted(CurveOptimizerBaselinePath, baseline);
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

    private string CurveOptimizerBaselinePath => BaselinePath("cpu-curve-optimizer-baseline.txt");

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
            && capabilityId is PowerLimitCapabilityId
                or FastPowerLimitCapabilityId
                or CurveOptimizerCapabilityId;

    private static ControlApplyOutcome Applied(ControlObject target, ControlCapability capability)
        => new(target.Id, capability.Id, ControlApplyStatuses.Applied, null);

    private static ControlApplyOutcome Failed(
        ControlObject target,
        ControlCapability capability,
        string reason)
        => new(target.Id, capability.Id, ControlApplyStatuses.Failed, reason);
}
