using System.Runtime.CompilerServices;
using ResourceManager.App.Infrastructure.Adaptation;
using ResourceManager.Ledger;

namespace ResourceManager.App.Infrastructure.NativeCore;

internal sealed class NativePlacementCoordinatorWorkspace : IDisposable
{
    private readonly RowSizedComputeScratch scratch;
    private NativePlacementScratchLayout layout;
    private readonly NativePlacementCoordinatorConfiguration configuration;
    private ComputeScratchLease? activeLease;
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
        layout = new(1, 1, 1);
        scratch = new(createScratch, "placement");
        scratch.Ensure(layout.Length, "desired=1, applied=1, actions=1");
    }

    internal int ActionCapacity => layout.ActionCapacity;

    internal void RequireNoOutstandingOperation()
    {
        ObjectDisposedException.ThrowIf(disposed || retiring, this);
        if (activeLease is not null)
            throw new InvalidOperationException("The previous placement scratch use has not ended.");
    }

    internal Operation BeginOperation(uint desiredCount = 1, uint appliedCount = 0)
    {
        RequireNoOutstandingOperation();
        if (desiredCount > configuration.MaximumDesiredCount || appliedCount > configuration.MaximumAppliedCount)
            throw new InvalidOperationException("Placement row count exceeds the configured upper bound.");
        // Each identity can issue at most one action; existing unseen identities
        // cannot issue an action. The sum bounds the desired/applied union.
        var actions = Math.Max(1U, Math.Min(configuration.MaximumActionCount, checked(desiredCount + appliedCount)));
        if (scratch.Length != layout.Length || desiredCount > layout.DesiredCapacity || appliedCount > layout.AppliedCapacity || actions > layout.ActionCapacity)
        {
            var next = new NativePlacementScratchLayout(Math.Max((uint)layout.DesiredCapacity, desiredCount),
                Math.Max((uint)layout.AppliedCapacity, appliedCount), Math.Max((uint)layout.ActionCapacity, actions));
            scratch.Ensure(next.Length, $"desired={next.DesiredCapacity}, applied={next.AppliedCapacity}, actions={next.ActionCapacity}");
            layout = next;
        }
        return new(this);
    }

    internal NativePlacementScratchView CreateScratchView()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (activeLease is null || endUsePending)
            throw new InvalidOperationException("Placement buffers require the current operation lease.");
        return new(activeLease, layout);
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

    internal NativePlacementCoordinatorStatus Plan(NativePlacementCoordinatorSession session,
        ref NativePlacementCoordinatorCycleInput cycle)
    {
        using var view = CreateScratchView();
        return session.Plan(ref cycle, view.Desired[..checked((int)cycle.DesiredCount)],
            view.Applied[..checked((int)cycle.AppliedCount)], view.Actions);
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
            ended = true;
            if (!owner.endUsePending)
            {
                owner.endUsePending = true;
                if (complete) lease.Complete();
            }
            lease.Dispose(); // A failure retains this exact lease for explicit close.
            owner.activeLease = null;
            owner.endUsePending = false;
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        retiring = true;
        if (activeLease is not null)
        {
            if (!endUsePending)
                throw new LedgerException(LedgerStatus.InUse, "close active placement operation");
            activeLease.Dispose();
            activeLease = null;
            endUsePending = false;
        }
        scratch.Dispose();
        disposed = true;
    }
}

internal sealed class NativePlacementScratchLayout
{
    internal int DesiredCapacity { get; }
    internal int AppliedCapacity { get; }
    internal int ActionCapacity { get; }
    internal ulong Desired { get; }
    internal ulong Applied { get; }
    internal ulong Actions { get; }
    internal ulong Feedback { get; }
    internal ulong Length { get; }

    internal NativePlacementScratchLayout(uint desired, uint applied, uint actions)
    {
        ArgumentOutOfRangeException.ThrowIfZero(desired);
        ArgumentOutOfRangeException.ThrowIfZero(applied);
        ArgumentOutOfRangeException.ThrowIfZero(actions);
        DesiredCapacity = checked((int)desired);
        AppliedCapacity = checked((int)applied);
        ActionCapacity = checked((int)actions);
        ulong end = 0;
        ulong Column<T>(int count) where T : unmanaged
        {
            var start = checked((end + 7) / 8 * 8);
            end = checked(start + (ulong)Unsafe.SizeOf<T>() * (ulong)count);
            return start;
        }
        Desired = Column<NativePlacementDesiredInput>(DesiredCapacity);
        Applied = Column<NativePlacementAppliedInput>(AppliedCapacity);
        Actions = Column<NativePlacementAction>(ActionCapacity);
        Feedback = Column<NativePlacementFeedback>(ActionCapacity);
        Length = checked((end + 7) / 8 * 8);
    }
}

// A synchronous pin inside the whole operation's use/endUse lifetime.
internal ref struct NativePlacementScratchView
{
    private ComputeScratchView view;
    private readonly NativePlacementScratchLayout layout;

    internal NativePlacementScratchView(ComputeScratchLease lease, NativePlacementScratchLayout layout)
    {
        if (lease.Length != layout.Length)
            throw new InvalidOperationException("The placement scratch layout does not match its backing region.");
        this.layout = layout;
        view = new ComputeScratchView(lease);
    }

    internal Span<NativePlacementDesiredInput> Desired => view.Column<NativePlacementDesiredInput>(layout.Desired, layout.DesiredCapacity);
    internal Span<NativePlacementAppliedInput> Applied => view.Column<NativePlacementAppliedInput>(layout.Applied, layout.AppliedCapacity);
    internal Span<NativePlacementAction> Actions => view.Column<NativePlacementAction>(layout.Actions, layout.ActionCapacity);
    internal Span<NativePlacementFeedback> Feedback => view.Column<NativePlacementFeedback>(layout.Feedback, layout.ActionCapacity);
    public void Dispose() => view.Dispose();
}
