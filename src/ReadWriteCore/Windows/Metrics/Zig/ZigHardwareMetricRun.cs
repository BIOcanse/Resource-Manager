using ResourceManager.ReadWrite.Native.MetricOwner;

namespace ResourceManager.ReadWrite.Windows.Metrics.Zig;

/// <summary>
/// The inactive migration entry. The service does not construct or bind this run
/// until its shared region owner is delivered. No old/new product-path switch.
/// </summary>
public sealed partial class ZigHardwareMetricRun
{
    private nint owner;
    private readonly IMetricOwnerMemoryResource work;
    private readonly MetricOwnerFailureTransitions failures;
    private readonly SemaphoreSlim gate = new(1, 1);
    private int accepting = 1;

    private ZigHardwareMetricRun(nint owner, IMetricOwnerMemoryResource work, Action<MetricOwnerFailureTransition>? failureTransition)
    { this.owner = owner; this.work = work; failures = new(failureTransition); }

    public static MetricOwnerSizes RequiredSizes(MetricOwnerConfig config)
    {
        MetricOwnerException.Check(MetricOwnerAbi.RequiredSizes(in config, out var sizes, out var failure), failure);
        return sizes;
    }

    /// <summary>
    /// Active ownership is local to this awaited run. It keeps catalog/baseline
    /// continuity while actual readers/work use the owner; callback exit ends
    /// admission, drains operations and unbinds before the provider returns its lease.
    /// Providers are stable identities and remain caller-owned.
    /// </summary>
    public static ValueTask RunAsync(MetricOwnerConfig config,
        ReadOnlyMemory<MetricOwnerTableBinding> sourceBindings, MetricOwnerTableBinding resultBinding,
        IMetricOwnerMemoryResource active, IMetricOwnerMemoryResource work,
        Func<ZigHardwareMetricRun, CancellationToken, ValueTask> run,
        CancellationToken cancellationToken = default, Action<MetricOwnerFailureTransition>? failureTransition = null)
    {
        ArgumentNullException.ThrowIfNull(active); ArgumentNullException.ThrowIfNull(work);
        ArgumentNullException.ThrowIfNull(run);
        var sizes = RequiredSizes(config);
        RequireCapacity(active.Length, sizes.Active, MetricOwnerFamily.Active);
        RequireCapacity(work.Length, sizes.Work, MetricOwnerFamily.Work);
        return active.UseAsync(async (memory, token) =>
        {
            var handle = Bind(config, sourceBindings.Span, resultBinding, memory);
            var scope = new ZigHardwareMetricRun(handle, work, failureTransition);
            try { await run(scope, token).ConfigureAwait(false); }
            finally { await scope.CloseAsync().ConfigureAwait(false); }
        }, cancellationToken);
    }

