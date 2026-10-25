using ResourceManager.App.Domain.Messages;
namespace ResourceManager.App.Domain.Metrics;

public sealed record MetricDefinition(
    string Id,
    string Unit,
    string? RequiredComponentId = null,
    bool Selectable = true,
    BackendMessage? DisabledReason = null,
    string? ScopeKind = null,
    string? ScopeKey = null)
{
    public MetricFieldCode FieldCode { get; init; }
    public string? FieldName { get; init; }
    public string? DeviceIdentity { get; init; }
    public string? SourceId { get; init; }
    public string ValueKind { get; init; } = "float64";
}
