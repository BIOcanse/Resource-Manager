using ResourceManager.App.Infrastructure.Adaptation;
using ResourceManager.Ledger;

namespace ResourceManager.App.Infrastructure.NativeCore;

internal sealed class NativeSmartCoordinatorWorkspace : IDisposable
{
    private readonly NativeSmartCoordinatorSession session;
    private readonly RowSizedComputeScratch scratch;
    private NativeSmartScratchLayout layout;
    private ComputeScratchLease? activeLease;
    private bool endUsePending;
    private bool retiring;
    private bool disposed;
    private NativeSmartCoordinatorSnapshotRow[] snapshotRows = [];
    private NativeSmartCoordinatorSnapshot snapshot;

    public NativeSmartCoordinatorWorkspace(
        in NativeSmartCoordinatorConfiguration configuration,
        Func<ulong, IComputeScratch> createScratch)
    {
        ArgumentNullException.ThrowIfNull(createScratch);
        session = new NativeSmartCoordinatorSession(in configuration);
        Capacity = session.Capacity;
        scratch = new RowSizedComputeScratch(createScratch, "smart-coordinator");
        try
        {
            var nativeScratch = session.ScratchRequirementsForRows(1);
            layout = new(1, 1, 1,
                nativeScratch.ByteCount, nativeScratch.Alignment);
            scratch.Ensure(layout.Length, "inputs=1, actions=1, feedback=1");
            scratch.DescribeNative(session.ReadWorkspaceCapacity, session.SetGrowthObserver);
        }
        catch (Exception failure)
        {
            try { scratch.Dispose(); }
            catch (Exception cleanup) { throw new AggregateException(failure, cleanup); }
            finally { session.Dispose(); }
            throw;
        }
    }

    public NativeSmartCoordinatorCapacity Capacity { get; private set; }

    public NativeSmartCoordinatorSnapshot Snapshot => snapshot;

    internal NativeSmartScratchLayout ScratchLayout => layout;

    internal void EnsureRows(uint inputRows)
    {
        RequireNoOutstandingOperation();
        if (inputRows > Capacity.InputRowCapacity)
            throw new InvalidDataException("The smart coordinator fact count exceeds its configured input limit.");
        var inputs = Math.Max((uint)layout.InputCapacity, Math.Max(1U, inputRows));
        // Every retained or newly observed process/software can produce one candidate,
        // even when this cycle permits fewer actual policy changes.
        var actions = Math.Max((uint)layout.ActionCapacity, checked((uint)Math.Min(Capacity.ActionCapacity,
            (ulong)snapshot.SnapshotRowCount + 2UL * inputRows)));
        var feedback = Math.Max((uint)layout.FeedbackCapacity, Math.Min(Capacity.FeedbackCapacity, actions));
        if (inputs == layout.InputCapacity && actions == layout.ActionCapacity && feedback == layout.FeedbackCapacity &&
            scratch.Length == layout.Length) return;
        var nativeScratch = session.ScratchRequirementsForRows(inputs);
        var next = new NativeSmartScratchLayout(inputs, actions, feedback, nativeScratch.ByteCount, nativeScratch.Alignment);
        scratch.Ensure(next.Length, $"inputs={inputs}, actions={actions}, feedback={feedback}");
        layout = next;
    }

    internal void RequireNoOutstandingOperation()
    {
        ObjectDisposedException.ThrowIf(disposed || retiring, this);
        if (activeLease is not null)
            throw new InvalidOperationException("The previous smart scratch use has not ended.");
    }

    internal Operation BeginOperation()
        => new(this);

