using System.Text.Json;
using ResourceManager.App.Application.Control;
using ResourceManager.App.Domain.Control;

namespace ResourceManager.App.Infrastructure.Control.Writers;

/// <summary>
/// 风扇，经由风扇控制核心。
///
/// **能做什么由核心报，不由我们假设。** 核心对每个风扇报两件事：
/// 现在是固件在管还是我们在管（<c>automaticControl</c>），
/// 以及这条通道给不给任意占空比（<c>supportsDuty</c>）。
/// 界面据此决定哪一项能动、哪一项置灰 —— 有的机器只有"全速 / 自动"两档，
/// 那种机器上摆一条能拖的曲线是骗人。
/// </summary>
public sealed class FanControlWriter(
    FanControlCoreClient core,
    ILogger<FanControlWriter>? logger = null) : IControlWriter
{
    internal const string LockMaximumCapabilityId = "fan.lock-maximum";
    internal const string CurveCapabilityId = "fan.curve";
    internal const string DutyCapabilityId = "fan.duty";
    internal const string RpmCapabilityId = "fan.rpm";

    /// <summary>
    /// 机身风扇的对象编号前缀，后面跟的是**核心报的那个风扇序号**。
    ///
    /// 序号必须进编号：这条通道上常常不止一个风扇（本机是主/副两个），
    /// 编号里不带序号，写入时就只能猜一个，那等于只能控第一个。
    /// </summary>
    internal const string ObjectIdPrefix = "fan:oem";

    private const string CoreMissing = "需要风扇控制核心。";
    private const string CoreSilent = "风扇控制核心没应答。";
    private const string NoChannel = "没有可用的风扇控制通道。";
    private const string NoDutyControl = "这台机器的风扇只有全速和自动两档。";

    /// <summary>机身风扇走风扇控制核心；具体哪条底层通道由核心自己报（见对象的词条）。</summary>
    public string? ChannelOf(ControlObject target, string capabilityId)
        => IsMine(target, capabilityId) ? ControlChannels.FanControlCore : null;

    /// <summary>
    /// 这个风扇**现在跑的**曲线，从固件里读出来。
    ///
    /// 用户要改曲线，起点必须是机器现在真在跑的那条 ——
    /// 给他一条我们编的曲线，他改出来的东西和这台机器的实际行为没有关系。
    ///
    /// **不归 <see cref="Probe"/> 管，也不进对象清单。** 读一次要把固件的三张表
    /// 都过一遍（几十次 EC 往返），而对象清单是画一次界面就要问一遍的。
    /// 所以它单独一条按需的路，只在用户真去看曲线的时候走。
    /// </summary>
    public Task<IReadOnlyList<ControlCurvePoint>?> ReadCurveAsync(
        ControlObject target,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        return IsMine(target, CurveCapabilityId)
            ? core.ReadCurveAsync(FanIndexOf(target), cancellationToken)
            : Task.FromResult<IReadOnlyList<ControlCurvePoint>?>(null);
    }

    public ControlWriteAvailability Probe(ControlObject target, ControlCapability capability)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(capability);

        if (!IsMine(target, capability.Id))
        {
            return ControlWriteAvailability.NotMine;
        }
        if (!core.IsInstalled)
        {
            return ControlWriteAvailability.No(CoreMissing, kind: ControlUnavailableKinds.Component);
        }

        /*
         * **用客户端缓存的那份清单，不再自己去问一次。**
         *
         * 目录每列一次可控对象，就要对每个风扇的每一项调一次 Probe；
         * 先前每次都发一条 describe 过去，于是列一次对象要走十几次往返。
         * 更糟的是那条通道一次只走一个请求 —— 后台一旦有个慢操作占着
         * （比如开机预读曲线，要十几秒），这些 Probe 全堵在后面，
         * 前端等不到清单就报"读不到可控对象"。**实测撞到过。**
         *
         * 这份清单本来就是"能做什么"，开通道那一刻就定了，客户端也早就缓存着。
         */
        if (core.Describe().FirstOrDefault(entry => entry.Index == FanIndexOf(target))
            is not { } fan)
        {
            // 用不上的具体原因由核心给 —— "没有这个接口"和"没有管理员权限"
            // 对用户是两件完全不同的事，笼统一句话等于什么都没说。
            return ControlWriteAvailability.No(core.UnavailableReason ?? NoChannel);
        }
        if (capability.Id == RpmCapabilityId)
        {
            return ControlWriteAvailability.No("转速为监测值。", canRead: true);
        }
        if (!fan.Writable)
        {
            return ControlWriteAvailability.No("固件没有声明支持风扇控制，只读。", canRead: true);
        }

        /*
         * **曲线和定速各有各的前提，不能共用一个。**
         *
         * 定速是我们直接给一个占空比，所以要这条通道"能给任意占空比"。
         * 曲线是把一张表交给固件去查，走的是完全不同的机制 ——
         * 本机就是活例子：`supportsDuty` 是 false（只有全速和自动两档），
         * 可固件那张曲线表照样读得出来、写得进去。
         * 先前两者共用 `supportsDuty`，于是曲线被一个和它无关的理由挡在外面。
         */
        if (string.Equals(capability.Id, CurveCapabilityId, StringComparison.Ordinal))
        {
            if (fan.FirmwareCurve is null && !fan.SupportsSoftwareCurve)
            {
                return ControlWriteAvailability.No(
                    "这条通道只能切自动和全速，给不了曲线。",
                    kind: ControlUnavailableKinds.Platform);
            }
            /*
             * 固件那条的前置条件没满足，又没有软件那条兜底 ——
             * **锁住，并把前置条件原样告诉用户。**
             *
             * 这不是"这台机器做不到"（那是 platform），是"还差一步"：
             * 和档位不够、缺组件属于同一类，用户自己动一下就解得开。
             * 不锁的话，他画完曲线、界面报"已应用"，风扇却毫无反应。
             *
             * 有软件那条的机器**不锁**：固件不执行，我们自己跑就是了 ——
             * 用户在编辑器里本来就能选这条曲线交给谁执行。
             */
            if (fan.FirmwareCurveRequires is { } requirement && !fan.SupportsSoftwareCurve)
            {
                return ControlWriteAvailability.No(
                    requirement,
                    kind: ControlUnavailableKinds.Prerequisite,
                    canRead: true);
            }
            // 固件那条或者软件那条，有一条就能给曲线。
            return ControlWriteAvailability.Yes(capability.Range);
        }
        if (capability.Id is DutyCapabilityId)
        {
            if (!fan.SupportsDuty)
            {
                return ControlWriteAvailability.No(
                    NoDutyControl,
                    kind: ControlUnavailableKinds.Platform, canRead: true);
            }
            /*
             * 下限取这个风扇**转得起来**的最低占空比，不是 0。
             *
             * 有的平台 0 到最小启动档之间是硬件死区：写 5% 和写 0 的结果一样（停转）。
             * 让用户能拖到那一段，界面会显示"已应用 5%"而风扇是停的。
             * 0 本身仍然留着 —— 停转是个有意义的选择，只是不能停在 1%~死区之间。
             */
            var minimumOn = (double)fan.MinimumOnPercent;
            return ControlWriteAvailability.Yes(
                capability.Range is { } range && minimumOn > range.Minimum
                    ? range with { Minimum = minimumOn }
                    : capability.Range);
        }
        return ControlWriteAvailability.Yes(capability.Range);
    }

    public async Task<bool> RestoreAsync(
        ControlObject target,
        ControlCapability capability,
        IReadOnlySet<string> stillConfigured,
        CancellationToken cancellationToken)
    {
        if (stillConfigured.Any(id => id is DutyCapabilityId or CurveCapabilityId or LockMaximumCapabilityId))
        {
            return true;
        }
        var index = FanIndexOf(target);
        var response = await core.SendAsync("auto",
            new Dictionary<string, object?> { ["fan"] = index }, cancellationToken).ConfigureAwait(false);
        return response is { } result && Flag(result, "ok")
            && FanAt(result, index) is { } fan && Flag(fan, "automaticControl");
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

        var fanIndex = FanIndexOf(target);

        if (string.Equals(capability.Id, DutyCapabilityId, StringComparison.Ordinal))
        {
            return await WriteDutyAsync(target, capability, setting, fanIndex, cancellationToken)
                .ConfigureAwait(false);
        }
        if (string.Equals(capability.Id, CurveCapabilityId, StringComparison.Ordinal))
        {
            return await WriteCurveAsync(target, capability, setting, fanIndex, cancellationToken)
                .ConfigureAwait(false);
        }
        if (!string.Equals(capability.Id, LockMaximumCapabilityId, StringComparison.Ordinal))
        {
            return Failed(target, capability, NoDutyControl);
        }

        // 一键强冷就是核心的 boost；关掉就是交还固件。
        // **这两个是对称的两个状态，不是一个开关的两个位置** ——
        // 关掉之后风扇归固件管，我们设的东西一律不再生效，界面要能表达这一点。
        var wantsMaximum = setting.Toggle == true;
        var response = await core.SendAsync(
            wantsMaximum ? "boost" : "auto",
            new Dictionary<string, object?> { ["fan"] = fanIndex },
            cancellationToken).ConfigureAwait(false);

        if (response is not { } result || !Flag(result, "ok"))
        {
            logger?.LogWarning(
                "风扇{Action}失败：{Error}",
                wantsMaximum ? "全速" : "交还固件",
                Text(response, "error") ?? "没有应答");
            return Failed(target, capability, Text(response, "error") ?? CoreSilent);
        }

        // 回读核对：核心在应答里带回了风扇状态，直接看它到底归谁管。
        if (FanAt(result, fanIndex) is not { } fan
            || !fan.TryGetProperty("automaticControl", out var automatic)
            || automatic.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
            || automatic.GetBoolean() == wantsMaximum)
        {
            return Failed(target, capability, "下发了，但风扇的控制归属没有跟着变。");
        }

        return new ControlApplyOutcome(
            target.Id,
            capability.Id,
            ControlApplyStatuses.Applied,
            null);
    }

    /// <summary>
    /// 把曲线写给固件。
    ///
    /// **和定速不是一条路**：定速是我们替固件把风扇按在某个转速上，程序退了就得还回去；
    /// 曲线是改固件自己查的那张表，**写进去就一直算数，重启也还在**。
    /// 所以这里不需要、也不应该在退出时撤销 —— 撤销等于用户设的东西悄悄失效。
    ///
    /// 核心回的是**写完之后重新读出来的**那条曲线，不是我们发过去的那条：
    /// 温度会被夹进表能表达的范围、回差会加上去，对不上正是用户该看见的。
    /// </summary>
    private async Task<ControlApplyOutcome> WriteCurveAsync(
        ControlObject target,
        ControlCapability capability,
        ControlSetting setting,
        int fanIndex,
        CancellationToken cancellationToken)
    {
        if (setting.Curve is not { Count: > 0 } curve)
        {
            return Failed(target, capability, "这条设定里没有曲线。");
        }

        var response = await core.SendAsync(
            "setcurve",
            new Dictionary<string, object?>
            {
                ["fan"] = fanIndex,
                // 交给谁执行是用户存下来的选择。不带的话核心会按它的默认来
                // （有固件曲线就走固件），而那不一定是用户选的那条。
                ["execution"] = setting.CurveExecution,
                ["curve"] = curve
                    .Select(point => new Dictionary<string, object?>
                    {
                        ["temperatureCelsius"] = point.TemperatureCelsius,
                        ["percent"] = point.Percent
                    })
                    .ToArray()
            },
            cancellationToken).ConfigureAwait(false);

        if (response is not { } result || !Flag(result, "ok"))
        {
            var reason = Text(response, "error") ?? CoreSilent;
            logger?.LogWarning("写风扇曲线失败：{Error}", reason);
            return Failed(target, capability, reason);
        }

        // 核心把回读的曲线带回来了。读不回来说明它没真的落到表上。
        if (!result.TryGetProperty("curve", out var written)
            || written.ValueKind != JsonValueKind.Array
            || written.GetArrayLength() == 0)
        {
            return Failed(target, capability, "下发了，但从固件里读不回这条曲线。");
        }

        /*
         * 把回读到的那条记下来 —— **它就是新的当前值**。
         * 不记的话，下次有人问"现在跑的是哪条曲线"又得花十几秒重读一遍，
         * 而答案我们这一刻手里就有。
         */
        var readBack = new List<ControlCurvePoint>();
        foreach (var point in written.EnumerateArray())
        {
            if (point.TryGetProperty("temperatureCelsius", out var celsius)
                && celsius.ValueKind == JsonValueKind.Number
                && point.TryGetProperty("percent", out var percent)
                && percent.ValueKind == JsonValueKind.Number)
            {
                readBack.Add(new ControlCurvePoint(celsius.GetDouble(), percent.GetDouble()));
            }
        }
        if (readBack.Count > 0)
        {
            core.StoreCurve(fanIndex, readBack);
        }

        return new ControlApplyOutcome(
            target.Id,
            capability.Id,
            ControlApplyStatuses.Applied,
            null);
    }

    /// <summary>
    /// 风扇现在什么样。
    ///
    /// 报的是**转速**（用户真正关心的那个数），不是占空比 ——
    /// 占空比的满量程各家不一样，转速是可比的。
    /// </summary>
    public async Task<ControlActualValue?> ReadAsync(
        ControlObject target,
        ControlCapability capability,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(capability);

        if (capability.Id is not (LockMaximumCapabilityId or DutyCapabilityId or RpmCapabilityId))
        {
            return null;
        }

        var fanIndex = FanIndexOf(target);
        var reading = await core.SendAsync("read", null, cancellationToken).ConfigureAwait(false);
        if (reading is not { } result
            || !Flag(result, "ok")
            || FanAt(result, fanIndex) is not { } fan)
        {
            return new ControlActualValue(
                target.Id,
                capability.Id,
                UnreadableReason: CoreSilent);
        }

        return ProjectReading(target, capability, fan);
    }

    internal static ControlActualValue ProjectReading(ControlObject target, ControlCapability capability, JsonElement fan)
    {
        if (capability.Id == RpmCapabilityId)
        {
            return new ControlActualValue(target.Id, capability.Id, Number(fan, "rpm"), Unit: "RPM");
        }
        if (string.Equals(capability.Id, DutyCapabilityId, StringComparison.Ordinal))
        {
            // 定速报的是**占空比**，因为它设的就是占空比。
            // 转速另有去处（监控那一侧），两者不该混成一个数。
            return Number(fan, "dutyPercent") is { } duty
                ? new ControlActualValue(
                    target.Id,
                    capability.Id,
                    Number: Math.Round(duty),
                    Unit: ControlUnits.Percent)
                : new ControlActualValue(
                    target.Id,
                    capability.Id,
                    UnreadableReason: "这条通道没有报出占空比。");
        }

        // Manual ownership alone is not full speed: fixed duty and software curves also own it.
        var dutyPercent = Number(fan, "dutyPercent");
        return new ControlActualValue(
            target.Id,
            capability.Id,
            Toggle: !fan.TryGetProperty("automaticControl", out var automatic)
                || automatic.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ? null
                : automatic.GetBoolean() ? false
                : dutyPercent is { } observedDuty ? observedDuty >= 100 : null);
    }

    /// <summary>
    /// 把风扇固定在一个占空比。
    ///
    /// **越界由核心按机型夹住，而且夹了要说。** 这一层只负责把值送到，
    /// 不自己猜这台机器的下限 —— 猜低了风扇停转，猜高了白吵。
    /// </summary>
    private async Task<ControlApplyOutcome> WriteDutyAsync(
        ControlObject target,
        ControlCapability capability,
        ControlSetting setting,
        int fanIndex,
        CancellationToken cancellationToken)
    {
        if (setting.Number is not { } percent || !double.IsFinite(percent))
        {
            return Failed(target, capability, "定速要一个百分比，收到的不是。");
        }

        var response = await core.SendAsync(
            "set",
            new Dictionary<string, object?> { ["fan"] = fanIndex, ["percent"] = percent },
            cancellationToken).ConfigureAwait(false);
        if (response is not { } result || !Flag(result, "ok"))
        {
            logger?.LogWarning(
                "风扇 {Fan} 定速 {Percent}% 失败：{Error}",
                fanIndex,
                percent,
                Text(response, "error") ?? "没有应答");
            return Failed(target, capability, Text(response, "error") ?? CoreSilent);
        }

        return new ControlApplyOutcome(
            target.Id,
            capability.Id,
            ControlApplyStatuses.Applied,
            null);
    }

    private static double? Number(JsonElement? element, string name)
        => element is { } value
            && value.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.Number
                ? property.GetDouble()
                : null;

    private JsonElement? Ask(string operation)
        => core.SendAsync(operation, null, CancellationToken.None).GetAwaiter().GetResult();

    /// <summary>
    /// 第一个风扇。
    ///
    /// 控制面现在按"机身风扇"一个对象呈现，而核心可能报出好几个 ——
    /// 全速那一位在这类固件上本来就是整机共用的，所以看第一个就够。
    /// 将来要分风扇单独控制时，这里换成按对象映射。
    /// </summary>
    /// <summary>
    /// 这个对象说的是哪个风扇。编号形如 <c>fan:oem1</c>，后面那个数就是核心报的序号。
    /// 认不出来就是 0 —— 那是"只有一个风扇"的机器上唯一说得通的答案。
    /// </summary>
    private static int FanIndexOf(ControlObject target)
        => target.Id.StartsWith(ObjectIdPrefix, StringComparison.Ordinal)
            && int.TryParse(target.Id[ObjectIdPrefix.Length..], out var index)
                ? index
                : 0;

    /// <summary>
    /// 核心报的那一串风扇里序号对得上的那个。
    ///
    /// **按序号找，不是取第一个。** 先前这里取 <c>fans[0]</c>，
    /// 于是副风扇的能力、状态、回读读的全是主风扇的。
    /// </summary>
    private static JsonElement? FanAt(JsonElement? response, int index)
    {
        if (response is not { } value
            || !value.TryGetProperty("fans", out var fans)
            || fans.ValueKind != JsonValueKind.Array)
        {
            return null;
        }
        foreach (var fan in fans.EnumerateArray())
        {
            if (fan.TryGetProperty("index", out var number)
                && number.ValueKind == JsonValueKind.Number
                && number.GetInt32() == index)
            {
                return fan;
            }
        }
        return null;
    }

    private static bool Flag(JsonElement? element, string name)
        => element is { } value
            && value.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.True;

    private static string? Text(JsonElement? element, string name)
        => element is { } value
            && value.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.String
                ? property.GetString()
                : null;

    private static bool IsMine(ControlObject target, string capabilityId)
        => string.Equals(target.Kind, ControlObjectKinds.Fan, StringComparison.Ordinal)
            // 机身风扇。显卡上那个风扇对象走显卡自己的路，不归这里。
            && target.Id.StartsWith(ObjectIdPrefix, StringComparison.Ordinal)
            && capabilityId is LockMaximumCapabilityId
                or CurveCapabilityId
                or DutyCapabilityId or RpmCapabilityId;

    private static ControlApplyOutcome Failed(
        ControlObject target,
        ControlCapability capability,
        string reason)
        => new(target.Id, capability.Id, ControlApplyStatuses.Failed, reason);
}
