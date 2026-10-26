using ResourceManager.App.Application.RuntimeSpecialization.BusinessBlocks;

namespace ResourceManager.App.Infrastructure.RuntimeSpecialization.BusinessBlocks;

/// <summary>Block-owned diagnostics only. This registry cannot execute, cancel or schedule a block.</summary>
public sealed class BusinessBlockDiagnosticsRegistry : IBusinessBlockDiagnostics
{
    private readonly Dictionary<string, Entry> entries;
    private readonly ILogger<BusinessBlockDiagnosticsRegistry> logger;

    public BusinessBlockDiagnosticsRegistry(IEnumerable<BusinessBlockRegistration> registrations,
        ILogger<BusinessBlockDiagnosticsRegistry> logger,
        IEnumerable<BusinessBlockDiagnosticRegistration>? observations = null)
    {
        this.logger = logger;
        entries = registrations.Select(value => value.Id).Concat((observations ?? []).Select(value => value.Id))
            .Distinct(StringComparer.Ordinal).ToDictionary(id => id, _ => new Entry(), StringComparer.Ordinal);
    }

    public void BeginRun(string id)
    {
        var entry = Get(id);
        lock (entry.Sync)
        {
            entry.Result = null;
            // A legacy source can already have an observed native thread when its host starts.
            if (entry.Metrics.Read().AllocationCoverageComplete) entry.Metrics.Reset();
        }
    }

    public void RecordResult(string id, BusinessBlockResult result)
    {
        var entry = Get(id);
        lock (entry.Sync) entry.Result = result with { Error = null };
        if (result.Error is { } error)
            logger.LogError(error, "Business block {BlockId} failed during {Stage}", id, result.FailureStage);
        else logger.LogInformation("Business block {BlockId} result {Outcome}", id, result.Outcome);
    }

    public void ReportFailure(string blockId, Exception error, string operation)
    {
        var entry = Get(blockId);
        lock (entry.Sync) entry.Result = BusinessBlockResult.Failed(error, BusinessBlockFailureStage.ReportedFailure) with { Error = null };
        logger.LogError(error, "Business block {BlockId} failure reported by {Operation}", blockId, operation);
    }
    public IDisposable MeasureSegment(string blockId) => Get(blockId).Metrics.MeasureSegment();
    public IDisposable RegisterConsumerThread(string blockId, string sessionName) => Get(blockId).Metrics.RegisterCurrentThread(sessionName);
    public BusinessBlockDiagnostics Read(string id)
    {
        var entry = Get(id);
        lock (entry.Sync) return new(id, entry.Result, entry.Metrics.Read());
    }
    public IReadOnlyList<BusinessBlockDiagnostics> ReadAll() => entries.Keys.Select(Read).ToArray();

    private Entry Get(string id) => entries.TryGetValue(id, out var value) ? value
        : throw new KeyNotFoundException($"Unknown business block diagnostic id: {id}");
    private sealed class Entry
    {
        public object Sync { get; } = new();
        public BusinessBlockResult? Result { get; set; }
        public BusinessBlockMetrics Metrics { get; } = new();
    }
}
