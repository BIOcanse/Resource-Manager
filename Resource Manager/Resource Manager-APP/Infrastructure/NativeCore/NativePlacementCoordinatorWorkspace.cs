namespace ResourceManager.App.Infrastructure.NativeCore;

internal sealed class NativePlacementCoordinatorWorkspace
{
    public NativePlacementCoordinatorWorkspace(
        in NativePlacementCoordinatorConfiguration configuration,
        in NativePlacementCoordinatorCapacity capacity)
    {
        if (capacity.StateCapacity != configuration.MaximumStateCount ||
            capacity.ActionCapacity != configuration.MaximumActionCount ||
            capacity.SnapshotStateCapacity != configuration.MaximumStateCount)
        {
            throw new InvalidOperationException("Placement coordinator workspace capacity mismatch.");
        }

        Desired = new NativePlacementDesiredInput[configuration.MaximumDesiredCount];
        Applied = new NativePlacementAppliedInput[configuration.MaximumAppliedCount];
        Actions = new NativePlacementAction[capacity.ActionCapacity];
        Feedback = new NativePlacementFeedback[capacity.ActionCapacity];
        States = new NativePlacementState[capacity.SnapshotStateCapacity];
    }

    public NativePlacementDesiredInput[] Desired { get; }

    public NativePlacementAppliedInput[] Applied { get; }

    public NativePlacementAction[] Actions { get; }

    public NativePlacementFeedback[] Feedback { get; }

    public NativePlacementState[] States { get; }
}
