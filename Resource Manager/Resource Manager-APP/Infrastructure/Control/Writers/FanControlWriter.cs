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

    private const string CoreMissing = "需要先安装风扇控制核心。";
    private const string CoreSilent = "风扇控制核心没有应答。";
    private const string NoChannel = "这台机器上还没有可用的风扇控制通道。";
    private const string NoDutyControl = "这台机器的风扇只有全速和自动两档，给不了曲线。";

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
            return ControlWriteAvailability.No(CoreMissing);
        }

        var described = Ask("describe");
        if (described is not { } description)
        {
            return ControlWriteAvailability.No(CoreSilent);
        }
        if (!Flag(description, "available"))
        {
            return ControlWriteAvailability.No(Text(description, "reason") ?? NoChannel);
        }
        if (FirstFan(description) is not { } fan)
        {
            return ControlWriteAvailability.No(NoChannel);
        }
        if (!Flag(fan, "writable"))
        {
            return ControlWriteAvailability.No("这台机器的固件没有声明支持风扇控制，只读。");
        }

        // 曲线要能给任意占空比才谈得上。只有两档的机器上如实说，不摆一条拖不动的曲线。
        return string.Equals(capability.Id, CurveCapabilityId, StringComparison.Ordinal)
            && !Flag(fan, "supportsDuty")
                ? ControlWriteAvailability.No(NoDutyControl)
                : ControlWriteAvailability.Yes();
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
            new Dictionary<string, object?> { ["fan"] = 0 },
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
        if (FirstFan(result) is { } fan
            && Flag(fan, "automaticControl") == wantsMaximum)
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

        if (!string.Equals(capability.Id, LockMaximumCapabilityId, StringComparison.Ordinal))
        {
            return null;
        }

        var reading = await core.SendAsync("read", null, cancellationToken).ConfigureAwait(false);
        if (reading is not { } result || !Flag(result, "ok") || FirstFan(result) is not { } fan)
        {
            return new ControlActualValue(
                target.Id,
                capability.Id,
                UnreadableReason: CoreSilent);
        }

        // 归固件管就是"没开强冷"。这就是那两个状态在实际侧的样子。
        return new ControlActualValue(
            target.Id,
            capability.Id,
            Toggle: !Flag(fan, "automaticControl"));
    }

    private JsonElement? Ask(string operation)
        => core.SendAsync(operation, null, CancellationToken.None).GetAwaiter().GetResult();

    /// <summary>
    /// 第一个风扇。
    ///
    /// 控制面现在按"机身风扇"一个对象呈现，而核心可能报出好几个 ——
    /// 全速那一位在这类固件上本来就是整机共用的，所以看第一个就够。
    /// 将来要分风扇单独控制时，这里换成按对象映射。
    /// </summary>
    private static JsonElement? FirstFan(JsonElement? response)
        => response is { } value
            && value.TryGetProperty("fans", out var fans)
            && fans.ValueKind == JsonValueKind.Array
            && fans.GetArrayLength() > 0
                ? fans[0]
                : null;

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
            && string.Equals(target.Id, "fan:cpu", StringComparison.Ordinal)
            && capabilityId is LockMaximumCapabilityId or CurveCapabilityId;

    private static ControlApplyOutcome Failed(
        ControlObject target,
        ControlCapability capability,
        string reason)
        => new(target.Id, capability.Id, ControlApplyStatuses.Failed, reason);
}
