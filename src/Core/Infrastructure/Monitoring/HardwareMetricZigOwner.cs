using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.Adaptation;
using ResourceManager.ReadWrite.Native.MetricOwner;
using ResourceManager.ReadWrite.Windows.Metrics.Zig;

namespace ResourceManager.App.Infrastructure.Monitoring;

/// <summary>
/// Migration entry, deliberately absent from service registration. The caller owns
/// region bindings and keeps their mappings alive for the complete awaited run.
/// Resource identities are declared once; only RunAsync acquires active ownership.
/// </summary>
internal sealed class HardwareMetricZigOwner : IDisposable
{
    private readonly object lifetimeGate = new();
    private readonly MetricOwnerConfig config;
    private readonly IMetricOwnerMemoryResource active;
    private readonly IMetricOwnerMemoryResource work;
    private readonly ReadOnlyMemory<MetricOwnerTableBinding> sources;
    private readonly MetricOwnerTableBinding result;
    private readonly ReadOnlyMemory<MetricOwnerSourceBinding> origins;
    private readonly ReadOnlyMemory<MetricOwnerTableBinding> resultGroups;
    private readonly bool grouped;
    private readonly ILogger<HardwareMetricZigOwner> logger;
    private bool running, disposed;

    internal HardwareMetricZigOwner(ResourceManagerLedgerOwner ledger, CompiledHostManagerPlan plan,
        MetricOwnerExtents extents, MetricOwnerReaderLimits readers,
        ReadOnlyMemory<MetricOwnerTableBinding> sources, MetricOwnerTableBinding result,
        ILogger<HardwareMetricZigOwner> logger)
        : this(ledger, plan, extents, readers, sources, result, default, default, false, logger) { }

    internal HardwareMetricZigOwner(ResourceManagerLedgerOwner ledger, CompiledHostManagerPlan plan,
        MetricOwnerExtents extents, MetricOwnerReaderLimits readers,
        ReadOnlyMemory<MetricOwnerSourceBinding> origins, ReadOnlyMemory<MetricOwnerTableBinding> resultGroups,
        ILogger<HardwareMetricZigOwner> logger)
        : this(ledger, plan, extents, readers, default, default, origins, resultGroups, true, logger) { }

    private HardwareMetricZigOwner(ResourceManagerLedgerOwner ledger, CompiledHostManagerPlan plan,
        MetricOwnerExtents extents, MetricOwnerReaderLimits readers,
        ReadOnlyMemory<MetricOwnerTableBinding> sources, MetricOwnerTableBinding result,
        ReadOnlyMemory<MetricOwnerSourceBinding> origins, ReadOnlyMemory<MetricOwnerTableBinding> resultGroups,
        bool grouped, ILogger<HardwareMetricZigOwner> logger)
    {
        ArgumentNullException.ThrowIfNull(ledger); ArgumentNullException.ThrowIfNull(plan);
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        var limits = plan.MetricOwnerLimits ?? throw new InvalidOperationException("Metric owner limits are not compiled.");
        var choices = NativeMetricSnapshotHostConfiguration.CreateConfiguration(plan.MetricSnapshot);
        config = MetricOwnerConfiguration.Create(choices, limits, extents, readers);
        if ((grouped ? origins.Length : sources.Length) != extents.Sources)
            throw new ArgumentException("Source binding count differs from admitted extents.");
        if (grouped && resultGroups.Length != 2)
            throw new ArgumentException("Normal and administrator result bindings are required.", nameof(resultGroups));
        this.sources = sources; this.result = result;
        this.origins = origins; this.resultGroups = resultGroups; this.grouped = grouped;
        var sizes = ZigHardwareMetricRun.RequiredSizes(config);
        var activeLength = Math.Max(checked(sizes.Active + extents.ExtraActiveBytes), ledger.NativePageSize);
        var workLength = Math.Max(Math.Max(sizes.Work, extents.OperationWorkBytes), ledger.NativePageSize);
        if (workLength > limits.WorkByteBudget)
            throw new MetricOwnerException(new() { Code = MetricOwnerCode.CapacityExceeded,
                Family = MetricOwnerFamily.Work, Required = workLength, Limit = limits.WorkByteBudget });
        var actual = (UInt128)activeLength + workLength + extents.PublishedPositionBytes + extents.SmallOwnerBytes + extents.ReadCopyBytes;
        if (actual > choices.ResidentByteBudget)
            throw new MetricOwnerException(new() { Code = MetricOwnerCode.CapacityExceeded,
                Family = MetricOwnerFamily.Active, Required = actual > ulong.MaxValue ? ulong.MaxValue : (ulong)actual,
                Limit = choices.ResidentByteBudget });
        active = ledger.CreateMetricOwnerMemoryResource(activeLength, activeState: true);
        try { work = ledger.CreateMetricOwnerMemoryResource(workLength, activeState: false); }
        catch { active.Dispose(); throw; }
    }

    internal async ValueTask RunAsync(Func<ZigHardwareMetricRun, CancellationToken, ValueTask> run,
        CancellationToken cancellationToken = default)
    {
        lock (lifetimeGate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (running) throw new InvalidOperationException("Metric authority is already running.");
            running = true;
        }
        try
        {
            if (grouped)
                await ZigHardwareMetricRun.RunGroupedAsync(config, origins, resultGroups, active, work, run,
                    cancellationToken, failureTransition: LogFailureTransition).ConfigureAwait(false);
            else
                await ZigHardwareMetricRun.RunAsync(config, sources, result, active, work, run,
                    cancellationToken, failureTransition: LogFailureTransition).ConfigureAwait(false);
        }
        finally { lock (lifetimeGate) running = false; }
    }

    public void Dispose()
    {
        lock (lifetimeGate)
        {
            if (disposed) return;
            if (running) throw new InvalidOperationException("Await the metric authority run before disposing its identities.");
            disposed = true;
        }
        try { work.Dispose(); }
        finally { active.Dispose(); }
    }

    private void LogFailureTransition(MetricOwnerFailureTransition transition)
    {
        if (transition.Recovered)
            logger.LogInformation("Hardware metric {Operation} recovered from {Code}/{Family}.",
                transition.Operation, transition.Failure.Code, transition.Failure.Family);
        else
            logger.LogWarning("Hardware metric {Operation} failed: {Code}/{Family}, required {Required}, limit {Limit}.",
                transition.Operation, transition.Failure.Code, transition.Failure.Family,
                transition.Failure.Required, transition.Failure.Limit);
    }
}
