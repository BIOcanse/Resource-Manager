using ResourceManager.App.Domain.Messages;
using ResourceManager.ReadWrite;
namespace ResourceManager.App.Domain.Metrics;

public sealed record MetricDefinition(
    string Id,
    string Unit,
    ReadVolatility Volatility,
    string? RequiredComponentId = null,
    bool Selectable = true,
    BackendMessage? DisabledReason = null,
    string? ScopeKind = null,
    string? ScopeKey = null)
{
    public bool? HasDedicatedMemoryMetrics { get; init; }
    public MetricFieldCode FieldCode { get; init; }
    public string? FieldName { get; init; }
    public string? DeviceIdentity { get; init; }
    public string? SourceId { get; init; }
    public string ValueKind { get; init; } = "float64";
    public IReadOnlyList<MetricSourceCandidate> CandidateSources { get; init; } = [];
}

public sealed record MetricSourceCandidate(string FieldName, string DeviceIdentity, string SourceId,
    MetricReadingSource? Reading, string SourceAvailability, bool IsSelected);
