namespace ResourceManager.App.Infrastructure.NativeCore;

internal sealed class NativeSmartCoordinatorWorkspace : IDisposable
{
    private readonly NativeSmartCoordinatorSession session;
    private readonly NativeSmartCoordinatorInputRow[] inputRows;
    private readonly NativeSmartCoordinatorAction[] actions;
    private readonly NativeSmartCoordinatorFeedback[] feedbackRows;
    private readonly NativeSmartCoordinatorSnapshotRow[] snapshotRows;
    private NativeSmartCoordinatorSnapshot snapshot;

    public NativeSmartCoordinatorWorkspace(
        in NativeSmartCoordinatorConfiguration configuration)
    {
        session = new NativeSmartCoordinatorSession(in configuration);
        Capacity = session.Capacity;
        inputRows = new NativeSmartCoordinatorInputRow[Capacity.InputRowCapacity];
        actions = new NativeSmartCoordinatorAction[Capacity.ActionCapacity];
        feedbackRows = new NativeSmartCoordinatorFeedback[Capacity.FeedbackCapacity];
        snapshotRows = new NativeSmartCoordinatorSnapshotRow[Capacity.SnapshotRowCapacity];
    }

    public NativeSmartCoordinatorCapacity Capacity { get; private set; }

    public NativeSmartCoordinatorSnapshot Snapshot => snapshot;

    public Span<NativeSmartCoordinatorInputRow> InputRows => inputRows;

    public Span<NativeSmartCoordinatorFeedback> FeedbackRows => feedbackRows;

    public ReadOnlySpan<NativeSmartCoordinatorAction> PlannedActions
        => actions.AsSpan(0, checked((int)snapshot.ActionCount));

    public uint PlannedActionCount => snapshot.ActionCount;

    public NativeSmartCoordinatorAction GetPlannedAction(uint index)
    {
        if (index >= snapshot.ActionCount)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        return actions[checked((int)index)];
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
        if (input.InputCount > Capacity.InputRowCapacity)
        {
            snapshot = default;
            return NativeSmartCoordinatorStatus.BufferTooSmall;
        }

        snapshot = default;
        return session.Plan(
            in input,
            inputRows.AsSpan(0, checked((int)input.InputCount)),
            actions,
            ref snapshot);
    }

    public NativeSmartCoordinatorStatus ApplyFeedback(uint feedbackCount)
    {
        if (feedbackCount > Capacity.FeedbackCapacity)
        {
            snapshot = default;
            return NativeSmartCoordinatorStatus.BufferTooSmall;
        }

        snapshot = default;
        return session.ApplyFeedback(
            feedbackRows.AsSpan(0, checked((int)feedbackCount)),
            ref snapshot);
    }

    public NativeSmartCoordinatorStatus RefreshSnapshot()
    {
        snapshot = default;
        return session.GetSnapshot(ref snapshot, snapshotRows);
    }

    public NativeSmartCoordinatorStatus Reconfigure(
        in NativeSmartCoordinatorConfiguration configuration)
    {
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
        snapshot = default;
        return session.Reset();
    }

    public void Dispose() => session.Dispose();
}
