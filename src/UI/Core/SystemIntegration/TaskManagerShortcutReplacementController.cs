namespace ResourceManager.NativeUi.SystemIntegration;

/// <summary>
/// The IFEO half of the Task Manager replacement: when the page switches the replacement on or off, the window
/// process (it runs elevated) registers or removes the resident as taskmgr.exe's Debugger, so a cold Task Manager
/// launch opens Resource Manager. The Ctrl+Shift+Esc hook is the resident's TaskManagerReplacement block.
/// The Debugger command names the resident with <c>--show-existing</c>; old NativeUi entries still hand off
/// to the resident (<see cref="ResidentHandoff"/>). Deletion condition of that compatibility: see
/// <see cref="LoginStartupRegistration"/>.
/// </summary>
internal sealed class TaskManagerShortcutReplacementController
{
    private readonly TaskManagerLaunchReplacementRegistry launchReplacementRegistry = new();
    private readonly string debuggerCommand = CreateDebuggerCommand();

    internal TaskManagerLaunchReplacementResult Apply(bool enabled)
    {
        var result = enabled
            ? launchReplacementRegistry.EnsureRegistered(debuggerCommand)
            : launchReplacementRegistry.RemoveIfOwned();
        if (result.State is not (TaskManagerLaunchReplacementState.Registered or TaskManagerLaunchReplacementState.Removed))
            System.Diagnostics.Trace.TraceWarning("Task Manager launch replacement: {0}.", result.State);
        return result;
    }

    private static string CreateDebuggerCommand()
    {
        var executablePath = ResourceManager.Shared.Packages.ProductDesktopLayout.ResidentBesideWindow(AppContext.BaseDirectory);
        return $"\"{executablePath}\" --show-existing {TaskManagerLaunchReplacementRegistry.OwnedCommandMarker}";
    }
}