    internal NativeSmartScratchView CreateScratchView()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (activeLease is null || endUsePending)
            throw new InvalidOperationException("Smart buffers require the current operation lease.");
        return new(activeLease, layout);
    }

    internal sealed class Operation : IDisposable
    {
        private readonly NativeSmartCoordinatorWorkspace owner;
        private readonly ComputeScratchLease lease;
        private bool complete;
        private bool ended;

        internal Operation(NativeSmartCoordinatorWorkspace owner)
        {
            owner.RequireNoOutstandingOperation();
            this.owner = owner;
            lease = owner.scratch.Acquire();
            owner.activeLease = lease;
        }

        internal void Complete()
        {
            ObjectDisposedException.ThrowIf(ended || owner.endUsePending || !ReferenceEquals(owner.activeLease, lease), this);
            complete = true;
        }

        public void Dispose()
        {
            if (ended) return;
            // The token gets one end attempt. A failed end stays on the workspace;
            // a surrounding using/finally must not silently retry it.
            ended = true;
            if (ReferenceEquals(owner.activeLease, lease))
            {
                if (!owner.endUsePending)
                {
                    owner.endUsePending = true;
                    if (complete) lease.Complete();
                }
                lease.Dispose(); // Failure keeps the exact lease for explicit close.
                owner.activeLease = null;
                owner.endUsePending = false;
            }
        }
    }

    public uint PlannedActionCount => snapshot.ActionCount;

    public NativeSmartCoordinatorAction GetPlannedAction(uint index)
    {
        if (index >= snapshot.ActionCount)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        using var view = CreateScratchView();
        return view.Actions[checked((int)index)];
    }

    internal void SetFeedback(uint index, NativeSmartCoordinatorFeedback feedback)
    {
        using var view = CreateScratchView();
        view.Feedback[checked((int)index)] = feedback;
    }

    internal NativeSmartCoordinatorFeedback GetFeedback(uint index)
    {
        using var view = CreateScratchView();
        return view.Feedback[checked((int)index)];
    }

    public ReadOnlySpan<NativeSmartCoordinatorSnapshotRow> CurrentSnapshotRows
        => snapshotRows.AsSpan(0, checked((int)snapshot.SnapshotRowCount));

    public NativeSmartCoordinatorSnapshotRow GetSnapshotRow(uint index)
    {
        if (index >= snapshot.SnapshotRowCount)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        return snapshotRows[checked((int)index)];
    }

    public NativeSmartCoordinatorStatus Plan(in NativeSmartCoordinatorCycleInput input)
    {
        using var view = CreateScratchView();
        if (input.InputCount > layout.InputCapacity || input.ActionCapacity > layout.ActionCapacity)
        {
            snapshot = default;
            return NativeSmartCoordinatorStatus.BufferTooSmall;
        }

        snapshot = default;
        return session.Plan(
            in input,
            view.Inputs[..checked((int)input.InputCount)],
            view.Actions,
            view.NativeScratchPointer,
            view.NativeScratchLength,
            ref snapshot);
    }

    public NativeSmartCoordinatorStatus ApplyFeedback(uint feedbackCount)
    {
        using var view = CreateScratchView();
        if (feedbackCount > layout.FeedbackCapacity)
        {
            snapshot = default;
            return NativeSmartCoordinatorStatus.BufferTooSmall;
        }

        snapshot = default;
        return session.ApplyFeedback(
            view.Feedback[..checked((int)feedbackCount)],
            ref snapshot);
    }

    public NativeSmartCoordinatorStatus RefreshSnapshot()
    {
        RequireSession();
        EnsureSnapshotRows(snapshot.SnapshotRowCount);
        snapshot = default;
        var status = session.GetSnapshot(ref snapshot, snapshotRows);
        if (status == NativeSmartCoordinatorStatus.BufferTooSmall && snapshot.SnapshotRowCount <= Capacity.SnapshotRowCapacity)
        {
            EnsureSnapshotRows(snapshot.SnapshotRowCount);
            status = session.GetSnapshot(ref snapshot, snapshotRows);
        }
        return status;
    }

    private void EnsureSnapshotRows(uint rows)
    {
        if (rows <= snapshotRows.Length) return;
        var previous = snapshotRows.Length;
        snapshotRows = new NativeSmartCoordinatorSnapshotRow[rows];
        System.Diagnostics.Trace.TraceInformation(
            "Workspace smart-coordinator snapshot grew rows {0} -> {1}: actual output exceeded high water.", previous, rows);
    }

    public NativeSmartCoordinatorStatus Reconfigure(
        in NativeSmartCoordinatorConfiguration configuration)
    {
        RequireNoOutstandingOperation();
        var result = session.Reconfigure(in configuration);
        if (result == NativeSmartCoordinatorStatus.Ok)
        {
            Capacity = session.Capacity;
            snapshot = default;
        }
        return result;
    }

    public NativeSmartCoordinatorStatus Reset()
    {
        RequireSession();
        snapshot = default;
        return session.Reset();
    }

    private void RequireSession()
        => ObjectDisposedException.ThrowIf(disposed || endUsePending || (retiring && activeLease is null), this);

    public void Dispose()
    {
        if (disposed) return;
        retiring = true;
        if (activeLease is not null)
        {
            if (!endUsePending)
                throw new LedgerException(LedgerStatus.InUse, "close active smart operation");
            activeLease.Dispose(); // Explicit close may retry an already ended call's endUse.
            activeLease = null;
            endUsePending = false;
        }
        scratch.Dispose(); // InUse/error retains the region handle and native session.
        session.Dispose();
        disposed = true;
    }
}