    public async ValueTask ReplaceCatalogAsync(MetricOwnerCatalogInput input, long utcMs,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireAccepting();
            await work.UseAsync((memory, token) =>
            {
                token.ThrowIfCancellationRequested();
                try { input.Replace(owner, memory, utcMs); }
                finally { Detach(owner); }
                return ValueTask.CompletedTask;
            }, cancellationToken).ConfigureAwait(false);
            failures.Set(MetricOwnerOperation.Catalog, default);
        }
        catch (MetricOwnerException error) { failures.Set(MetricOwnerOperation.Catalog, error.Failure); throw; }
        finally { gate.Release(); }
    }

    /// <summary>One local work lease spans plan, sampler, prepare, admission and native detach.</summary>
    public async ValueTask<bool> ExecuteAsync(ReadOnlyMemory<uint> metricIndices,
        IMetricOwnerRawSampler sampler, MetricOwnerPublicationPermit permit, long utcMs,
        CancellationToken cancellationToken = default, MetricOwnerPublished? onPublished = null,
        MetricOwnerHistoryBinding? history = null, ReadOnlyMemory<MetricOwnerHistoryBinding> historyGroups = default,
        ReadOnlyMemory<MetricOwnerMixedHistoryBinding> mixedHistory = default)
    {
        ArgumentNullException.ThrowIfNull(sampler); ArgumentNullException.ThrowIfNull(permit);
        if (history.HasValue && !historyGroups.IsEmpty) throw new ArgumentException("Supply one history binding form.");
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var published = false;
        try
        {
            RequireAccepting();
            await work.UseAsync(async (memory, token) =>
            {
                MetricOwnerRawRound? query = null;
                var drained = false;
                try
                {
                    query = Plan(owner, metricIndices.Span, memory, utcMs);
                    await sampler.SampleAsync(query, token).ConfigureAwait(false);
                    try { await sampler.DrainAsync().ConfigureAwait(false); }
                    finally { drained = true; } // Drain faults also guarantee that borrowed writes have ended.
                    query.Freeze();
                    token.ThrowIfCancellationRequested();
                    MetricOwnerException.Check(MetricOwnerAbi.Prepare(owner, query.Token, out var failure), failure);
                    var receipt = BorrowReceipt(owner, query.Token);
                    var committed = false;
                    var admitted = permit(() =>
                    {
                        if (committed) throw new InvalidOperationException("Publication permission invoked commit twice.");
                        token.ThrowIfCancellationRequested(); RequireAccepting();
                        Commit(owner, query.Token, metricIndices.Span, history, historyGroups.Span, utcMs, mixedHistory.Span);
                        committed = true;
                    });
                    if (admitted != committed) throw new InvalidOperationException("Publication permission returned an inconsistent admission result.");
                    published = committed;
                    if (committed && onPublished is not null) Notify(onPublished, query, receipt);
                }
                finally
                {
                    // Even a failed sample/interop remains in this scope through drain.
                    // Sampler's contract forbids a return (including fault) while writes survive.
                    try { if (!drained) await sampler.DrainAsync().ConfigureAwait(false); }
                    finally
                    {
                        try { Detach(owner); }
                        finally { query?.Close(); }
                    }
                }
            }, cancellationToken).ConfigureAwait(false);
            failures.Set(MetricOwnerOperation.Round, default);
            return published;
        }
        catch (MetricOwnerException error) { failures.Set(MetricOwnerOperation.Round, error.Failure); throw; }
        finally { gate.Release(); }
    }

    public MetricOwnerDiagnostics ReadDiagnostics()
    {
        gate.Wait();
        try
        {
            RequireAccepting();
            MetricOwnerException.Check(MetricOwnerAbi.Diagnostics(owner, out var diagnostics), default);
            return diagnostics;
        }
        finally { gate.Release(); }
    }

    private async ValueTask CloseAsync()
    {
        Volatile.Write(ref accepting, 0);
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (owner == 0) return;
            var utcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            try
            {
                try { Detach(owner); }
                finally
                {
                    try { BeforeUnbind(utcMs); }
                    finally { MetricOwnerException.Check(MetricOwnerAbi.UnbindAt(owner, utcMs, out var failure), failure); }
                }
            }
            finally { owner = 0; }
        }
        finally { gate.Release(); }
    }

    private void RequireAccepting() => ObjectDisposedException.ThrowIf(Volatile.Read(ref accepting) == 0, this);
    private void BeforeUnbind(long utcMs) => CloseDiscovery();
    private static void RequireCapacity(ulong actual, ulong required, MetricOwnerFamily family)
    {
        if (actual < required) throw new MetricOwnerException(new() { Code = MetricOwnerCode.CapacityExceeded,
            Family = family, Required = required, Limit = actual });
    }
    private static void Detach(nint owner)
        => MetricOwnerException.Check(MetricOwnerAbi.AbortDetach(owner, out var failure), failure);
    private static unsafe nint Bind(MetricOwnerConfig config, ReadOnlySpan<MetricOwnerTableBinding> sources,
        MetricOwnerTableBinding result, MetricOwnerMemory active)
    {
        fixed (MetricOwnerTableBinding* bindings = sources)
        {
            MetricOwnerException.Check(MetricOwnerAbi.Bind(in config, (byte*)active.Address, active.Length,
                bindings, checked((uint)sources.Length), in result, out var owner, out var failure), failure);
            return owner;
        }
    }
    private static unsafe MetricOwnerRawRound Plan(nint owner, ReadOnlySpan<uint> metrics,
        MetricOwnerMemory work, long utcMs)
    {
        fixed (uint* indices = metrics)
        {
            MetricOwnerException.Check(MetricOwnerAbi.Plan(owner, indices, checked((uint)metrics.Length),
                (byte*)work.Address, work.Length, utcMs, out var round, out var failure), failure);
            return new(round);
        }
    }

    private static unsafe nint BorrowReceipt(nint owner, ulong token)
    {
        MetricOwnerException.Check(MetricOwnerAbi.ReceiptView(owner, token, out var receipt, out var failure), failure);
        return (nint)receipt;
    }
    private static unsafe void Commit(nint owner, ulong token, ReadOnlySpan<uint> completed,
        MetricOwnerHistoryBinding? history, ReadOnlySpan<MetricOwnerHistoryBinding> historyGroups, long utcMs,
        ReadOnlySpan<MetricOwnerMixedHistoryBinding> mixedHistory = default)
    {
        if (!mixedHistory.IsEmpty)
        {
            // Native admits every kernel/vector/history writer before the one
            // commit. There is no follow-up append or second publication.
            Span<MetricOwnerHistoryBinding> single = stackalloc MetricOwnerHistoryBinding[1];
            if (history is { } scalar) single[0] = scalar;
            ReadOnlySpan<MetricOwnerHistoryBinding> scalars = history.HasValue ? single : historyGroups;
            fixed (MetricOwnerHistoryBinding* scalarBindings = scalars)
            fixed (MetricOwnerMixedHistoryBinding* mixedBindings = mixedHistory)
            fixed (uint* indices = completed)
                MetricOwnerException.Check(MetricOwnerSupportAbi.HistoryCommitMixed(owner, scalarBindings,
                    checked((uint)scalars.Length), mixedBindings, checked((uint)mixedHistory.Length), indices,
                    checked((uint)completed.Length), token, utcMs, out var failure), failure);
            return;
        }
        if (!historyGroups.IsEmpty)
        {
            fixed (MetricOwnerHistoryBinding* bindings = historyGroups)
            fixed (uint* indices = completed)
                MetricOwnerException.Check(MetricOwnerSupportAbi.HistoryCommitGrouped(owner, bindings,
                    checked((uint)historyGroups.Length), indices, checked((uint)completed.Length), token, utcMs, out var failure), failure);
            return;
        }
        if (history is not { } binding)
        {
            MetricOwnerException.Check(MetricOwnerAbi.Commit(owner, token, utcMs, out var failure), failure);
            return;
        }
        fixed (uint* indices = completed)
            MetricOwnerException.Check(MetricOwnerSupportAbi.HistoryCommit(owner, in binding, indices,
                checked((uint)completed.Length), token, utcMs, out var failure), failure);
    }
    private static void Notify(MetricOwnerPublished notify, MetricOwnerRawRound query, nint nativeReceipt)
    {
        var receipt = new MetricOwnerPublication(query, nativeReceipt);
        notify(in receipt);
    }
}
