using ResourceManager.ReadWrite.Native.MetricOwner;

namespace ResourceManager.ReadWrite.Windows.Metrics.Zig;

public sealed partial class ZigHardwareMetricRun
{
    /// <summary>One authority, with creator-declared stable origins and normal/admin result groups.</summary>
    public static ValueTask RunGroupedAsync(MetricOwnerConfig config,
        ReadOnlyMemory<MetricOwnerSourceBinding> origins, ReadOnlyMemory<MetricOwnerTableBinding> resultGroups,
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
            var handle = BindGrouped(config, origins.Span, resultGroups.Span, memory);
            var scope = new ZigHardwareMetricRun(handle, work, failureTransition);
            try { await run(scope, token).ConfigureAwait(false); }
            finally { await scope.CloseAsync().ConfigureAwait(false); }
        }, cancellationToken);
    }

    /// <summary>Native route metadata only; this never reads or caches a current value.</summary>
    public MetricOwnerResultRoute ReadResultRoute(uint globalMetricIndex)
    {
        gate.Wait();
        try
        {
            RequireAccepting();
            MetricOwnerException.Check(MetricOwnerAbi.ResultRoute(owner, globalMetricIndex, out var route, out var failure), failure);
            return route;
        }
        finally { gate.Release(); }
    }

    /// <summary>A source region/location change is an explicit atomic declaration plus catalog replacement.</summary>
    public async ValueTask ReplaceBoundCatalogAsync(MetricOwnerCatalogInput input,
        ReadOnlyMemory<MetricOwnerSourceBinding> origins, long utcMs, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireAccepting();
            await work.UseAsync((memory, token) =>
            {
                token.ThrowIfCancellationRequested();
                try { input.Replace(owner, memory, utcMs, origins.Span, replaceBindings: true); }
                finally { Detach(owner); }
                return ValueTask.CompletedTask;
            }, cancellationToken).ConfigureAwait(false);
            failures.Set(MetricOwnerOperation.Catalog, default);
        }
        catch (MetricOwnerException error) { failures.Set(MetricOwnerOperation.Catalog, error.Failure); throw; }
        finally { gate.Release(); }
    }

    private static unsafe nint BindGrouped(MetricOwnerConfig config, ReadOnlySpan<MetricOwnerSourceBinding> origins,
        ReadOnlySpan<MetricOwnerTableBinding> results, MetricOwnerMemory active)
    {
        fixed (MetricOwnerSourceBinding* sources = origins)
        fixed (MetricOwnerTableBinding* groups = results)
        {
            MetricOwnerException.Check(MetricOwnerAbi.BindGrouped(in config, (byte*)active.Address, active.Length,
                sources, checked((uint)origins.Length), groups, checked((uint)results.Length), out var handle, out var failure), failure);
            return handle;
        }
    }
}
