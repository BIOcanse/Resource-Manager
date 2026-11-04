using ResourceManager.App.Application.RuntimeSpecialization.BusinessBlocks;

namespace ResourceManager.App.Infrastructure.RuntimeSpecialization.BusinessBlocks;

/// <summary>Block-owned diagnostics only. This registry cannot execute, cancel or schedule a block.</summary>
public sealed class BusinessBlockDiagnosticsRegistry : IBusinessBlockDiagnostics
{
    private readonly Dictionary<int, Entry> entries;
    private readonly ILogger<BusinessBlockDiagnosticsRegistry> logger;

    public BusinessBlockDiagnosticsRegistry(IEnumerable<BusinessBlockRegistration> registrations,
        ILogger<BusinessBlockDiagnosticsRegistry> logger,
        IEnumerable<BusinessBlockDiagnosticRegistration>? observations = null)
    {
        this.logger = logger;
        entries = registrations.Select(value => value.Id).Concat((observations ?? []).Select(value => value.Id))
            .Distinct().ToDictionary(id => id, _ => new Entry());
    }

    public void BeginRun(int id)
    {
        var entry = Get(id);
        lock (entry.Sync)
        {
            entry.Result = null;
            // A legacy source can already have an observed native thread when its host starts.
            if (entry.Metrics.Read().AllocationCoverageComplete) entry.Metrics.Reset();
        }
    }

    public void RecordResult(int id, BusinessBlockResult result)
    {
        var entry = Get(id);
        lock (entry.Sync) entry.Result = result with { Error = null };
        if (result.Error is { } error)
            logger.LogError(error, "Business block {BlockId}{BlockName} failed during {Stage}", id, NameSuffix(id), result.FailureStage);
        else logger.LogInformation("Business block {BlockId}{BlockName} result {Outcome}", id, NameSuffix(id), result.Outcome);
    }

    public void ReportFailure(int blockId, Exception error, string operation)
    {
        var entry = Get(blockId);
        lock (entry.Sync) entry.Result = BusinessBlockResult.Failed(error, BusinessBlockFailureStage.ReportedFailure) with { Error = null };
        logger.LogError(error, "Business block {BlockId}{BlockName} failure reported by {Operation}", blockId, NameSuffix(blockId), operation);
    }
    public IDisposable MeasureSegment(int blockId) => Get(blockId).Metrics.MeasureSegment();
    public IDisposable RegisterConsumerThread(int blockId, string sessionName) => Get(blockId).Metrics.RegisterCurrentThread(sessionName);
    public BusinessBlockDiagnostics Read(int id)
    {
        var entry = Get(id);
        lock (entry.Sync) return new(id, entry.Result, entry.Metrics.Read()) { Name = LookupName(id) };
    }
    public IReadOnlyList<BusinessBlockDiagnostics> ReadAll() => entries.Keys.Select(Read).ToArray();

    private static string? LookupName(int uid)
    {
#if RM_STATE_NAMES
        return ResourceManager.App.StateItems.StateNames.Name(uid);
#else
        return null;
#endif
    }
    private static string NameSuffix(int uid) => LookupName(uid) is { } name ? $" ({name})" : "";

    private Entry Get(int id) => entries.TryGetValue(id, out var value) ? value
        : throw new KeyNotFoundException($"Unknown business block diagnostic id: {id}");
    private sealed class Entry
    {
        public object Sync { get; } = new();
        public BusinessBlockResult? Result { get; set; }
        public BusinessBlockMetrics Metrics { get; } = new();
    }
}
