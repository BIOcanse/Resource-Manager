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
    string? DisabledReason = null,
    string? ScopeKind = null,
    string? ScopeKey = null);

public sealed record MetricValue(
    string Id,
    string Label,
    string Group,
    string DisplayValue,
    double? NumericValue,
    string Unit,
    double? Percent,
    string? Detail);
