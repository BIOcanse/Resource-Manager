using System.Diagnostics;

namespace ResourceManager.NativeUi;

internal sealed record NativeUiShutdownActions(
    Action MarkExiting,
    Action CancelBackgroundWork,
    Action HideTray,
    Action StopAndCloseMainWindow,
    Action ExitMessageLoop)
{
    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(MarkExiting);
        ArgumentNullException.ThrowIfNull(CancelBackgroundWork);
        ArgumentNullException.ThrowIfNull(HideTray);
        ArgumentNullException.ThrowIfNull(StopAndCloseMainWindow);
        ArgumentNullException.ThrowIfNull(ExitMessageLoop);
    }
}

internal sealed class NativeUiShutdownCoordinator
{
    private int shutdownStarted;

    public bool TryShutdown(NativeUiShutdownActions actions)
    {
        ArgumentNullException.ThrowIfNull(actions);
        actions.Validate();
        if (Interlocked.Exchange(ref shutdownStarted, 1) != 0)
        {
            return false;
        }

        Run(actions.MarkExiting);
        Run(actions.CancelBackgroundWork);
        Run(actions.HideTray);
        Run(actions.StopAndCloseMainWindow);
        Run(actions.ExitMessageLoop);
        return true;
    }

    private static void Run(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Trace.WriteLine(ex);
        }
    }
}
