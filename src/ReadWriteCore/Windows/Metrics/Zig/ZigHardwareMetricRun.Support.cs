using ResourceManager.ReadWrite.Native.MetricOwner;

namespace ResourceManager.ReadWrite.Windows.Metrics.Zig;

public sealed partial class ZigHardwareMetricRun
{
    /// <summary>Addresses and requirements borrow only this configuration call's work scope.</summary>
    public async ValueTask ConfigureMixedHistoryAsync(MetricOwnerMixedHistoryBinding binding,
        ReadOnlyMemory<MetricOwnerMixedHistoryRequirement> requirements, ReadOnlyMemory<byte> utf8,
        long utcMs, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireAccepting();
            await work.UseAsync((memory, token) =>
            {
                token.ThrowIfCancellationRequested();
                try { ConfigureMixedHistory(owner, binding, requirements.Span, utf8.Span, memory, utcMs); }
                finally { Detach(owner); }
                return ValueTask.CompletedTask;
            }, cancellationToken).ConfigureAwait(false);
            failures.Set(MetricOwnerOperation.History, default);
        }
        catch (MetricOwnerException error) { failures.Set(MetricOwnerOperation.History, error.Failure); throw; }
        finally { gate.Release(); }
    }

    public async ValueTask ConfigureHistoryAsync(MetricOwnerHistoryBinding binding,
        ReadOnlyMemory<MetricOwnerHistoryRequirement> requirements, long utcMs,
        CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireAccepting();
            await work.UseAsync((memory, token) =>
            {
                token.ThrowIfCancellationRequested();
                try { ConfigureHistory(owner, binding, requirements.Span, memory, utcMs); }
                finally { Detach(owner); }
                return ValueTask.CompletedTask;
            }, cancellationToken).ConfigureAwait(false);
            failures.Set(MetricOwnerOperation.History, default);
        }
        catch (MetricOwnerException error) { failures.Set(MetricOwnerOperation.History, error.Failure); throw; }
        finally { gate.Release(); }
    }

    /// <summary>Persistence is an explicit copy boundary. Destination ownership stays local through consumption.</summary>
    public async ValueTask ExportCheckpointAsync(IMetricOwnerMemoryResource destination,
        Func<MetricOwnerMemory, ulong, CancellationToken, ValueTask> consume,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination); ArgumentNullException.ThrowIfNull(consume);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireAccepting();
            await destination.UseAsync(async (memory, token) =>
            {
                token.ThrowIfCancellationRequested();
                var written = EncodeCheckpoint(owner, memory);
                await consume(memory, written, token).ConfigureAwait(false);
            }, cancellationToken).ConfigureAwait(false);
            failures.Set(MetricOwnerOperation.CheckpointExport, default);
        }
        catch (MetricOwnerException error) { failures.Set(MetricOwnerOperation.CheckpointExport, error.Failure); throw; }
        finally { gate.Release(); }
    }

    /// <summary>Prepare and admitted publish share exactly one local work lease.</summary>
    public async ValueTask<bool> RestoreCheckpointAsync(ReadOnlyMemory<byte> image,
        MetricOwnerPublicationPermit permit, long utcMs, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(permit);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var published = false;
        try
        {
            RequireAccepting();
            await work.UseAsync((memory, token) =>
            {
                MetricOwnerCheckpointPrepared prepared = default;
                var acceptingPublish = true;
                try
                {
                    token.ThrowIfCancellationRequested();
                    prepared = PrepareCheckpoint(owner, image.Span, memory);
                    var admitted = permit(() =>
                    {
                        if (!acceptingPublish || published) throw new InvalidOperationException("Checkpoint permission is expired or repeated.");
                        token.ThrowIfCancellationRequested(); RequireAccepting();
                        MetricOwnerException.Check(MetricOwnerSupportAbi.CheckpointPublish(owner, ref prepared, utcMs, out var failure), failure);
                        published = true;
                    });
                    if (admitted != published) throw new InvalidOperationException("Checkpoint permission returned an inconsistent result.");
                }
                finally { acceptingPublish = false; prepared = default; Detach(owner); }
                return ValueTask.CompletedTask;
            }, cancellationToken).ConfigureAwait(false);
            failures.Set(MetricOwnerOperation.CheckpointRestore, default);
            return published;
        }
        catch (MetricOwnerException error) { failures.Set(MetricOwnerOperation.CheckpointRestore, error.Failure); throw; }
        finally { gate.Release(); }
    }

    private static unsafe void ConfigureMixedHistory(nint owner, MetricOwnerMixedHistoryBinding binding,
        ReadOnlySpan<MetricOwnerMixedHistoryRequirement> requirements, ReadOnlySpan<byte> utf8,
        MetricOwnerMemory work, long utcMs)
    {
        fixed (MetricOwnerMixedHistoryRequirement* input = requirements)
        fixed (byte* bytes = utf8)
        {
            var text = new MetricVectorText { Bytes = bytes, Length = checked((uint)utf8.Length) };
            MetricOwnerException.Check(MetricOwnerSupportAbi.HistoryConfigureMixed(owner, in binding, input,
                checked((uint)requirements.Length), in text, (byte*)work.Address, work.Length, utcMs, out var failure), failure);
        }
    }
    private static unsafe void ConfigureHistory(nint owner, MetricOwnerHistoryBinding binding,
        ReadOnlySpan<MetricOwnerHistoryRequirement> requirements, MetricOwnerMemory work, long utcMs)
    {
        fixed (MetricOwnerHistoryRequirement* input = requirements)
            MetricOwnerException.Check(MetricOwnerSupportAbi.HistoryConfigure(owner, in binding, input,
                checked((uint)requirements.Length), (byte*)work.Address, work.Length, utcMs, out var failure), failure);
    }
    private static unsafe ulong EncodeCheckpoint(nint owner, MetricOwnerMemory destination)
    {
        MetricOwnerException.Check(MetricOwnerSupportAbi.CheckpointEncode(owner, (byte*)destination.Address,
            destination.Length, out var written, out var failure), failure);
        return written;
    }
    private static unsafe MetricOwnerCheckpointPrepared PrepareCheckpoint(nint owner, ReadOnlySpan<byte> image, MetricOwnerMemory work)
    {
        fixed (byte* bytes = image)
        {
            MetricOwnerException.Check(MetricOwnerSupportAbi.CheckpointPrepare(owner, bytes,
                checked((ulong)image.Length), (byte*)work.Address, work.Length, out var prepared, out var failure), failure);
            return prepared;
        }
    }
}
