using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Optimization;

namespace Resource_Manager_APP.Tests;

public sealed partial class HostManagerSmartCoordinatorScoreOnlyCompositionTests
{
    private static CapturingPlacementProbe CapturePlacementOperations(HostManagerSmartCoordinator coordinator)
    {
        var capture = new CapturingPlacementProbe();
        coordinator.TransitionProbe = capture;
        return capture;
    }

    private sealed class CapturingPlacementProbe
        : IHostManagerSmartCoordinatorTransitionProbe
    {
        internal NativePlacementDesiredInput[] Desired { get; private set; } = [];
        internal NativePlacementAction[] Actions { get; private set; } = [];
        internal NativePlacementFeedback[] Feedback { get; private set; } = [];
        internal int PlanCalls { get; private set; }

        public void ObservePlacementPlan(ReadOnlySpan<NativePlacementDesiredInput> desired,
            ReadOnlySpan<NativePlacementAppliedInput> applied, ReadOnlySpan<NativePlacementAction> actions)
        {
            PlanCalls++;
            Desired = desired.ToArray();
            Actions = actions.ToArray();
            Feedback = [];
        }

        public void ObservePlacementFeedback(ReadOnlySpan<NativePlacementFeedback> feedback)
            => Feedback = feedback.ToArray();

        public void Reach(HostManagerSmartCoordinatorTransitionPoint point) { }
        public void ObserveShutdown(HostManagerSmartCoordinatorShutdownPoint point) { }
    }
}
