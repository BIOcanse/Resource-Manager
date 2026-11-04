using ResourceManager.ReadWrite.Native.MetricOwner;
using ResourceManager.ReadWrite.Windows.Metrics.Zig.Discovery;

namespace ResourceManager.ReadWrite.Windows.Metrics.Zig;

// Fill invokes accept exactly once while its fixed/native-input lifetime is
// still valid. Work-backed inputs occupy InputPrefixBytes, which native skips.
public unsafe delegate void MetricDiscoveryCatalogAccept(in MetricDiscoveryFacts facts, in MetricDiscoveryLimits limits,
    MetricDiscoveryProviderInputs* initialProviders, ulong inputPrefixBytes);
public delegate void MetricDiscoveryCatalogFill(MetricOwnerMemory memory, MetricDiscoveryCatalogAccept accept);
public unsafe delegate void MetricDiscoveryRoleCatalogAccept(in MetricDiscoveryFacts facts, in MetricDiscoveryLimits limits,
    in MetricDiscoveryRoles roles, ulong inputPrefixBytes);
public delegate void MetricDiscoveryRoleCatalogFill(MetricOwnerMemory memory, MetricDiscoveryRoleCatalogAccept accept);
public unsafe delegate void MetricDiscoveryRequestAccept(in MetricDiscoveryRequest request, ulong inputPrefixBytes);
public delegate void MetricDiscoveryRequestFill(MetricOwnerMemory memory, MetricDiscoveryRequestAccept accept);
public interface IMetricOwnerDiscoverySampler
{
    // Drain finishes or faults only after all borrowed SDK writes/callbacks
    // have ended. Preparation follows Drain, including cancellation/errors.
    ValueTask SampleAsync(MetricOwnerRawRound raw, MetricDiscoveryRoundFacts facts, CancellationToken cancellationToken);
    ValueTask DrainAsync();
}

/// <summary>One operation's raw SDK facts and native-selected dispatch columns.</summary>
public sealed unsafe partial class MetricDiscoveryRoundFacts
{
    internal MetricDiscoveryRuntimePlan Native;
    private int closed, frozen, used;
    private MetricDiscoverySourceRoleView roles;
    private MetricDiscoveryInventoryInputView? inventory;
    internal MetricDiscoveryRoundFacts(MetricDiscoveryRuntimePlan plan) => Native = plan;
    internal void SetInputViews(MetricDiscoverySourceRoleView sourceRoles, MetricDiscoveryInventoryInput input)
    { roles = sourceRoles; if (input.Header != null) inventory = new(this, input); }
    public uint SourceRoleFlags(int sourceRow) { SourceRow(sourceRow); return roles.Flags[sourceRow]; }
    public MetricDiscoveryInventoryInputView? GpuInventory { get { Sampling(); return inventory; } }
    public int SourceCount { get { Sampling(); return checked((int)Native.Sources.Count); } }
    public MetricDiscoveryDatasetMask RequestedDatasets { get { Sampling(); return Native.Selection.DatasetMask; } }
    public ulong SourceId(int sourceRow) { SourceRow(sourceRow); return Native.Sources.SourceIds[sourceRow]; }
    public ulong Observation(int sourceRow) { SourceRow(sourceRow); return Native.Sources.Observations[sourceRow]; }
    public uint FieldCode(int row) { Row(row); return Native.Dispatch.FieldCodes[row]; }
    public uint ScopeIndex(int row) { Row(row); return Native.Dispatch.ScopeIndices[row]; }
    public uint ReadingAvailability(int row) { Row(row); return Native.Selection.Availability[Native.Raw.Observations.RuleIndices[row]]; }
    public ReadOnlySpan<byte> FieldName(int row) { Row(row); return MetricDiscoveryBorrowed.Utf8(Native.Dispatch.Text, Native.Dispatch.FieldNames[row]); }
    public ReadOnlySpan<byte> DeviceKey(int row) { Row(row); return MetricDiscoveryBorrowed.Utf8(Native.Dispatch.Text, Native.Dispatch.DeviceKeys[row]); }
    public void CompleteSource(int sourceRow, uint rawSourceStatus, bool applyCooldown, long completedMs)
    {
        SourceRow(sourceRow);
        Native.Sources.Statuses[sourceRow] = rawSourceStatus;
        Native.Sources.ApplyCooldown[sourceRow] = applyCooldown ? 1U : 0U;
        Native.Sources.CompletedMs[sourceRow] = completedMs;
    }
    public void DeclareSupported(int row) { Row(row); Native.Readings.Supported[row] = 1; }
    public void DeclareQuantity(int row, ReadOnlySpan<byte> utf8) { Row(row); Native.Readings.Quantities[row] = Store(utf8); }
    public void DeclareIntegratedGpu(int row, ReadOnlySpan<byte> utf8)
    {
        Row(row); Native.Readings.IntegratedGpu = Store(utf8);
        Native.Readings.IntegratedRule = Native.Raw.Observations.RuleIndices[row];
    }
    private MetricDiscoveryRef Store(ReadOnlySpan<byte> utf8)
    {
        var end = Interlocked.Add(ref used, utf8.Length);
        if (end < utf8.Length || (ulong)end > Native.Readings.Text.Length)
            throw new MetricOwnerException(new() { Code = MetricOwnerCode.PayloadCapacity, Family = MetricOwnerFamily.Payload,
                Required = checked((ulong)Math.Max(end, 0)), Limit = Native.Readings.Text.Length });
        utf8.CopyTo(new Span<byte>(Native.Readings.Text.Bytes + end - utf8.Length, utf8.Length));
        return new() { Offset = checked((uint)(end - utf8.Length)), Length = checked((uint)utf8.Length) };
    }
    internal void Prepare(nint context)
    {
        Open(); var readings = Native.Readings; readings.Text.Length = checked((uint)used);
        MetricOwnerException.Check(MetricOwnerDiscoveryAbi.RuntimePrepare(context, Native.Operation, in readings, in Native.Sources, out var failure), failure);
    }
    internal void Close() { Volatile.Write(ref closed, 1); inventory?.Close(); CloseVectorInputs(); OnClose(); Native = default; roles = default; }
    partial void OnClose();
    internal void Freeze() { Open(); ReleaseSdkGrouping(); Volatile.Write(ref frozen, 1); }
    private void Open() => ObjectDisposedException.ThrowIf(Volatile.Read(ref closed) != 0, this);
    internal void Sampling()
    {
        Open();
        if (Volatile.Read(ref frozen) != 0) throw new InvalidOperationException("Discovery SDK views expire before native preparation.");
    }
    private void Row(int row) { Sampling(); if ((uint)row >= Native.Raw.Count) throw new ArgumentOutOfRangeException(nameof(row)); }
    private void SourceRow(int row) { Sampling(); if ((uint)row >= Native.Sources.Count) throw new ArgumentOutOfRangeException(nameof(row)); }
}

