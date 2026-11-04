namespace ResourceManager.App.Domain.Diagnostics;

public sealed record DebugDiagnosticLogRecord(
    DateTimeOffset Timestamp,
    string Category,
    string EventName,
    long RuntimePlanVersion,
    IReadOnlyDictionary<string, object?> Properties);
