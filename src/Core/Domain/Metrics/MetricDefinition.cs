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
