using ResourceManager.ReadWrite.Native.MetricOwner;

namespace ResourceManager.ReadWrite.Windows.Metrics.Zig;

public unsafe delegate void MetricDiscoveryFullMeasureAccept(in MetricDiscoveryFacts facts, in MetricDiscoveryLimits limits,
    in MetricDiscoveryRoles roles, in MetricVectorScopeTopology topology, ulong inputPrefix);
public delegate void MetricDiscoveryFullMeasureFill(MetricOwnerMemory memory, MetricDiscoveryFullMeasureAccept accept);
public unsafe delegate void MetricDiscoveryFullCatalogAccept(in MetricDiscoveryFacts facts, in MetricDiscoveryLimits limits,
    in MetricVectorScopeTopology topology, MetricDiscoveryProviderInputs* initial, ulong inputPrefix);
public delegate void MetricDiscoveryFullCatalogFill(MetricOwnerMemory memory, MetricDiscoveryFullCatalogAccept accept);

/// <summary>Owner-specific raw topology input layout in a caller's existing local workspace.</summary>
public unsafe ref struct MetricDiscoveryTopologyInput
{
    public MetricVectorScopeTopology Native;
    public ulong InputPrefixBytes;
    public static MetricDiscoveryTopologyInput Create(MetricOwnerMemory memory, uint actualScopes, uint admittedTextBytes)
    {
        MetricOwnerException.Check(MetricOwnerDiscoveryAbi.RuntimeTopologyInput(actualScopes, admittedTextBytes, (byte*)memory.Address,
            memory.Length, out var native, out var prefix, out var failure), failure);
        return new() { Native = native, InputPrefixBytes = prefix };
    }
}

public sealed partial class ZigHardwareMetricRun
{
    public static async ValueTask<MetricDiscoveryFullMeasurement> MeasureDiscoveryFullAsync(IMetricOwnerMemoryResource workspace,
        MetricDiscoveryFullMeasureFill fill, ulong learnedTextBytes, ulong canonicalReserve = 0, ulong inputBytes = 0,
        MetricDiscoveryVectorAdmission? vector = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace); ArgumentNullException.ThrowIfNull(fill);
        MetricDiscoveryFullMeasurement measured = default;
        await workspace.UseAsync((memory, token) =>
        {
            token.ThrowIfCancellationRequested(); measured = MeasureFull(fill, memory, learnedTextBytes, canonicalReserve, inputBytes, vector);
            return ValueTask.CompletedTask;
        }, cancellationToken).ConfigureAwait(false);
        return measured;
    }
    private static unsafe MetricDiscoveryFullMeasurement MeasureFull(MetricDiscoveryFullMeasureFill fill, MetricOwnerMemory memory,
        ulong learned, ulong canonical, ulong input, MetricDiscoveryVectorAdmission? vector)
    {
        var acceptingInputs = true; var invoked = false; var borrowed = memory; var admission = vector.GetValueOrDefault();
        MetricDiscoveryFullMeasurement result = default;
        try
        {
            fill(memory, (in MetricDiscoveryFacts facts, in MetricDiscoveryLimits limits, in MetricDiscoveryRoles roles, in MetricVectorScopeTopology topology, ulong prefix) =>
            {
                if (!acceptingInputs || invoked) throw new InvalidOperationException("Full discovery measurement callback expired or repeated."); invoked = true;
                var local = admission;
                MetricOwnerException.Check(MetricOwnerDiscoveryAbi.RuntimeMeasureFull(in facts, in limits, in roles, in topology, vector.HasValue ? &local : null,
                    (byte*)borrowed.Address, borrowed.Length, prefix, learned, canonical, input, out result, out var failure), failure);
            });
            if (!invoked) throw new InvalidOperationException("Full discovery measurement supplied no facts.");
            return result;
        }
        finally { acceptingInputs = false; borrowed = default; admission = default; }
    }
    public unsafe void AttachDiscoveryFull(MetricDiscoveryFullSizes sizes, ReadOnlySpan<MetricDiscoverySourceRole> sourceRoles,
        MetricDiscoveryInventoryBinding? inventory, MetricDiscoveryVectorAdmission? vector, ulong transientUnavailableMs)
    {
        gate.Wait();
        try
        {
            RequireAccepting();
            if (discoveryContext != 0) throw new InvalidOperationException("Discovery is already attached.");
            fixed (MetricDiscoverySourceRole* rows = sourceRoles)
            {
                var roles = new MetricDiscoveryRoles { Count = checked((uint)sourceRoles.Length), Rows = rows };
                var inventoryLocal = inventory.GetValueOrDefault(); var vectorLocal = vector.GetValueOrDefault();
                MetricOwnerException.Check(MetricOwnerDiscoveryAbi.RuntimeAttachFull(owner, in sizes, in roles, inventory.HasValue ? &inventoryLocal : null,
                    vector.HasValue ? &vectorLocal : null, transientUnavailableMs, out discoveryContext, out var failure), failure);
            }
        }
        finally { gate.Release(); }
    }
    public async ValueTask ReplaceDiscoveryCatalogFullAsync(MetricDiscoveryFullCatalogFill fill, long utcMs,
        CancellationToken cancellationToken = default, MetricDiscoveryCatalogBinding? catalog = null)
    {
        ArgumentNullException.ThrowIfNull(fill); await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var context = RequireDiscovery();
            await work.UseAsync((memory, token) =>
            {
                token.ThrowIfCancellationRequested();
                try { ReplaceFull(context, fill, memory, catalog, utcMs); }
                finally { MetricOwnerDiscoveryAbi.RuntimeDetach(context); }
                return ValueTask.CompletedTask;
            }, cancellationToken).ConfigureAwait(false);
            failures.Set(MetricOwnerOperation.Catalog, default);
        }
        catch (MetricOwnerException error) { failures.Set(MetricOwnerOperation.Catalog, error.Failure); throw; }
        finally { gate.Release(); }
    }
    private static unsafe void ReplaceFull(nint context, MetricDiscoveryFullCatalogFill fill, MetricOwnerMemory memory,
        MetricDiscoveryCatalogBinding? catalog, long utc)
    {
        var acceptingInputs = true; var invoked = false; var borrowed = memory; var binding = catalog.GetValueOrDefault();
        try
        {
            fill(memory, (in MetricDiscoveryFacts facts, in MetricDiscoveryLimits limits, in MetricVectorScopeTopology topology, MetricDiscoveryProviderInputs* initial, ulong prefix) =>
            {
                if (!acceptingInputs || invoked) throw new InvalidOperationException("Full discovery catalog callback expired or repeated."); invoked = true;
                var local = binding;
                MetricOwnerException.Check(MetricOwnerDiscoveryAbi.RuntimeReplaceFull(context, in facts, in limits, in topology, initial, catalog.HasValue ? &local : null,
                    (byte*)borrowed.Address, borrowed.Length, prefix, utc, out var failure), failure);
            });
            if (!invoked) throw new InvalidOperationException("Full discovery catalog supplied no facts.");
        }
        finally { acceptingInputs = false; borrowed = default; binding = default; }
    }
}
