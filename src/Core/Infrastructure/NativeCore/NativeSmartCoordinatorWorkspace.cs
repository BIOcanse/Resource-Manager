using ResourceManager.App.Infrastructure.Adaptation;
using ResourceManager.Ledger;

namespace ResourceManager.App.Infrastructure.NativeCore;

// A smart round holds three regions, each sized from that round's actual rows
// and returned when its operation ends: facts from EnsureRows, the plan after
// native measures the facts, feedback after the plan reports its actions.
internal sealed class NativeSmartCoordinatorWorkspace : IDisposable
{
    private readonly NativeSmartCoordinatorSession session;
    private readonly RowSizedComputeScratch facts;
    private readonly RowSizedComputeScratch plan;
    private readonly RowSizedComputeScratch feedback;
    private NativeSmartFactsLayout? factsLayout;
    private NativeSmartPlanLayout? planLayout;
    private NativeSmartFeedbackLayout? feedbackLayout;
    // The operation's fact lease; plan and feedback leases exist only after Plan opens them.
    private ComputeScratchLease? activeLease;
    private ComputeScratchLease? planLease;
    private ComputeScratchLease? feedbackLease;
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
        facts = new RowSizedComputeScratch(createScratch, "smart-coordinator");
        plan = new RowSizedComputeScratch(createScratch, "smart-coordinator-plan");
        feedback = new RowSizedComputeScratch(createScratch, "smart-coordinator-feedback");
        try
        {
            facts.DescribeNative(session.ReadWorkspaceCapacity, session.SetGrowthObserver);
        }
        catch (Exception failure)
        {
            try { facts.Dispose(); }
            catch (Exception cleanup) { throw new AggregateException(failure, cleanup); }
            finally { session.Dispose(); }
            throw;
        }
    }

    public NativeSmartCoordinatorCapacity Capacity { get; private set; }

    public NativeSmartCoordinatorSnapshot Snapshot => snapshot;

    // Each layout exists only while its region is open in the current round.
    internal NativeSmartFactsLayout? FactsLayout => factsLayout;

    internal NativeSmartPlanLayout? PlanLayout => planLayout;

    internal NativeSmartFeedbackLayout? FeedbackLayout => feedbackLayout;

    // Opens the fact region for this round's rows; the operation returns it.
    internal void EnsureRows(uint inputRows)
    {
        RequireNoOutstandingOperation();
        if (inputRows > Capacity.InputRowCapacity)
            throw new InvalidDataException("The smart coordinator fact count exceeds its configured input limit.");
        var inputs = Math.Max(1U, inputRows);
        var next = new NativeSmartFactsLayout(inputs, session.MeasureTableCount(inputs));
        factsLayout = null;
        if (facts.Length != next.Length) facts.Release();
        facts.Ensure(next.Length, $"inputs={inputs}, measure={next.MeasureTableCount}");
        factsLayout = next;
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
        var lease = RequireOperation();
        return new(lease, factsLayout!, planLease, planLayout, feedbackLease, feedbackLayout);
    }

    private ComputeScratchLease RequireOperation()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (activeLease is null || endUsePending)
            throw new InvalidOperationException("Smart buffers require the current operation lease.");
        return activeLease;
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
            if (owner.factsLayout is null)
                throw new InvalidOperationException("A smart operation needs EnsureRows for its round.");
            this.owner = owner;
            lease = owner.facts.Acquire();
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
                    if (complete)
                    {
                        owner.feedbackLease?.Complete();
                        owner.planLease?.Complete();
                        lease.Complete();
                    }
                }
                owner.EndLeases(); // Failure keeps the exact leases for explicit close.
                owner.activeLease = null;
                owner.endUsePending = false;
                owner.ReleaseRegions();
            }
        }
    }

    // Ends the round's leases from the last opened to the first; a lease whose
    // end fails stays referenced for the explicit close in Dispose.
    private void EndLeases()
    {
        if (feedbackLease is not null)
        {
            feedbackLease.Dispose();
            feedbackLease = null;
        }
        if (planLease is not null)
        {
            planLease.Dispose();
            planLease = null;
        }
        activeLease?.Dispose();
    }

    // A round keeps nothing: the next EnsureRows and Plan open exactly their rows.
    private void ReleaseRegions()
    {
        feedbackLayout = null;
        feedback.Release();
        planLayout = null;
        plan.Release();
        factsLayout = null;
        facts.Release();
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

    // input.ActionCapacity is the caller's ceiling. Native first measures the
    // facts; the plan region then holds only the candidate rows it reported,
    // within that ceiling, and feedback holds only the actions the plan issued.
    public NativeSmartCoordinatorStatus Plan(in NativeSmartCoordinatorCycleInput input)
    {
        RequireOperation();
        snapshot = default;
        if (input.InputCount > factsLayout!.InputCapacity || input.ActionCapacity > Capacity.ActionCapacity)
        {
            OpenFeedbackRegion(0);
            return NativeSmartCoordinatorStatus.BufferTooSmall;
        }

        var inputCount = checked((int)input.InputCount);
        NativeSmartCoordinatorStatus status;
        NativeSmartCoordinatorScratchRequirements staging;
        uint actionRows;
        using (var view = CreateScratchView())
            status = session.MeasurePlan(in input, view.Inputs[..inputCount], view.MeasureTable, out staging, out actionRows);
        if (status != NativeSmartCoordinatorStatus.Ok)
        {
            OpenFeedbackRegion(0); // A failed plan issued no actions to answer.
            return status;
        }

        var rows = Math.Min(input.ActionCapacity, Math.Max(1U, actionRows));
        OpenPlanRegion(rows, in staging);
        var cycle = input;
        cycle.ActionCapacity = rows;
        using (var view = CreateScratchView())
            status = session.Plan(in cycle, view.Inputs[..inputCount], view.Actions, view.NativeScratchPointer,
                in staging, ref snapshot);
        OpenFeedbackRegion(status == NativeSmartCoordinatorStatus.Ok
            ? Math.Min(snapshot.ActionCount, Capacity.FeedbackCapacity)
            : 0);
        return status;
    }

    // A later plan in the same operation keeps the plan region only when its
    // shape is exactly the one this plan measured; any other shape is returned
    // and the measured one opened.
    private void OpenPlanRegion(uint rows, in NativeSmartCoordinatorScratchRequirements staging)
    {
        if (planLayout is { } current && current.ActionCapacity == rows &&
            current.NativeScratchByteCount == staging.ByteCount && current.NativeScratchAlignment == staging.Alignment)
            return;
        if (planLease is not null)
        {
            planLease.Dispose();
            planLease = null;
        }
        planLayout = null;
        plan.Release();
        var next = new NativeSmartPlanLayout(rows, staging.ByteCount, staging.Alignment);
        plan.Ensure(next.Length,
            $"actions={rows}, processes={staging.ProcessCount}, software={staging.SoftwareCount}, gpus={staging.GpuCount}");
        planLease = plan.Acquire();
        planLayout = next;
    }

    // Feedback holds one row per issued action; a plan without actions holds none.
    private void OpenFeedbackRegion(uint rows)
    {
        if ((uint)(feedbackLayout?.FeedbackCapacity ?? 0) == rows) return;
        if (feedbackLease is not null)
        {
            feedbackLease.Dispose();
            feedbackLease = null;
        }
        feedbackLayout = null;
        feedback.Release();
        if (rows == 0) return;
        var next = new NativeSmartFeedbackLayout(rows);
        feedback.Ensure(next.Length, $"feedback={rows}");
        feedbackLease = feedback.Acquire();
        feedbackLayout = next;
    }

    public NativeSmartCoordinatorStatus ApplyFeedback(uint feedbackCount)
    {
        using var view = CreateScratchView();
        if (feedbackCount > (feedbackLayout?.FeedbackCapacity ?? 0))
        {
            snapshot = default;
            return NativeSmartCoordinatorStatus.BufferTooSmall;
        }

        snapshot = default;
        return session.ApplyFeedback(
            feedbackCount == 0 ? ReadOnlySpan<NativeSmartCoordinatorFeedback>.Empty : view.Feedback[..checked((int)feedbackCount)],
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
            EndLeases(); // Explicit close may retry an already ended call's endUse.
            activeLease = null;
            endUsePending = false;
        }
        feedback.Dispose();
        plan.Dispose();
        facts.Dispose(); // InUse/error retains the region handle and native session.
        session.Dispose();
        disposed = true;
    }
}
