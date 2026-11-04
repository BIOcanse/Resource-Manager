using System.Runtime.CompilerServices;
using ResourceManager.App.Infrastructure.Adaptation;
using ResourceManager.Ledger;

namespace ResourceManager.App.Infrastructure.NativeCore;

// A placement round holds two regions, each sized from that round's actual rows
// and returned when its operation ends: desired and applied rows from
// BeginOperation, actions and their feedback after native counts the actions.
internal sealed class NativePlacementCoordinatorWorkspace : IDisposable
{
    private readonly RowSizedComputeScratch rows;
    private readonly RowSizedComputeScratch actions;
    private readonly NativePlacementCoordinatorConfiguration configuration;
    private NativePlacementScratchLayout? layout;
    private NativePlacementActionLayout? actionLayout;
    // The operation's row lease; the action lease exists only after Plan opens it.
    private ComputeScratchLease? activeLease;
    private ComputeScratchLease? actionLease;
    private bool endUsePending;
    private bool retiring;
    private bool disposed;

    public NativePlacementCoordinatorWorkspace(
        in NativePlacementCoordinatorConfiguration configuration,
        in NativePlacementCoordinatorCapacity capacity,
        Func<ulong, IComputeScratch> createScratch)
    {
        ArgumentNullException.ThrowIfNull(createScratch);
        if (capacity.StateCapacity != configuration.MaximumStateCount ||
            capacity.ActionCapacity != configuration.MaximumActionCount ||
            capacity.SnapshotStateCapacity != configuration.MaximumStateCount)
        {
            throw new InvalidOperationException("Placement coordinator workspace capacity mismatch.");
        }

        this.configuration = configuration;
        rows = new(createScratch, "placement");
        actions = new(createScratch, "placement-actions");
    }

    // Each layout exists only while its region is open in the current round.
    internal NativePlacementScratchLayout? Layout => layout;

    internal NativePlacementActionLayout? ActionLayout => actionLayout;

    // Action rows of this round's plan; zero before Plan or when it issued none.
    internal int ActionCapacity => actionLayout?.ActionCapacity ?? 0;

    internal void RequireNoOutstandingOperation()
    {
        ObjectDisposedException.ThrowIf(disposed || retiring, this);
        if (activeLease is not null)
            throw new InvalidOperationException("The previous placement scratch use has not ended.");
    }

    // Opens the row region for exactly this round's desired and applied rows;
    // the operation returns it.
    internal Operation BeginOperation(uint desiredCount = 1, uint appliedCount = 0)
    {
        RequireNoOutstandingOperation();
        if (desiredCount > configuration.MaximumDesiredCount || appliedCount > configuration.MaximumAppliedCount)
            throw new InvalidOperationException("Placement row count exceeds the configured upper bound.");
        // A region is never empty: a round without rows still holds one desired row.
        var next = new NativePlacementScratchLayout(desiredCount == 0 && appliedCount == 0 ? 1U : desiredCount,
            appliedCount);
        layout = null;
        if (rows.Length != next.Length) rows.Release();
        rows.Ensure(next.Length, $"desired={next.DesiredCapacity}, applied={next.AppliedCapacity}");
        layout = next;
        return new(this);
    }

    internal NativePlacementScratchView CreateScratchView()
    {
        var lease = RequireOperation();
        return new(lease, layout!, actionLease, actionLayout);
    }

    private ComputeScratchLease RequireOperation()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (activeLease is null || endUsePending)
            throw new InvalidOperationException("Placement buffers require the current operation lease.");
        return activeLease;
    }

    internal NativePlacementAction GetAction(int index)
    {
        using var view = CreateScratchView();
        return view.Actions[index];
    }

    internal void SetFeedback(int index, NativePlacementFeedback feedback)
    {
        using var view = CreateScratchView();
        view.Feedback[index] = feedback;
    }

    // The workspace sizes the actions; the caller's cycle.ActionCapacity is not
    // read. Native counts this cycle's actions before it changes any slot: the
    // first call offers no action rows, and a cycle that issues actions runs again
    // with exactly the rows it reported. cycle returns the envelope of the call
    // whose status is returned.
    internal NativePlacementCoordinatorStatus Plan(NativePlacementCoordinatorSession session,
        ref NativePlacementCoordinatorCycleInput cycle)
    {
        RequireOperation();
        // A second plan in one operation first returns the previous plan's region.
        EndActionLease();
        ReleaseActionRegion();
        var desiredCount = checked((int)cycle.DesiredCount);
        var appliedCount = checked((int)cycle.AppliedCount);
        var counted = cycle;
        counted.ActionCapacity = 0;
        NativePlacementCoordinatorStatus status;
        using (var view = CreateScratchView())
            status = session.Plan(ref counted, view.Desired[..desiredCount], view.Applied[..appliedCount], view.Actions);
        if (status != NativePlacementCoordinatorStatus.BufferTooSmall || counted.ActionCount == 0 ||
            counted.ActionCount > configuration.MaximumActionCount)
        {
            cycle = counted;
            return status;
        }

        var next = new NativePlacementActionLayout(counted.ActionCount);
        actions.Ensure(next.Length, $"actions={next.ActionCapacity}");
        actionLease = actions.Acquire();
        actionLayout = next;
        var planned = cycle;
        planned.ActionCapacity = counted.ActionCount;
        using (var view = CreateScratchView())
            status = session.Plan(ref planned, view.Desired[..desiredCount], view.Applied[..appliedCount], view.Actions);
        cycle = planned;
        return status;
    }

    internal NativePlacementCoordinatorStatus ApplyFeedback(NativePlacementCoordinatorSession session, int count)
    {
        using var view = CreateScratchView();
        return session.ApplyFeedback(view.Feedback[..count]);
    }

    internal sealed class Operation : IDisposable
    {
        private readonly NativePlacementCoordinatorWorkspace owner;
        private readonly ComputeScratchLease lease;
        private bool complete;
        private bool ended;

        internal Operation(NativePlacementCoordinatorWorkspace owner)
        {
            owner.RequireNoOutstandingOperation();
            this.owner = owner;
            lease = owner.rows.Acquire();
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
            ended = true;
            if (!owner.endUsePending)
            {
                owner.endUsePending = true;
                if (complete)
                {
                    owner.actionLease?.Complete();
                    lease.Complete();
                }
            }
            owner.EndActionLease();
            lease.Dispose(); // A failure retains the exact leases for explicit close.
            owner.activeLease = null;
            owner.endUsePending = false;
            owner.ReleaseRegions();
        }
    }

    private void EndActionLease()
    {
        if (actionLease is null) return;
        actionLease.Dispose();
        actionLease = null;
    }

    // A round keeps nothing: the next BeginOperation and Plan open exactly their rows.
    private void ReleaseRegions()
    {
        ReleaseActionRegion();
        layout = null;
        rows.Release();
    }

    private void ReleaseActionRegion()
    {
        actionLayout = null;
        actions.Release();
    }

    public void Dispose()
    {
        if (disposed) return;
        retiring = true;
        if (activeLease is not null)
        {
            if (!endUsePending)
                throw new LedgerException(LedgerStatus.InUse, "close active placement operation");
            EndActionLease();
            activeLease.Dispose();
            activeLease = null;
            endUsePending = false;
        }
        actions.Dispose();
        rows.Dispose();
        disposed = true;
    }
}

