using ResourceManager.NativeUi;

namespace Resource_Manager_APP.Tests;

public sealed class NativeUiShutdownCoordinatorTests
{
    [Fact]
    public void Shutdown_ExecutesExactOrderOnce()
    {
        var calls = new List<string>();
        var coordinator = new NativeUiShutdownCoordinator();
        var actions = CreateActions(calls);

        Assert.True(coordinator.TryShutdown(actions));
        Assert.False(coordinator.TryShutdown(actions));

        Assert.Equal(
            [
                "mark-exiting",
                "cancel-background",
                "hide-tray",
                "stop-window",
                "exit-loop"
            ],
            calls);
    }

    [Fact]
    public void Shutdown_ActionFailureDoesNotSkipRemainingCleanup()
    {
        var calls = new List<string>();
        var coordinator = new NativeUiShutdownCoordinator();
        var actions = new NativeUiShutdownActions(
            MarkExiting: () => calls.Add("mark-exiting"),
            CancelBackgroundWork: () => calls.Add("cancel-background"),
            HideTray: () => throw new InvalidOperationException("tray failure"),
            StopAndCloseMainWindow: () => calls.Add("stop-window"),
            ExitMessageLoop: () => calls.Add("exit-loop"));

        Assert.True(coordinator.TryShutdown(actions));
        Assert.Equal(
            [
                "mark-exiting",
                "cancel-background",
                "stop-window",
                "exit-loop"
            ],
            calls);
    }

    private static NativeUiShutdownActions CreateActions(ICollection<string> calls) =>
        new(
            MarkExiting: () => calls.Add("mark-exiting"),
            CancelBackgroundWork: () => calls.Add("cancel-background"),
            HideTray: () => calls.Add("hide-tray"),
            StopAndCloseMainWindow: () => calls.Add("stop-window"),
            ExitMessageLoop: () => calls.Add("exit-loop"));
}
