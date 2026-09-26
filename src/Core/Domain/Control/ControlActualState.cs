namespace ResourceManager.App.Domain.Control;

/// <summary>
/// 某一项能力**现在实际**是什么值。
///
/// 和期望状态里那条记录一样自带单位，因为前端要把两者并排显示。
/// </summary>
public sealed record ControlActualValue(
    string ObjectId,
    string CapabilityId,
    double? Number = null,
    bool? Toggle = null,
    IReadOnlyList<ControlCurvePoint>? Curve = null,
    string? Unit = null,
    /// <summary>读不到时说明为什么。读不到和"读到 0"是两回事。</summary>
    string? UnreadableReason = null);

/// <summary>
/// 实际状态：这台机器现在实际是什么样。
///
/// **它不是期望状态的回声。** 固件会按温度自己调度，用户也可能用别的软件改过，
/// 两者对不上是常态 —— 而且正是用户需要看见的信息，所以两份都要留着，
/// 不能用一份去覆盖另一份。
///
/// 这一份由转发层定期读出来，进统一订阅源，和别的读数走同一条通道。
/// </summary>
public sealed record ControlActualState(
    IReadOnlyList<ControlActualValue> Values,
    DateTimeOffset ReadAt)
{
    public static ControlActualState Empty { get; } = new([], DateTimeOffset.MinValue);
}
