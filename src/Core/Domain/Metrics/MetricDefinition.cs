using ResourceManager.App.Domain.Messages;
namespace ResourceManager.App.Domain.Metrics;

public sealed record MetricDefinition(
    string Id,
    string Label,
    string Group,
    string Unit,
    string PreferredSlot,
    string? Detail = null,
    string? RequiredComponentId = null,
    string? RequiredComponentName = null,
    bool Selectable = true,
    BackendMessage? DisabledReason = null,
    string? ScopeKind = null,
    string? ScopeKey = null);

public sealed record MetricValue(
    string Id,
    string Label,
    string Group,
    // 后端已经拼好的一句显示值。容量类指标不再走这里：它们的 Unit 是 MetricUnits.Bytes，
    // NumericValue 是原始字节，换算、跳档和单位标签一律由前端负责。
    string DisplayValue,
    double? NumericValue,
    string Unit,
    double? Percent,
    string? Detail,
    // 和 NumericValue 同单位的总量。只有存在有意义总量的指标才有（内存、虚拟内存、显存）。
    double? Total = null);

public static class MetricUnits
{
    /// <summary>原始字节。数值不做任何换算，单位标签由前端按用户选择的进制给出。</summary>
    public const string Bytes = "B";

    public const string Percent = "%";
}
