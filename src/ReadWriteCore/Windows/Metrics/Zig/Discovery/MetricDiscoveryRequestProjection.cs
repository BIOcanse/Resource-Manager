using ResourceManager.ReadWrite.Native.MetricOwner;

namespace ResourceManager.ReadWrite.Windows.Metrics.Zig;

public delegate void MetricDiscoveryProjected(in MetricDiscoveryProjection projection);

internal sealed class MetricDiscoveryProjectionBorrow
{
    internal MetricDiscoveryRequestProjection Native;
    private bool closed;
    internal void Open() => ObjectDisposedException.ThrowIf(closed, this);
    internal void Close() { closed = true; Native = default; }
}
/// <summary>Native-selected request shape; no sampled values or managed filtering.</summary>
public readonly unsafe ref struct MetricDiscoveryProjection
{
    private readonly MetricDiscoveryProjectionBorrow borrow;
    internal MetricDiscoveryProjection(MetricDiscoveryProjectionBorrow borrow) => this.borrow = borrow;
    private ref readonly MetricDiscoveryRequestProjection Native { get { borrow.Open(); return ref borrow.Native; } }
    public MetricVectorBinding Binding => Native.Binding;
    public ulong SourceId => Native.SourceId;
    public ulong CatalogGeneration => Native.CatalogGeneration;
    public ulong MembershipRevision => Native.MembershipRevision;
    public ulong SelectionRevision => Native.SelectionRevision;
    public ReadOnlySpan<uint> SelectedRows => new(Native.SelectedRows, checked((int)Native.SelectedCount));
    public ReadOnlySpan<ulong> ExpectedIdentities => new(Native.ExpectedIdentities, checked((int)Native.SelectedCount));
    public ReadOnlySpan<uint> BaseMetricIndices => new(Native.BaseMetricIndices, checked((int)Native.BaseCount));
    public ReadOnlySpan<ulong> BaseMetricIds => new(Native.BaseMetricIds, checked((int)Native.BaseCount));
    public ReadOnlySpan<uint> SuppressedMetricIndices => new(Native.SuppressedMetricIndices, checked((int)Native.SuppressedCount));
    public ReadOnlySpan<ulong> SuppressedMetricIds => new(Native.SuppressedMetricIds, checked((int)Native.SuppressedCount));
}

public sealed partial class ZigHardwareMetricRun
{
    public async ValueTask ProjectDiscoveryRequestAsync(MetricDiscoveryRequestFill fill, MetricDiscoveryProjected consume,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fill); ArgumentNullException.ThrowIfNull(consume);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var context = RequireDiscovery();
            await work.UseAsync((memory, token) =>
            {
                token.ThrowIfCancellationRequested(); ProjectDiscovery(context, fill, consume, memory);
                return ValueTask.CompletedTask;
            }, cancellationToken).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }
    private static unsafe void ProjectDiscovery(nint context, MetricDiscoveryRequestFill fill, MetricDiscoveryProjected consume, MetricOwnerMemory memory)
    {
        var invoked = false; var accepting = true; var borrowedMemory = memory; var borrow = new MetricDiscoveryProjectionBorrow();
        try
        {
            fill(memory, (in MetricDiscoveryRequest request, ulong prefix) =>
            {
                if (!accepting || invoked) throw new InvalidOperationException("Projection input callback expired or repeated."); invoked = true;
                MetricOwnerException.Check(MetricOwnerDiscoveryAbi.RuntimeProjectRequest(context, in request, (byte*)borrowedMemory.Address,
                    borrowedMemory.Length, prefix, out borrow.Native, out var failure), failure);
                var view = new MetricDiscoveryProjection(borrow); consume(in view);
            });
            if (!invoked) throw new InvalidOperationException("Projection supplied no request.");
        }
        finally { accepting = false; borrowedMemory = default; borrow.Close(); }
    }
}
