namespace ResourceManager.App.Domain.Control;

/// <summary>风扇曲线上的一个点：到这个温度时给这么大转速。</summary>
public sealed record ControlCurvePoint(double TemperatureCelsius, double Percent);

/// <summary>
/// 用户对某一项能力的设定。
///
/// 三种取值按 <see cref="ControlCapability.ValueKind"/> 三选一，只会有一个非空。
/// </summary>
public sealed record ControlSetting(
    string CapabilityId,
    double? Number = null,
    bool? Toggle = null,
    IReadOnlyList<ControlCurvePoint>? Curve = null);

/// <summary>一个对象上用户设定的全部项。</summary>
public sealed record ControlObjectDesiredState(
    string ObjectId,
    IReadOnlyList<ControlSetting> Settings);

/// <summary>
/// 期望状态：用户想让这台机器保持成什么样。
///
/// **这是持久化的，而且要反复维持。**
/// 用户设定一次之后就该一直是那样 —— 重启、显卡驱动重置、设备拔了再插回来，
/// 都要自动重新施加，而不是回到默认值，更不是再问用户一遍。
/// 所以它的所有者是后端存储，不是某个界面的临时状态。
/// </summary>
public sealed record ControlDesiredState(
    IReadOnlyList<ControlObjectDesiredState> Objects)
{
    public static ControlDesiredState Empty { get; } = new([]);

    public ControlObjectDesiredState? ForObject(string objectId)
        => Objects.FirstOrDefault(entry => string.Equals(
            entry.ObjectId,
            objectId,
            StringComparison.Ordinal));
}

/// <summary>施加一项设定之后的结果。</summary>
public static class ControlApplyStatuses
{
    /// <summary>写进去了，而且读回来对得上。</summary>
    public const string Applied = "applied";
    /// <summary>这台机器上这一项根本控不了（缺驱动/缺组件/硬件不支持）。</summary>
    public const string Unsupported = "unsupported";
    /// <summary>试了但没成功。</summary>
    public const string Failed = "failed";
}

/// <summary>
/// 一项设定的施加结果。**实际状态就是从这里来的** ——
/// 前端不自己推断有没有生效，以这份回执为准。
/// </summary>
public sealed record ControlApplyOutcome(
    string ObjectId,
    string CapabilityId,
    string Status,
    string? Message = null);

/// <summary>一次施加的整体结果。</summary>
public sealed record ControlApplyReport(
    IReadOnlyList<ControlApplyOutcome> Outcomes,
    DateTimeOffset AppliedAt)
{
    public static ControlApplyReport Empty { get; } = new([], DateTimeOffset.MinValue);
}

/// <summary>期望状态 + 最近一次施加的回执。界面要的就是这两样。</summary>
public sealed record ControlStateView(
    ControlDesiredState Desired,
    ControlApplyReport LastApply);
