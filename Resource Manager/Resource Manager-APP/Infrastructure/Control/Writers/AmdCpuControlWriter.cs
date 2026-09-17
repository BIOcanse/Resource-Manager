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
    internal const string CurveOptimizerCapabilityId = "cpu.curve-optimizer";

    /// <summary>回读允许的误差，单位瓦。SMU 报的是浮点，不要求逐位相等。</summary>
    private const double WattTolerance = 1;

    private const string BridgeMissing = "需要先安装硬件写入辅助进程。";
    private const string BridgeSilent = "硬件写入辅助进程没有应答。";
    private const string NotSupported = "这颗处理器上没有这一项。";
    private const string NotEffective = "写下去了，但回读的值没有变。";

    private readonly object gate = new();
    private double? baselinePowerLimitWatts;
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
        var isPowerLimit = string.Equals(
            capability.Id,
            PowerLimitCapabilityId,
            StringComparison.Ordinal);
        if (!Flag(description, isPowerLimit ? "powerTable" : "curveOptimizer"))
        {
            return ControlWriteAvailability.No(NotSupported);
        }

        if (Ask("read", null) is not { } reading)
        {
            return ControlWriteAvailability.No(BridgeSilent);
        }

        return isPowerLimit
            ? PowerLimitAvailability(capability, reading)
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
        ControlCapability capability,
        JsonElement reading)
    {
        if (Number(reading, "stapmLimitWatts") is not { } current)
        {
            return ControlWriteAvailability.No(BridgeSilent);
        }

        var baseline = CaptureBaselinePowerLimit(current);
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

        return string.Equals(capability.Id, PowerLimitCapabilityId, StringComparison.Ordinal)
            ? await WritePowerLimitAsync(target, capability, wanted, cancellationToken)
                .ConfigureAwait(false)
            : await WriteCurveOptimizerAsync(target, capability, (int)wanted, cancellationToken)
                .ConfigureAwait(false);
    }

    /// <summary>
    /// 持续和长时一起设成这个值，**瞬时不动** —— 用户说的"功耗上限"是持续那个，
    /// 把瞬时也压下去会连短暂加速一起砍掉，那是另一回事。
    /// </summary>
    private async Task<ControlApplyOutcome> WritePowerLimitAsync(
        ControlObject target,
        ControlCapability capability,
        double watts,
        CancellationToken cancellationToken)
    {
        JsonElement? last = null;
        foreach (var which in new[] { "stapm", "slow" })
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
        if (Number(last, "stapmLimitWatts") is not { } after
            || Math.Abs(after - watts) > WattTolerance)
        {
            logger?.LogWarning(
                "处理器功耗上限没写进去：要 {Watts} W，回读 {After}。",
                watts,
                Number(last, "stapmLimitWatts"));
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
    /// 我们动手之前的值。**记在盘上，只记一次。**
    ///
    /// 只放在内存里不够：用户调低之后重启，下次读到的"当前值"就是那个低的，
    /// "恢复默认"会回到他调过的数而不是原本的数，而且一次比一次低。
    /// </summary>
    private double CaptureBaselinePowerLimit(double current)
    {
        lock (gate)
        {
            if (baselinePowerLimitWatts is { } captured)
            {
                return captured;
            }
            var baseline = ReadPersisted(PowerLimitBaselinePath) ?? current;
            baselinePowerLimitWatts = baseline;
            WritePersisted(PowerLimitBaselinePath, baseline);
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

    private string PowerLimitBaselinePath => BaselinePath("cpu-power-baseline.txt");

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
            && capabilityId is PowerLimitCapabilityId or CurveOptimizerCapabilityId;

    private static ControlApplyOutcome Applied(ControlObject target, ControlCapability capability)
        => new(target.Id, capability.Id, ControlApplyStatuses.Applied, null);

    private static ControlApplyOutcome Failed(
        ControlObject target,
        ControlCapability capability,
        string reason)
        => new(target.Id, capability.Id, ControlApplyStatuses.Failed, reason);
}