public sealed partial class ZigHardwareMetricRun
{
    // Non-owning actual-run-tail address only. Never an acquired lease or pin.
    private nint discoveryContext;

    public static async ValueTask<MetricDiscoveryMeasurement> MeasureDiscoveryRolesAsync(IMetricOwnerMemoryResource workspace,
        MetricDiscoveryRoleCatalogFill fill, ulong learnedTextBytes, ulong canonicalReserve = 0, ulong inputBytes = 0,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace); ArgumentNullException.ThrowIfNull(fill);
        MetricDiscoveryMeasurement result = default;
        await workspace.UseAsync((memory, token) =>
        {
            token.ThrowIfCancellationRequested(); result = MeasureRoles(fill, memory, learnedTextBytes, canonicalReserve, inputBytes);
            return ValueTask.CompletedTask;
        }, cancellationToken).ConfigureAwait(false);
        return result;
    }
    private static unsafe MetricDiscoveryMeasurement MeasureRoles(MetricDiscoveryRoleCatalogFill fill, MetricOwnerMemory memory,
        ulong learnedTextBytes, ulong canonicalReserve, ulong inputBytes)
    {
        var invoked = false; var acceptingInputs = true; var borrowed = memory; MetricDiscoveryMeasurement measured = default;
        try
        {
            fill(memory, (in MetricDiscoveryFacts facts, in MetricDiscoveryLimits limits, in MetricDiscoveryRoles roles, ulong prefix) =>
            {
                if (!acceptingInputs || invoked) throw new InvalidOperationException("Discovery role measurement callback expired or repeated."); invoked = true;
                MetricOwnerException.Check(MetricOwnerDiscoveryAbi.RuntimeMeasureRoles(in facts, in limits, in roles, (byte*)borrowed.Address,
                    borrowed.Length, prefix, learnedTextBytes, canonicalReserve, inputBytes, out measured, out var failure), failure);
            });
            if (!invoked) throw new InvalidOperationException("Discovery measurement callback supplied no facts.");
            return measured;
        }
        finally { acceptingInputs = false; borrowed = default; }
    }
    public unsafe void AttachDiscoveryRoles(MetricDiscoveryRuntimeSizes sizes, ReadOnlySpan<MetricDiscoverySourceRole> sourceRoles,
        MetricDiscoveryInventoryBinding? inventory, ulong transientUnavailableMs)
    {
        gate.Wait();
        try
        {
            RequireAccepting();
            if (discoveryContext != 0) throw new InvalidOperationException("Discovery is already attached to this run.");
            fixed (MetricDiscoverySourceRole* rows = sourceRoles)
            {
                var roles = new MetricDiscoveryRoles { Count = checked((uint)sourceRoles.Length), Rows = rows };
                var binding = inventory.GetValueOrDefault();
                MetricOwnerException.Check(MetricOwnerDiscoveryAbi.RuntimeAttachRoles(owner, in sizes, in roles, inventory.HasValue ? &binding : null,
                    transientUnavailableMs, out discoveryContext, out var failure), failure);
            }
        }
        finally { gate.Release(); }
    }

    /// <summary>One initial local work use; returns only scalar extents/declarations.</summary>
    public static async ValueTask<MetricDiscoveryMeasurement> MeasureDiscoveryAsync(IMetricOwnerMemoryResource workspace,
        MetricDiscoveryCatalogFill fill, ulong learnedTextBytes, ulong canonicalReserve = 0, ulong inputBytes = 0,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace); ArgumentNullException.ThrowIfNull(fill);
        MetricDiscoveryMeasurement result = default;
        await workspace.UseAsync((memory, token) =>
        {
            token.ThrowIfCancellationRequested(); result = MeasureDiscovery(fill, memory, learnedTextBytes, canonicalReserve, inputBytes);
            return ValueTask.CompletedTask;
        }, cancellationToken).ConfigureAwait(false);
        return result;
    }
    private static unsafe MetricDiscoveryMeasurement MeasureDiscovery(MetricDiscoveryCatalogFill fill, MetricOwnerMemory memory,
        ulong learnedTextBytes, ulong canonicalReserve, ulong inputBytes)
    {
        var invoked = false; var acceptingInputs = true; var borrowed = memory; MetricDiscoveryMeasurement measured = default;
        try
        {
            fill(memory, (in MetricDiscoveryFacts facts, in MetricDiscoveryLimits limits, MetricDiscoveryProviderInputs* initial, ulong prefix) =>
            {
                if (!acceptingInputs || invoked) throw new InvalidOperationException("Discovery measurement callback expired or repeated."); invoked = true;
                MetricOwnerException.Check(MetricOwnerDiscoveryAbi.RuntimeMeasure(in facts, in limits, (byte*)borrowed.Address,
                    borrowed.Length, prefix, learnedTextBytes, canonicalReserve, inputBytes, out measured, out var failure), failure);
            });
            if (!invoked) throw new InvalidOperationException("Discovery measurement callback supplied no facts.");
            return measured;
        }
        finally { acceptingInputs = false; borrowed = default; }
    }

    /// <summary>Parent invokes after Bind inside its existing active UseAsync.</summary>
    public void AttachDiscovery(MetricDiscoveryRuntimeSizes sizes, ulong transientUnavailableMs)
    {
        gate.Wait();
        try
        {
            RequireAccepting();
            if (discoveryContext != 0) throw new InvalidOperationException("Discovery is already attached to this run.");
            MetricOwnerException.Check(MetricOwnerDiscoveryAbi.RuntimeAttach(owner, in sizes, transientUnavailableMs, out discoveryContext, out var failure), failure);
        }
        finally { gate.Release(); }
    }
    // Parent CloseAsync calls this inside its gate, before UnbindAt and before
    // active end. Exchanging first clears the managed pointer on every exit.
    private void CloseDiscovery()
    {
        var context = Interlocked.Exchange(ref discoveryContext, 0);
        if (context != 0) MetricOwnerDiscoveryAbi.RuntimeClose(context);
    }
    /// <summary>Explicit close for a caller-driven run; parent CloseAsync uses the gated hook above.</summary>
    public void CloseDiscoveryContext()
    {
        gate.Wait(); try { CloseDiscovery(); } finally { gate.Release(); }
    }
    private nint RequireDiscovery()
    {
        RequireAccepting();
        if (discoveryContext == 0) throw new InvalidOperationException("This run has no admitted native discovery tail.");
        return discoveryContext;
    }
    public async ValueTask ReplaceDiscoveryCatalogAsync(MetricDiscoveryCatalogFill fill, long utcMs,
        CancellationToken cancellationToken = default, MetricDiscoveryCatalogBinding? catalog = null)
    {
        ArgumentNullException.ThrowIfNull(fill); await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var context = RequireDiscovery();
            await work.UseAsync((memory, token) =>
            {
                token.ThrowIfCancellationRequested();
                try { ReplaceDiscovery(context, fill, memory, catalog, utcMs); }
                finally { MetricOwnerDiscoveryAbi.RuntimeDetach(context); }
                return ValueTask.CompletedTask;
            }, cancellationToken).ConfigureAwait(false);
            failures.Set(MetricOwnerOperation.Catalog, default);
        }
        catch (MetricOwnerException error) { failures.Set(MetricOwnerOperation.Catalog, error.Failure); throw; }
        finally { gate.Release(); }
    }
    private static unsafe void ReplaceDiscovery(nint context, MetricDiscoveryCatalogFill fill, MetricOwnerMemory memory,
        MetricDiscoveryCatalogBinding? catalog, long utcMs)
    {
        var invoked = false; var acceptingInputs = true; var binding = catalog.GetValueOrDefault();
        var hasCatalog = catalog.HasValue; var borrowedMemory = memory;
        try
        {
            fill(memory, (in MetricDiscoveryFacts facts, in MetricDiscoveryLimits limits, MetricDiscoveryProviderInputs* initial, ulong inputBytes) =>
            {
                if (!acceptingInputs || invoked) throw new InvalidOperationException("Discovery input callback expired or repeated."); invoked = true;
                var bindingLocal = binding;
                MetricOwnerException.Check(MetricOwnerDiscoveryAbi.RuntimeReplace(context, in facts, in limits, initial,
                    hasCatalog ? &bindingLocal : null, (byte*)borrowedMemory.Address, borrowedMemory.Length, inputBytes, utcMs, out var failure), failure);
            });
            if (!invoked) throw new InvalidOperationException("Discovery input callback supplied no facts.");
        }
        finally { acceptingInputs = false; borrowedMemory = default; binding = default; }
    }
    public async ValueTask<bool> ExecuteDiscoveryAsync(MetricDiscoveryRequestFill fill, IMetricOwnerDiscoverySampler sampler,
        MetricOwnerPublicationPermit permit, long utcMs, CancellationToken cancellationToken = default,
        MetricOwnerPublished? onPublished = null, MetricOwnerHistoryBinding? history = null,
        ReadOnlyMemory<MetricOwnerHistoryBinding> historyGroups = default, MetricDiscoveryVectorPublished? onVectorPublished = null,
        ReadOnlyMemory<MetricOwnerMixedHistoryBinding> mixedHistoryGroups = default)
    {
        ArgumentNullException.ThrowIfNull(fill); ArgumentNullException.ThrowIfNull(sampler); ArgumentNullException.ThrowIfNull(permit);
        if (history.HasValue && !historyGroups.IsEmpty) throw new ArgumentException("Supply one history binding form.");
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false); var published = false;
        try
        {
            var context = RequireDiscovery();
            await work.UseAsync(async (memory, token) =>
            {
                MetricOwnerRawRound? query = null; MetricDiscoveryRoundFacts? facts = null;
                var drained = false;
                try
                {
                    facts = PlanDiscovery(context, fill, memory, utcMs);
                    query = new(facts.Native.Raw);
                    await sampler.SampleAsync(query, facts, token).ConfigureAwait(false);
                    try { await sampler.DrainAsync().ConfigureAwait(false); }
                    finally { drained = true; }
                    query.Freeze(); facts.Freeze();
                    token.ThrowIfCancellationRequested(); facts.Prepare(context);
                    var vectorPublication = BorrowVectorPublication(context, facts);
                    var receipt = BorrowReceipt(owner, query.Token);
                    var acceptingPublish = true; var committed = false;
                    try
                    {
                        var admitted = permit(() =>
                        {
                            if (!acceptingPublish || committed) throw new InvalidOperationException("Discovery publication permission expired or repeated.");
                            token.ThrowIfCancellationRequested(); RequireAccepting();
                            CommitDiscovery(context, facts, history, historyGroups.Span, mixedHistoryGroups.Span, utcMs); committed = true;
                        });
                        if (admitted != committed) throw new InvalidOperationException("Publication permission returned inconsistent admission.");
                    }
                    finally { acceptingPublish = false; }
                    published = committed;
                    if (committed && onPublished is not null) Notify(onPublished, query, receipt);
                    if (committed && vectorPublication != 0 && onVectorPublished is not null) NotifyVector(onVectorPublished, query, vectorPublication);
                }
                finally
                {
                    try { if (!drained) await sampler.DrainAsync().ConfigureAwait(false); }
                    finally
                    {
                        try
                        {
                            if (facts is not null) MetricOwnerDiscoveryAbi.RuntimeDetachOperation(context, facts.Native.Operation);
                            else MetricOwnerDiscoveryAbi.RuntimeDetach(context);
                        }
                        finally { facts?.Close(); query?.Close(); }
                    }
                }
            }, cancellationToken).ConfigureAwait(false);
            failures.Set(MetricOwnerOperation.Round, default); return published;
        }
        catch (MetricOwnerException error) { failures.Set(MetricOwnerOperation.Round, error.Failure); throw; }
        finally { gate.Release(); }
    }
    private static unsafe MetricDiscoveryRoundFacts PlanDiscovery(nint context, MetricDiscoveryRequestFill fill,
        MetricOwnerMemory memory, long utcMs)
    {
        MetricDiscoveryRoundFacts? facts = null; var invoked = false; var acceptingInputs = true; var borrowedMemory = memory;
        try
        {
            fill(memory, (in MetricDiscoveryRequest request, ulong inputBytes) =>
            {
                if (!acceptingInputs || invoked) throw new InvalidOperationException("Discovery request callback expired or repeated."); invoked = true;
                MetricOwnerException.Check(MetricOwnerDiscoveryAbi.RuntimePlan(context, in request, (byte*)borrowedMemory.Address,
                    borrowedMemory.Length, inputBytes, utcMs, out var plan, out var failure), failure);
                facts = new(plan);
                MetricOwnerException.Check(MetricOwnerDiscoveryAbi.RuntimeSourceRolesView(context, plan.Operation, out var roles, out failure), failure);
                MetricOwnerException.Check(MetricOwnerDiscoveryAbi.RuntimeInventoryView(context, plan.Operation, out var inventory, out failure), failure);
                facts.SetInputViews(roles, inventory);
                MetricOwnerException.Check(MetricOwnerDiscoveryAbi.RuntimeSdkDispatch(context, plan.Operation, out var grouping, out failure), failure);
                facts.AttachSdkGrouping(in grouping);
                MetricOwnerException.Check(MetricOwnerDiscoveryAbi.RuntimeVectorInputs(context, plan.Operation, out var vectorInputs, out failure), failure);
                facts.AttachVectorInputs(vectorInputs);
            });
            return facts ?? throw new InvalidOperationException("Discovery request callback supplied no request.");
        }
        finally { acceptingInputs = false; borrowedMemory = default; }
    }
    private static unsafe void CommitDiscovery(nint context, MetricDiscoveryRoundFacts facts, MetricOwnerHistoryBinding? history,
        ReadOnlySpan<MetricOwnerHistoryBinding> historyGroups, ReadOnlySpan<MetricOwnerMixedHistoryBinding> mixed, long utcMs)
    {
        if (!mixed.IsEmpty)
        {
            var one = history.GetValueOrDefault();
            fixed (MetricOwnerMixedHistoryBinding* mixedBindings = mixed)
            fixed (MetricOwnerHistoryBinding* scalarGroups = historyGroups)
                MetricOwnerException.Check(MetricOwnerDiscoveryAbi.RuntimeCommitMixed(context, facts.Native.Operation,
                    history.HasValue ? &one : scalarGroups, history.HasValue ? 1U : checked((uint)historyGroups.Length),
                    mixedBindings, checked((uint)mixed.Length), utcMs, out var mixedFailure), mixedFailure);
            return;
        }
        if (!historyGroups.IsEmpty)
        {
            fixed (MetricOwnerHistoryBinding* bindings = historyGroups)
                MetricOwnerException.Check(MetricOwnerDiscoveryAbi.RuntimeCommitGrouped(context, facts.Native.Operation, bindings,
                    checked((uint)historyGroups.Length), utcMs, out var groupedFailure), groupedFailure);
            return;
        }
        var binding = history.GetValueOrDefault();
        MetricOwnerException.Check(MetricOwnerDiscoveryAbi.RuntimeCommit(context, facts.Native.Operation,
            history.HasValue ? &binding : null, utcMs, out var failure), failure);
    }
    private static nint BorrowVectorPublication(nint context, MetricDiscoveryRoundFacts facts)
    {
        MetricOwnerException.Check(MetricOwnerDiscoveryAbi.RuntimeVectorPublication(context, facts.Native.Operation, out var pointer, out var failure), failure);
        return pointer;
    }
    private static void NotifyVector(MetricDiscoveryVectorPublished callback, MetricOwnerRawRound query, nint pointer)
    { var publication = new MetricDiscoveryVectorPublication(query, pointer); callback(in publication); }
}