// The desired and applied rows one round declared.
internal sealed class NativePlacementScratchLayout
{
    internal int DesiredCapacity { get; }
    internal int AppliedCapacity { get; }
    internal ulong Desired { get; }
    internal ulong Applied { get; }
    internal ulong Length { get; }

    internal NativePlacementScratchLayout(uint desired, uint applied)
    {
        if (desired == 0 && applied == 0)
            throw new ArgumentOutOfRangeException(nameof(desired), "A placement row region needs at least one row.");
        DesiredCapacity = checked((int)desired);
        AppliedCapacity = checked((int)applied);
        ulong end = 0;
        ulong Column<T>(int count) where T : unmanaged
        {
            var start = checked((end + 7) / 8 * 8);
            end = checked(start + (ulong)Unsafe.SizeOf<T>() * (ulong)count);
            return start;
        }
        Desired = Column<NativePlacementDesiredInput>(DesiredCapacity);
        Applied = Column<NativePlacementAppliedInput>(AppliedCapacity);
        Length = checked((end + 7) / 8 * 8);
    }
}

// The actions one plan issued and one feedback row per action.
internal sealed class NativePlacementActionLayout
{
    internal int ActionCapacity { get; }
    internal ulong Actions { get; }
    internal ulong Feedback { get; }
    internal ulong Length { get; }

    internal NativePlacementActionLayout(uint actions)
    {
        ArgumentOutOfRangeException.ThrowIfZero(actions);
        ActionCapacity = checked((int)actions);
        ulong end = 0;
        ulong Column<T>(int count) where T : unmanaged
        {
            var start = checked((end + 7) / 8 * 8);
            end = checked(start + (ulong)Unsafe.SizeOf<T>() * (ulong)count);
            return start;
        }
        Actions = Column<NativePlacementAction>(ActionCapacity);
        Feedback = Column<NativePlacementFeedback>(ActionCapacity);
        Length = checked((end + 7) / 8 * 8);
    }
}

// A synchronous pin inside the whole operation's use/endUse lifetime. A plan
// that has not opened action rows has empty action and feedback columns.
internal ref struct NativePlacementScratchView
{
    private ComputeScratchView view;
    private ComputeScratchView actionView;
    private readonly NativePlacementScratchLayout layout;
    private readonly NativePlacementActionLayout? actionLayout;

    internal NativePlacementScratchView(ComputeScratchLease lease, NativePlacementScratchLayout layout,
        ComputeScratchLease? actionLease, NativePlacementActionLayout? actionLayout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        if (lease.Length != layout.Length || (actionLease is null) != (actionLayout is null) ||
            (actionLease is not null && actionLease.Length != actionLayout!.Length))
            throw new InvalidOperationException("The placement scratch layout does not match its backing region.");
        this.layout = layout;
        this.actionLayout = actionLayout;
        view = new ComputeScratchView(lease);
        if (actionLease is null) return;
        try
        {
            actionView = new ComputeScratchView(actionLease);
        }
        catch
        {
            view.Dispose();
            throw;
        }
    }

    internal Span<NativePlacementDesiredInput> Desired => view.Column<NativePlacementDesiredInput>(layout.Desired, layout.DesiredCapacity);
    internal Span<NativePlacementAppliedInput> Applied => view.Column<NativePlacementAppliedInput>(layout.Applied, layout.AppliedCapacity);
    internal Span<NativePlacementAction> Actions => actionLayout is null
        ? Span<NativePlacementAction>.Empty
        : actionView.Column<NativePlacementAction>(actionLayout.Actions, actionLayout.ActionCapacity);
    internal Span<NativePlacementFeedback> Feedback => actionLayout is null
        ? Span<NativePlacementFeedback>.Empty
        : actionView.Column<NativePlacementFeedback>(actionLayout.Feedback, actionLayout.ActionCapacity);
    public void Dispose()
    {
        actionView.Dispose();
        view.Dispose();
    }
}
